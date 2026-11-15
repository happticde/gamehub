using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using GameHub.Core.Data;
using GameHub.Shared.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace GameHub.Core.Services;

/// <summary>Preview and authenticated confirmation bound to the original code and browser family.</summary>
public sealed class McpBrowserConsent(CoreContext db, McpGrantAdmission admission, McpDeviceConsent consent,
    McpProtocolKeys keys, McpRestResource resource, TimeProvider clock)
{
    private readonly IDataProtector protector = keys.Provider.CreateProtector("GameHub.Mcp.BrowserConsent.v1");

    /// <summary>Handles only the bounded server transport admitted by the outer middleware.</summary>
    public async Task ExecuteAsync(HttpContext http)
    {
        var browser = await http.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
        if (!browser.Succeeded) { http.Response.StatusCode = 401; return; }
        var protocol = await http.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        var pending = protocol.Principal;
        if (!browser.Succeeded || browser.Principal is null || !protocol.Succeeded || pending is null
            || !pending.HasTokenType(TokenTypeIdentifiers.Private.UserCode)
            || pending.GetTokenId() is not { Length: > 0 } userId
            || pending.GetClaim(Claims.Private.DeviceCodeId) is not { Length: > 0 } deviceId
            || !pending.GetResources().SequenceEqual([resource.Resource.AbsoluteUri]))
        { http.Response.StatusCode = 400; return; }
        var owner = browser.Principal;
        var scopes = string.Join(' ', pending.GetScopes().Order(StringComparer.Ordinal));
        var identity = string.Join('|', new[] { Claims.Subject, GameHubClaimTypes.TenantId,
            GameHubClaimTypes.AccountSessionId, GameHubClaimTypes.SessionVersion }
            .Select(type => owner.FindFirstValue(type) ?? ""));
        var action = http.Request.Form["action"].ToString();
        if (action != "preview")
        {
            Confirmation? proof;
            try { proof = JsonSerializer.Deserialize<Confirmation>(protector.Unprotect(http.Request.Form["confirmation"].ToString())); }
            catch (Exception exception) when (exception is CryptographicException or JsonException or ArgumentException)
            { http.Response.StatusCode = 400; return; }
            if (proof is null || proof.UserId != userId || proof.DeviceId != deviceId || proof.Identity != identity
                || proof.Scopes != scopes || proof.Resource != resource.Resource.AbsoluteUri
                || proof.End <= clock.GetUtcNow().UtcDateTime
                || (http.Request.Form["allow_tracking_write"] == "true" && !pending.HasScope(McpPermissions.TrackingWrite)))
            { http.Response.StatusCode = 400; return; }
            await consent.ExecuteAsync(http, action == "approve", http.Request.Form["allow_tracking_write"] == "true", proof.End);
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, http.RequestAborted);
        if (!await admission.CanConsentAsync(browser, http.RequestAborted)) { http.Response.StatusCode = 403; return; }
        var now = clock.GetUtcNow().UtcDateTime;
        var entries = await db.Set<McpProtocolToken>().AsNoTracking().Where(row => (row.Id == userId || row.Id == deviceId)
            && row.Authorization == null && row.Subject == null && row.ExpirationDate > now
            && row.Application != null && row.Application.ClientId == McpDeviceProtocol.ClientId).ToListAsync(http.RequestAborted);
        if (entries.Count != 2
            || !entries.Any(row => row.Id == userId && row.Type == TokenTypeIdentifiers.Private.UserCode && row.Status == Statuses.Valid)
            || !entries.Any(row => row.Id == deviceId && row.Type == TokenTypeIdentifiers.Private.DeviceCode && row.Status == Statuses.Inactive))
        { http.Response.StatusCode = 400; return; }
        var end = entries.Min(row => row.ExpirationDate)!.Value;
        if (pending.GetExpirationDate() is { } expiry && expiry.UtcDateTime < end) end = expiry.UtcDateTime;
        if (end > now.AddMinutes(5)) end = now.AddMinutes(5);
        var accountId = Guid.Parse(owner.FindFirstValue(Claims.Subject)!);
        var tenantId = Guid.Parse(owner.FindFirstValue(GameHubClaimTypes.TenantId)!);
        var account = await db.UserAccounts.AsNoTracking().SingleAsync(row => row.Id == accountId && row.TenantId == tenantId, http.RequestAborted);
        var confirmation = protector.Protect(JsonSerializer.Serialize(new Confirmation(userId, deviceId, identity, scopes,
            resource.Resource.AbsoluteUri, end)));
        await transaction.CommitAsync(http.RequestAborted);
        http.Features.Set(new McpConsentPreviewFeature());
        await http.Response.WriteAsJsonAsync(new
        {
            confirmation, instance = resource.Issuer.GetLeftPart(UriPartial.Authority),
            account = string.IsNullOrWhiteSpace(account.DisplayName) ? account.Email ?? account.Id.ToString("D") : account.DisplayName,
            accountReference = account.Id.ToString("D"),
            resource = resource.Resource.AbsoluteUri, trackingWriteRequested = pending.HasScope(McpPermissions.TrackingWrite),
            renewalRequested = pending.HasScope(Scopes.OfflineAccess), expiresAtUtc = end
        }, http.RequestAborted);
    }

    private sealed record Confirmation(string UserId, string DeviceId, string Identity, string Scopes, string Resource, DateTime End);
}

/// <summary>Marks the only successful preview response allowed through the outer sanitizer.</summary>
public sealed class McpConsentPreviewFeature;

/// <summary>Production endpoint continuation; protocol parsing and storage validation remain native.</summary>
public static class McpDeviceEndpoints
{
    /// <summary>Maps independently authenticated consent and transactionally admitted token continuation.</summary>
    public static void MapMcpDeviceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(McpDeviceProtocol.VerifyPath, (HttpContext http, McpBrowserConsent consent) => consent.ExecuteAsync(http));
        endpoints.MapPost(McpDeviceProtocol.TokenPath, async (HttpContext http) =>
        {
            var result = await http.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            if (!result.Succeeded || result.Principal is null)
                return Results.Forbid(authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
            return Results.SignIn(result.Principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        });
    }
}
