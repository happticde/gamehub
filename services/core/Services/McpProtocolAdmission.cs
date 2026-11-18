using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using GameHub.Core.Data;
using GameHub.Core.Mcp;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace GameHub.Core.Services;

/// <summary>Process-wide anonymous budgets; client IDs and forwarded addresses are never trust.</summary>
public sealed class McpProtocolBudget : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, (long Minute, int Count)> windows = new();
    private readonly ConcurrencyLimiter concurrency = new(new ConcurrencyLimiterOptions { PermitLimit = 16, QueueLimit = 0 });
    /// <summary>Rejects excess work immediately instead of queuing unbounded anonymous requests.</summary>
    public RateLimitLease AcquireConcurrency() => concurrency.AttemptAcquire();
    /// <summary>Fixed global buckets bound code guessing and storage work, including unique guesses.</summary>
    public bool Admit(string path)
    {
        var limit = path == McpDeviceProtocol.TokenPath ? 300 : 30;
        var minute = Environment.TickCount64 / 60000;
        lock (gate)
        {
            var previous = windows.GetValueOrDefault(path);
            var count = previous.Minute == minute ? previous.Count : 0;
            if (count >= limit) return false;
            windows[path] = (minute, count + 1);
            return true;
        }
    }
    /// <inheritdoc/>
    public void Dispose() => concurrency.Dispose();
}

/// <summary>Durable compare-and-swap polling budget shared by replicas and restarts.</summary>
public sealed class McpPollingAdmission(CoreContext db, IOpenIddictTokenManager tokens)
{
    /// <summary>Returns false for a premature poll, increasing its required interval by five seconds.</summary>
    public async Task<bool> AdmitAsync(string code, CancellationToken cancellationToken)
    {
        // The supported manager hashes reference identifiers before lookup. No raw code is stored.
        var entry = await tokens.FindByReferenceIdAsync(code, cancellationToken) as McpProtocolToken;
        if (entry is null || entry.Type != TokenTypeIdentifiers.Private.DeviceCode
            || entry.ExpirationDate <= DateTime.UtcNow
            || entry.Status is not (Statuses.Inactive or Statuses.Valid)) return true;
        var properties = string.IsNullOrEmpty(entry.Properties) ? new JsonObject() : JsonNode.Parse(entry.Properties)!.AsObject();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var interval = properties["gamehub_poll_interval"]?.GetValue<int>() ?? 5;
        var next = properties["gamehub_poll_next"]?.GetValue<long>() ?? 0;
        var allowed = now >= next;
        if (!allowed) interval = Math.Min(McpDeviceProtocol.LifetimeSeconds, interval + 5);
        properties["gamehub_poll_interval"] = interval;
        properties["gamehub_poll_next"] = now + interval * 1000L;
        var changed = await db.Set<McpProtocolToken>()
            .Where(row => row.Id == entry.Id && row.ConcurrencyToken == entry.ConcurrencyToken)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.Properties, properties.ToJsonString())
                .SetProperty(row => row.ConcurrencyToken, Guid.NewGuid().ToString()), cancellationToken);
        if (changed == 1) await db.Entry(entry).ReloadAsync(cancellationToken);
        // Contention is denied conservatively. A competing poll cannot bypass the stored interval.
        return changed == 1 && allowed;
    }
}

/// <summary>Bounds input and database work before OpenIddict extracts or validates protocol fields.</summary>
public sealed class McpProtocolMiddleware(RequestDelegate next)
{
    private const int MaximumBody = 4096;
    private static readonly HashSet<string> DeviceFields = [Parameters.ClientId, Parameters.Scope, Parameters.Resource];
    private static readonly HashSet<string> TokenFields = [Parameters.ClientId, Parameters.Scope, Parameters.Resource,
        Parameters.GrantType, Parameters.DeviceCode, Parameters.RefreshToken];
    private static readonly HashSet<string> RemoteTokenFields = [Parameters.ClientId, Parameters.Scope, Parameters.Resource,
        Parameters.GrantType, Parameters.Code, Parameters.RedirectUri, Parameters.CodeVerifier, Parameters.RefreshToken];
    private static readonly HashSet<string> VerificationFields = [Parameters.UserCode, "action", "confirmation", "allow_tracking_write"];
    private static readonly HashSet<string> RevocationFields = [Parameters.ClientId, Parameters.Token, Parameters.TokenTypeHint];

    /// <summary>Runs before authentication. Errors expose only stable protocol error names.</summary>
    public async Task InvokeAsync(HttpContext http, McpProtocolBudget budget, McpRestResource resource)
    {
        var path = (http.Request.Path.Value ?? "").TrimEnd('/').ToLowerInvariant();
        if (path is not (McpDeviceProtocol.DevicePath or McpDeviceProtocol.TokenPath or McpDeviceProtocol.VerifyPath
            or McpDeviceProtocol.RevocationPath))
        {
            await next(http);
            return;
        }
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";
        var requestCancellation = http.RequestAborted;
        using var revocationDeadline = CancellationTokenSource.CreateLinkedTokenSource(requestCancellation);
        if (path == McpDeviceProtocol.RevocationPath)
        {
            revocationDeadline.CancelAfter(TimeSpan.FromSeconds(15));
            http.RequestAborted = revocationDeadline.Token;
        }
        using var lease = budget.AcquireConcurrency();
        if (!lease.IsAcquired || !budget.Admit(path))
        {
            http.Response.Headers.RetryAfter = "60";
            await Error(http, Errors.TemporarilyUnavailable, 429);
            return;
        }
        // Unconfigured test/staged hosts stay closed. Enabled hosts additionally require the
        // independent BFF transport proof before parsing any code or authenticating a browser.
        var verification = path == McpDeviceProtocol.VerifyPath;
        if (verification && http.RequestServices.GetService<McpDeviceSettings>() is not { Enabled: true })
        {
            http.Response.StatusCode = 403;
            return;
        }
        if (verification && !http.RequestServices.GetRequiredService<McpDeviceSettings>().IsTrustedBff(http))
        { http.Response.StatusCode = 403; return; }
        var expected = resource.Issuer;
        if (!http.Request.IsHttps
            || !string.Equals(http.Request.Host.Host, expected.IdnHost, StringComparison.OrdinalIgnoreCase)
            || (http.Request.Host.Port ?? 443) != expected.Port
            || http.Request.Headers.Origin.Count > 0 || http.Request.QueryString.HasValue
            || !HttpMethods.IsPost(http.Request.Method)
            || !http.Request.HasFormContentType
            || !http.Request.ContentType!.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            await Error(http, Errors.InvalidRequest);
            return;
        }
        if (http.Request.ContentLength > MaximumBody)
        {
            await Error(http, Errors.InvalidRequest, 413);
            return;
        }
        var bodyLimit = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodyLimit is { IsReadOnly: false }) bodyLimit.MaxRequestBodySize = MaximumBody;
        IFormCollection form;
        try
        {
            // Enforce the bound even for chunked streams and TestServer, before allocating form values.
            var bytes = new byte[MaximumBody + 1];
            var length = 0;
            while (length < bytes.Length)
            {
                var count = await http.Request.Body.ReadAsync(bytes.AsMemory(length), http.RequestAborted);
                if (count == 0) break;
                length += count;
            }
            if (length > MaximumBody) { await Error(http, Errors.InvalidRequest, 413); return; }
            using var body = new MemoryStream(bytes, 0, length, writable: false);
            var reader = new FormReader(body) { ValueCountLimit = 8, KeyLengthLimit = 32, ValueLengthLimit = 1024 };
            form = new FormCollection(await reader.ReadFormAsync(http.RequestAborted));
            http.Request.Form = form;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or BadHttpRequestException)
        {
            await Error(http, Errors.InvalidRequest);
            return;
        }
        var client = form[Parameters.ClientId].ToString();
        var local = client == McpDeviceProtocol.ClientId;
        var remote = (path is McpDeviceProtocol.TokenPath or McpDeviceProtocol.RevocationPath)
            && McpRemoteClient.Find(client) is not null && http.RequestServices.GetService<McpRemoteResource>() is not null;
        var allowed = verification ? VerificationFields : path == McpDeviceProtocol.DevicePath ? DeviceFields
            : path == McpDeviceProtocol.RevocationPath ? RevocationFields : remote ? RemoteTokenFields : TokenFields;
        if (form.Any(field => !allowed.Contains(field.Key) || field.Value.Count != 1
                || StringValues.IsNullOrEmpty(field.Value))
            || (!verification && !local && !remote)
            || (verification && (form[Parameters.UserCode].Count != 1 || form[Parameters.UserCode].ToString().Length > 32
                || form["action"].ToString() is not ("preview" or "approve" or "deny")
                || (form["action"] != "preview" && form["confirmation"].Count != 1)
                || (form.ContainsKey("allow_tracking_write") && form["allow_tracking_write"].ToString() is not ("true" or "false")))))
        {
            await Error(http, Errors.InvalidRequest);
            return;
        }

        // Buffer until the pending transaction commits. Aborted serialization cannot leak codes.
        var output = http.Response.Body;
        await using var buffered = new MemoryStream();
        http.Response.Body = buffered;
        try
        {
            var db = http.RequestServices.GetRequiredService<CoreContext>();
            if (verification)
            {
                await next(http);
                if (http.Features.Get<McpConsentPreviewFeature>() is null || http.Response.StatusCode != 200)
                {
                    buffered.SetLength(0);
                    http.Response.Headers.Remove("Content-Type");
                    http.Response.Headers.Remove("Content-Length");
                    http.Response.Headers.Remove("Location");
                    if (http.Response.StatusCode is >= 200 and < 300 && http.Response.StatusCode != 204)
                        http.Response.StatusCode = 400;
                    else if (http.Response.StatusCode is >= 300 and < 400) http.Response.StatusCode = 400;
                }
            }
            else if (path == McpDeviceProtocol.RevocationPath)
            {
                db.Database.SetCommandTimeout(5);
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, http.RequestAborted);
                await next(http);
                if (http.Response.StatusCode == 200)
                {
                    await transaction.CommitAsync(http.RequestAborted);
                    // RFC 7009's success receipt contains no credential or connection metadata.
                    buffered.SetLength(0);
                }
                http.Response.Headers.Remove("Content-Length");
                http.Response.Headers.Remove("Location");
            }
            else if (path == McpDeviceProtocol.DevicePath)
            {
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, http.RequestAborted);
                var now = DateTime.UtcNow;
                // Bound ownerless protocol debris without touching any approved owner's tokens.
                var expired = await db.Set<McpProtocolToken>().Where(row => row.Authorization == null
                    && row.ExpirationDate < now && (row.Type == TokenTypeIdentifiers.Private.DeviceCode
                        || row.Type == TokenTypeIdentifiers.Private.UserCode)).Select(row => row.Id).Take(128).ToArrayAsync(http.RequestAborted);
                if (expired.Length > 0) await db.Set<McpProtocolToken>().Where(row => expired.Contains(row.Id))
                    .ExecuteDeleteAsync(http.RequestAborted);
                var pending = await db.Set<McpProtocolToken>().CountAsync(row => row.Type == TokenTypeIdentifiers.Private.DeviceCode
                    && row.ExpirationDate > now && (row.Status == Statuses.Inactive || row.Status == Statuses.Valid), http.RequestAborted);
                if (pending >= McpDeviceProtocol.MaximumPending) await Error(http, Errors.TemporarilyUnavailable, 429);
                else await next(http);
                await transaction.CommitAsync(http.RequestAborted);
            }
            else
            {
                // Keep poll admission and OpenIddict's single-consumption update in one
                // serializable transaction. A competing poll cannot invalidate its CAS midway.
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, http.RequestAborted);
                var state = new McpTokenTransaction();
                http.Features.Set(state);
                if (form[Parameters.GrantType] == GrantTypes.DeviceCode
                    && !StringValues.IsNullOrEmpty(form[Parameters.DeviceCode])
                    && !await http.RequestServices.GetRequiredService<McpPollingAdmission>()
                        .AdmitAsync(form[Parameters.DeviceCode].ToString(), http.RequestAborted))
                    await Error(http, Errors.SlowDown);
                else
                {
                    // Keep durable polling backoff even on protocol rejection, but roll back
                    // token consumption/creation when later validation or response handling fails.
                    await transaction.CreateSavepointAsync("mcp_issuance", http.RequestAborted);
                    await next(http);
                    using var response = JsonDocument.Parse(buffered.ToArray());
                    var success = http.Response.StatusCode is >= 200 and < 300
                        && !response.RootElement.TryGetProperty("error", out _)
                        && response.RootElement.TryGetProperty("access_token", out var accessToken)
                        && accessToken.ValueKind == JsonValueKind.String
                        && !string.IsNullOrEmpty(accessToken.GetString());
                    if (success && (state.Grant is null
                        || state.Grant.ExpiresAtUtc <= http.RequestServices.GetRequiredService<TimeProvider>()
                            .GetUtcNow().UtcDateTime))
                    {
                        http.Response.StatusCode = 400;
                        success = false;
                    }
                    if (!success && !state.RefreshReuseRevoked)
                    {
                        await transaction.RollbackToSavepointAsync("mcp_issuance", http.RequestAborted);
                        db.ChangeTracker.Clear();
                    }
                    if (!success)
                    {
                        var error = response.RootElement.TryGetProperty("error", out var value)
                            ? value.GetString() ?? Errors.InvalidGrant : Errors.InvalidGrant;
                        var status = http.Response.StatusCode;
                        buffered.SetLength(0);
                        http.Response.Headers.Remove("Content-Length");
                        await Error(http, error, status is >= 400 and < 600 ? status : 400);
                    }
                }
                await transaction.CommitAsync(http.RequestAborted);
            }
        }
        catch (OperationCanceledException) when (path == McpDeviceProtocol.RevocationPath && !requestCancellation.IsCancellationRequested)
        {
            http.RequestServices.GetRequiredService<CoreContext>().ChangeTracker.Clear();
            buffered.SetLength(0);
            http.Response.Headers.Remove("Content-Length");
            http.Response.StatusCode = 503;
        }
        catch (OperationCanceledException)
        {
            http.RequestServices.GetRequiredService<CoreContext>().ChangeTracker.Clear();
            throw;
        }
        catch (Exception exception)
        {
            http.RequestServices.GetRequiredService<ILogger<McpProtocolMiddleware>>()
                .LogWarning("MCP protocol work unavailable ({FailureType}).", exception.GetType().Name);
            buffered.SetLength(0);
            http.RequestServices.GetRequiredService<CoreContext>().ChangeTracker.Clear();
            http.Response.Headers.Remove("Content-Length");
            if (verification) http.Response.StatusCode = 503;
            else await Error(http, Errors.TemporarilyUnavailable, 503);
        }
        finally { http.Response.Body = output; http.RequestAborted = requestCancellation; }
        buffered.Position = 0;
        await buffered.CopyToAsync(output, http.RequestAborted);
    }

    private static async Task Error(HttpContext http, string error, int status = 400)
    {
        http.Response.StatusCode = status;
        await http.Response.WriteAsJsonAsync(new { error }, http.RequestAborted);
    }
}
