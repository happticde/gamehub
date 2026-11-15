using System.Net;
using System.Text.Json;

namespace GameHub.Mcp.Authentication;

/// <summary>Explicit terminal login only. No protocol body, credential or exception becomes a diagnostic.</summary>
public sealed class McpLoginCommands(McpLoginSettings settings, HttpClient http,
    TextWriter terminal, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    /// <summary>Runs setup, login, status or logout with sanitized output; stdout is never used.</summary>
    public async Task<int> RunAsync(string command, bool requestTrackingWrite = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var selected = await new McpCredentialStore(settings).LockAsync(cancellationToken);
            var current = await selected.ReadAsync(cancellationToken);
            switch (command)
            {
                case "setup":
                    await terminal.WriteLineAsync("Credential storage ready (0700 directories, 0600 files). The volume is not encrypted. Run login in an interactive terminal.");
                    return 0;
                case "status":
                    await terminal.WriteLineAsync(current is null ? "No stored authorization. Run login in an interactive terminal."
                        : "Authorization stored. Credentials are never shown by status.");
                    return 0;
                case "logout":
                    if (current is null) { await terminal.WriteLineAsync("No local authorization to remove."); return 0; }
                    var remote = false;
                    try { remote = await RevokeAsync(current.RefreshToken, cancellationToken); }
                    catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or McpLoginException or JsonException)
                    { /* Local deletion must still run; remote failure is reported explicitly. */ }
                    selected.Delete();
                    await terminal.WriteLineAsync(remote ? "Logout complete. Selected remote connection revoked and local credential removed."
                        : "Local credential removed. Remote revocation could not be confirmed. Revoke this connection in GameHub; it may still be active.");
                    return remote ? 0 : 2;
                case "login":
                    if (current is not null)
                        throw new McpLoginException("This connection already has stored authorization. Use logout before a fresh login, or configure another Mcp__Connection.");
                    await LoginAsync(selected, requestTrackingWrite, cancellationToken);
                    return 0;
                default:
                    throw new McpLoginException("Use setup, login [--tracking-write], status or logout. Credentials and codes are never command arguments.");
            }
        }
        catch (McpLoginException error) { await terminal.WriteLineAsync(error.Message); return 1; }
        catch (OperationCanceledException)
        { await terminal.WriteLineAsync("Login or setup cancelled/timed out. No pending code was stored. Restart login for a new code; if consent was already approved, revoke that connection in GameHub."); return 1; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or HttpRequestException or JsonException)
        { await terminal.WriteLineAsync("Credential storage or HTTPS service is unavailable. Check the volume owner/permissions and configured GameHub HTTPS routing, then retry. No sensitive diagnostic is printed."); return 1; }
    }

    private async Task LoginAsync(McpCredentialStore.LockedCredential selected, bool write, CancellationToken cancellationToken)
    {
        var scope = write ? "library:read tracking:write offline_access" : "library:read offline_access";
        var started = clock.GetUtcNow();
        var device = await PostAsync("connect/device", [new("scope", scope), new("resource", settings.Resource)], cancellationToken);
        if (device.Status != HttpStatusCode.OK || device.Body.TryGetProperty("error", out _))
            throw new McpLoginException("GameHub could not start login. Check protocol activation and HTTPS routing, then retry.");
        var code = String(device.Body, "device_code");
        var humanCode = String(device.Body, "user_code");
        if (!McpCredentialStore.ValidToken(code) || humanCode is not { Length: > 0 and <= 32 }
            || humanCode.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')
            || String(device.Body, "verification_uri") != new Uri(settings.Issuer, "mcp/verify").AbsoluteUri
            || !Integer(device.Body, "expires_in", out var lifetime) || lifetime is < 1 or > 600
            || !Integer(device.Body, "interval", out var interval) || interval is < 5 or > 600)
            throw new McpLoginException("GameHub returned an invalid login response. No code was stored or opened.");
        var deadline = started.AddSeconds(lifetime);
        await terminal.WriteLineAsync("Open " + new Uri(settings.Issuer, "mcp/verify").AbsoluteUri + " in your host browser.");
        await terminal.WriteLineAsync("Enter code: " + humanCode);
        await terminal.WriteLineAsync("Check the instance, account and permissions before approving. If this terminal has no browser, open that URL on another device. Ctrl+C cancels; codes are not saved.");
        while (true)
        {
            if (clock.GetUtcNow().AddSeconds(interval) >= deadline)
                throw new McpLoginException("Login expired. Run login again for a new code.");
            await Task.Delay(TimeSpan.FromSeconds(interval), clock, cancellationToken);
            var requestedAt = clock.GetUtcNow();
            var response = await PostAsync("connect/token", [new("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
                new("device_code", code!), new("resource", settings.Resource)], cancellationToken);
            var error = String(response.Body, "error");
            if (error == "authorization_pending") continue;
            if (error == "slow_down") { interval = Math.Min(600, interval + 5); continue; }
            if (error == "access_denied") throw new McpLoginException("Login denied. No credential was stored. Run login again if access is still wanted.");
            if (error is "expired_token" or "invalid_grant") throw new McpLoginException("Login expired or was already used. Run login again for a new code.");
            if (response.Status != HttpStatusCode.OK || error is not null)
                throw new McpLoginException("Login polling is unavailable. Restart login for a new code; if already approved, revoke the unused connection in GameHub.");
            var access = String(response.Body, "access_token");
            var refresh = String(response.Body, "refresh_token");
            var granted = String(response.Body, "scope");
            if (clock.GetUtcNow() >= deadline || String(response.Body, "token_type") != "Bearer"
                || !McpCredentialStore.ValidToken(access) || !McpCredentialStore.ValidToken(refresh)
                || !McpCredentialStore.ValidScope(granted) || (!write && granted!.Split(' ').Contains("tracking:write"))
                || !Integer(response.Body, "expires_in", out var seconds) || seconds is < 1 or > 600)
                throw new McpLoginException("Invalid credential response. Nothing was stored. Revoke the unused connection in GameHub before retrying.");
            try
            {
                await selected.WriteAsync(new McpCredential
                {
                    Issuer = settings.Issuer.AbsoluteUri, Resource = settings.Resource, Connection = settings.Connection,
                    AccessToken = access!, RefreshToken = refresh!, Scope = granted!,
                    AccessExpiresAt = requestedAt.AddSeconds(seconds)
                }, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException or McpLoginException)
            {
                // A successful but unstored grant is still a server connection. Best-effort cleanup
                // gets its own short budget, independent from a cancelled terminal operation.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { await RevokeAsync(refresh!, cleanup.Token); }
                catch (Exception cleanupError) when (cleanupError is HttpRequestException or IOException or OperationCanceledException or McpLoginException or JsonException) { }
                throw new McpLoginException("Login credential could not be saved. Remote cleanup was attempted; revoke the unused connection in GameHub and repair the volume before retrying.");
            }
            await terminal.WriteLineAsync("Login complete. Authorization saved for this connection.");
            return;
        }
    }

    private async Task<bool> RevokeAsync(string token, CancellationToken cancellationToken)
    {
        var result = await PostAsync("connect/revoke", [new("token", token), new("token_type_hint", "refresh_token")], cancellationToken);
        return result.Status == HttpStatusCode.OK && !result.Body.TryGetProperty("error", out _);
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(string path,
        List<KeyValuePair<string, string>> fields, CancellationToken cancellationToken)
    {
        fields.Add(new("client_id", McpLoginSettings.ClientId));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(settings.Issuer, path))
        { Content = new FormUrlEncodedContent(fields) };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new McpLoginException("GameHub redirected a credential request. Configure direct HTTPS protocol routing; redirects are not followed.");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        var bytes = new byte[8193];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(length), deadline.Token);
            if (read == 0) break;
            length += read;
        }
        if (length > 8192) throw new McpLoginException("GameHub protocol response exceeded its size limit.");
        if (length == 0 && path == "connect/revoke")
            return (response.StatusCode, JsonSerializer.SerializeToElement(new { }));
        using var document = JsonDocument.Parse(bytes.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 4 });
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || document.RootElement.EnumerateObject().Select(property => property.Name).Distinct().Count()
                != document.RootElement.EnumerateObject().Count())
            throw new McpLoginException("GameHub returned an invalid protocol response.");
        return (response.StatusCode, document.RootElement.Clone());
    }

    private static string? String(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static bool Integer(JsonElement value, string name, out int number)
    { number = 0; return value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out number); }
}
