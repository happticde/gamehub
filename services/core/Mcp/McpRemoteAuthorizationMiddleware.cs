using System.Collections.Immutable;
using System.Data;
using GameHub.Core.Data;
using GameHub.Core.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace GameHub.Core.Mcp;

/// <summary>Bounds the native authorization and private browser-continuation requests.</summary>
internal sealed class McpRemoteAuthorizationMiddleware(RequestDelegate next, ILogger<McpRemoteAuthorizationMiddleware> logger)
{
    private const int MaximumBody = 4096;
    private static readonly HashSet<string> StartFields = [Parameters.ClientId, Parameters.ResponseType, Parameters.ResponseMode,
        Parameters.RedirectUri, Parameters.Resource, Parameters.Scope, Parameters.State, Parameters.CodeChallenge,
        Parameters.CodeChallengeMethod, Parameters.Prompt];
    private static readonly HashSet<string> ConsentFields = [Parameters.ClientId, Parameters.RequestUri,
        "action", "confirmation", "allow_tracking_write"];

    public async Task InvokeAsync(HttpContext http, McpProtocolBudget budget, McpRemoteResource resource)
    {
        var path = http.Request.Path.Value ?? "";
        if (!http.Request.Path.StartsWithSegments(McpRemoteResource.AuthorizationPath, StringComparison.OrdinalIgnoreCase))
        { await next(http); return; }
        if (path is not (McpRemoteResource.AuthorizationPath or McpRemoteResource.ConsentPath))
        { http.Response.StatusCode = 404; return; }
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
        var start = path == McpRemoteResource.AuthorizationPath;
        if (!start && !http.RequestServices.GetRequiredService<McpDeviceSettings>().IsTrustedBff(http))
        { http.Response.StatusCode = 404; return; }
        var expected = resource.Uri;
        if (!http.Request.IsHttps || !string.Equals(http.Request.Host.Host, expected.IdnHost, StringComparison.OrdinalIgnoreCase)
            || (http.Request.Host.Port ?? 443) != expected.Port || http.Request.Headers.ContainsKey("Origin"))
        { await Error(http, 400, Errors.InvalidRequest); return; }
        using var lease = budget.AcquireConcurrency();
        if (!lease.IsAcquired || !budget.Admit(path))
        {
            http.Response.Headers.RetryAfter = "60";
            await Error(http, 429, Errors.TemporarilyUnavailable);
            return;
        }

        var output = http.Response.Body;
        await using var buffer = new MemoryStream();
        http.Response.Body = buffer;
        try
        {
            if (start) await StartAsync(http, resource);
            else if (await ReadConsentAsync(http)) await next(http);
            var reply = http.Features.Get<McpRemoteReply>();
            if (reply is null || reply.Status != http.Response.StatusCode)
            {
                // Only the native request receipt, committed callback, or safe preview may leave Core.
                http.Response.Headers.Remove("Location");
                http.Response.Headers.Remove("Content-Type");
                http.Response.Headers.Remove("Content-Length");
                buffer.SetLength(0);
                await Error(http, http.Response.StatusCode is >= 400 and < 600 ? http.Response.StatusCode : 400,
                    Errors.InvalidRequest);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning("Remote authorization unavailable ({FailureType}).", exception.GetType().Name);
            http.Response.Headers.Remove("Location");
            http.Response.Headers.Remove("Content-Length");
            buffer.SetLength(0);
            await Error(http, 503, Errors.TemporarilyUnavailable);
        }
        finally { http.Response.Body = output; }
        buffer.Position = 0;
        await buffer.CopyToAsync(output, http.RequestAborted);
    }

    private async Task StartAsync(HttpContext http, McpRemoteResource resource)
    {
        if (!HttpMethods.IsGet(http.Request.Method) || http.Request.QueryString.Value is not { Length: <= MaximumBody }) return;
        var values = http.Request.Query;
        if (!ValidFields(values, StartFields, 1024)
            || McpRemoteClient.Find(values[Parameters.ClientId]) is not { } client
            || !client.AllowsRedirect(values[Parameters.RedirectUri], client.RedirectUris)
            || values[Parameters.Resource] != resource.Uri.AbsoluteUri
            || values[Parameters.ResponseType] != ResponseTypes.Code
            || (values.ContainsKey(Parameters.ResponseMode) && values[Parameters.ResponseMode] != ResponseModes.Query)
            || (values.ContainsKey(Parameters.Prompt) && values[Parameters.Prompt] != PromptValues.Consent)
            || values[Parameters.CodeChallengeMethod] != CodeChallengeMethods.Sha256
            || values[Parameters.CodeChallenge].ToString() is not { Length: 43 } challenge
            || !McpRemoteAuthorization.IsBase64Url(challenge) || StringValues.IsNullOrEmpty(values[Parameters.State])
            || !McpDeviceProtocol.AllowedScopes(values[Parameters.Scope].ToString()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries).ToImmutableArray())) return;
        var cancellationToken = http.RequestAborted;
        var metadata = http.RequestServices.GetRequiredService<McpClientMetadata>();
        var redirects = await metadata.GetRedirectsAsync(client, cancellationToken);
        if (redirects is null || !client.AllowsRedirect(values[Parameters.RedirectUri], redirects))
        { http.Response.StatusCode = 403; return; }

        var db = http.RequestServices.GetRequiredService<CoreContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
        var pending = db.Set<McpProtocolToken>().Where(row => row.Type == TokenTypeIdentifiers.Private.RequestToken
            && row.Authorization == null && row.Subject == null);
        var expired = await pending.Where(row => row.ExpirationDate <= now).Select(row => row.Id)
            .Take(128).ToArrayAsync(cancellationToken);
        if (expired.Length != 0)
            await pending.Where(row => expired.Contains(row.Id)).ExecuteDeleteAsync(cancellationToken);
        if (await pending.CountAsync(row => row.ExpirationDate > now, cancellationToken) >= McpDeviceProtocol.MaximumPending)
        { http.Response.StatusCode = 429; return; }
        if (!await McpRemoteAuthorization.EnsureRegistrationAsync(
            http.RequestServices.GetRequiredService<IOpenIddictApplicationManager>(), client, resource, cancellationToken))
        { http.Response.StatusCode = 403; return; }
        await next(http);
        if (http.Features.Get<McpRemoteReply>() is not null)
            await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<bool> ReadConsentAsync(HttpContext http)
    {
        if (!HttpMethods.IsPost(http.Request.Method) || http.Request.QueryString.HasValue || !http.Request.HasFormContentType
            || !http.Request.ContentType!.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
            return false;
        var body = new byte[MaximumBody + 1];
        var length = await http.Request.Body.ReadAtLeastAsync(body, body.Length, false, http.RequestAborted);
        if (length > MaximumBody) { http.Response.StatusCode = 413; return false; }
        try
        {
            using var stream = new MemoryStream(body, 0, length, writable: false);
            var reader = new FormReader(stream) { ValueCountLimit = 5, KeyLengthLimit = 32, ValueLengthLimit = 2048 };
            var form = new FormCollection(await reader.ReadFormAsync(http.RequestAborted));
            var action = form["action"].ToString();
            if (!ValidFields(form, ConsentFields, 2048) || McpRemoteClient.Find(form[Parameters.ClientId]) is null
                || !form[Parameters.RequestUri].ToString().StartsWith(RequestUris.Prefixes.Generic, StringComparison.Ordinal)
                || action is not ("preview" or "approve" or "deny")
                || (action == "preview" ? form.Count != 3 : StringValues.IsNullOrEmpty(form["confirmation"]))
                || (form.ContainsKey("allow_tracking_write")
                    && (action != "approve" || form["allow_tracking_write"].ToString() is not ("true" or "false"))))
                return false;
            http.Request.Form = form;
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or BadHttpRequestException)
        { return false; }
    }

    private static bool ValidFields(IEnumerable<KeyValuePair<string, StringValues>> fields,
        HashSet<string> allowed, int limit) =>
        fields.All(field => allowed.Contains(field.Key) && field.Value is { Count: 1 }
            && field.Value[0] is { Length: > 0 } value && value.Length <= limit);

    private static Task Error(HttpContext http, int status, string error)
    {
        http.Response.StatusCode = status;
        return http.Response.WriteAsJsonAsync(new { error }, http.RequestAborted);
    }
}
