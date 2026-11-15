using System.Net;
using System.Text.Json;

namespace GameHub.Mcp.Authentication;

/// <summary>Serializes reread, one-time renewal and atomic replacement across container processes.</summary>
public sealed class McpTokenProvider(McpLoginSettings settings, HttpClient http, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private const string Recovery = "Authorization renewal could not be confirmed. Run logout, then login in an interactive terminal; revoke the connection in GameHub if remote logout cannot be confirmed.";

    /// <summary>Returns a current access credential without retaining a process-local token cache.</summary>
    public async Task<string> GetAccessTokenAsync(bool write, CancellationToken cancellationToken = default)
        => await UseAccessTokenAsync(write, Task.FromResult, cancellationToken);

    /// <summary>Keeps the selected connection locked through an operation, including read/modify/write tools.</summary>
    public async Task<T> UseAccessTokenAsync<T>(bool write, Func<string, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        using var selected = await new McpCredentialStore(settings).LockAsync(cancellationToken);
        if (selected.RequiresRecovery) throw new McpLoginException(Recovery);
        var current = await selected.ReadAsync(cancellationToken)
            ?? throw new McpLoginException("No stored authorization. Run setup and login in an interactive terminal with the selected connection and credential volume.");
        if (write && !current.Scope.Split(' ').Contains("tracking:write"))
            throw new McpLoginException("This connection permits library reads only. Run logout, then login --tracking-write and explicitly approve tracking updates.");
        if (current.AccessExpiresAt > clock.GetUtcNow().AddSeconds(30)) return await operation(current.AccessToken);

        // Once this marker exists even a killed process must never retry the old refresh token.
        selected.BeginRefresh();
        McpCredential replacement;
        try
        {
            var requestedAt = clock.GetUtcNow();
            var response = await new McpProtocolClient(settings, http).PostAsync("connect/token",
                [new("grant_type", "refresh_token"), new("refresh_token", current.RefreshToken),
                 new("resource", settings.Resource), new("scope", current.Scope)], cancellationToken);
            var body = response.Body;
            var access = McpProtocolClient.String(body, "access_token");
            var refresh = McpProtocolClient.String(body, "refresh_token");
            // RFC 6749 section 5.1 permits omission when granted scope equals requested scope.
            var scope = body.TryGetProperty("scope", out _) ? McpProtocolClient.String(body, "scope") : current.Scope;
            if (response.Status != HttpStatusCode.OK || body.TryGetProperty("error", out _)
                || McpProtocolClient.String(body, "token_type") != "Bearer"
                || !McpCredentialStore.ValidToken(access) || !McpCredentialStore.ValidToken(refresh)
                || refresh == current.RefreshToken || access == current.AccessToken
                || !McpCredentialStore.ValidScope(scope)
                || !scope!.Split(' ').ToHashSet(StringComparer.Ordinal).SetEquals(current.Scope.Split(' '))
                || !McpProtocolClient.Integer(body, "expires_in", out var seconds) || seconds is < 1 or > 600
                || requestedAt.AddSeconds(seconds) <= clock.GetUtcNow())
                throw new McpLoginException(Recovery);
            replacement = new McpCredential
            {
                Issuer = current.Issuer, Resource = current.Resource, Connection = current.Connection,
                AccessToken = access!, RefreshToken = refresh!, Scope = scope!,
                AccessExpiresAt = requestedAt.AddSeconds(seconds)
            };
            await selected.WriteAsync(replacement, cancellationToken);
            selected.CompleteRefresh();
        }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException
            or OperationCanceledException or JsonException or McpLoginException)
        {
            // Keep the marker on terminal rejection, network uncertainty, cancellation or save failure.
            // Logout may still present the retained token for best-effort remote revocation.
            throw new McpLoginException(Recovery);
        }
        return await operation(replacement.AccessToken);
    }
}
