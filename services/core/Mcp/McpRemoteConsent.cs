using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using GameHub.Core.Data;
using GameHub.Core.Services;
using GameHub.Shared.Authentication;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace GameHub.Core.Mcp;

internal sealed class McpRemoteConsent(CoreContext db, McpGrantAdmission admission, IOpenIddictTokenManager tokens,
    McpProtocolKeys keys, McpRestResource rest, McpRemoteResource resource, TimeProvider clock)
{
    private const string Protocol = OpenIddictServerAspNetCoreDefaults.AuthenticationScheme;
    private readonly IDataProtector _protector = keys.Provider.CreateProtector("GameHub.Mcp.RemoteConsent.v1");

    internal async Task ExecuteAsync(HttpContext http)
    {
        var cancellation = http.RequestAborted;
        var browser = await http.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
        if (!browser.Succeeded || browser.Principal is not { } owner)
        { http.Response.StatusCode = 401; return; }
        var request = http.GetOpenIddictServerRequest();
        var pending = http.Features.Get<McpPendingRemoteConsent>()?.Principal;
        if (request is null || McpRemoteClient.Find(request.ClientId) is not { } client
            || !McpDeviceProtocol.ExactResource(request, resource.Uri.AbsoluteUri)
            || pending is null || !pending.HasTokenType(TokenTypeIdentifiers.Private.RequestToken)
            || !pending.GetPresenters().SequenceEqual([client.Id])
            || pending.GetTokenId() is not { Length: > 0 } id
            || pending.GetExpirationDate() is not { } expiry || expiry <= clock.GetUtcNow())
        { http.Response.StatusCode = 400; return; }

        var form = http.Request.Form;
        var action = form["action"].ToString();
        var identity = string.Join('|', new[] { Claims.Subject, GameHubClaimTypes.TenantId,
            GameHubClaimTypes.AccountSessionId, GameHubClaimTypes.SessionVersion }.Select(type => owner.FindFirstValue(type)));
        var binding = new Confirmation(id, identity, expiry);
        if (action != "preview" && !Matches(form["confirmation"].ToString(), binding))
        { http.Response.StatusCode = 400; return; }

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellation);
        if (!await admission.CanConsentAsync(browser, cancellation))
        { http.Response.StatusCode = 403; return; }
        var now = clock.GetUtcNow().UtcDateTime;
        var pendingEntry = db.Set<McpProtocolToken>().Where(row => row.Id == id && row.Authorization == null
            && row.Subject == null && row.ExpirationDate > now && row.Type == TokenTypeIdentifiers.Private.RequestToken
            && row.Status == Statuses.Valid && row.Application != null && row.Application.ClientId == client.Id);
        if (action == "preview")
        {
            if (!await pendingEntry.AnyAsync(cancellation) || expiry <= clock.GetUtcNow())
            { http.Response.StatusCode = 400; return; }
            var accountId = Guid.Parse(owner.FindFirstValue(Claims.Subject)!);
            var tenantId = Guid.Parse(owner.FindFirstValue(GameHubClaimTypes.TenantId)!);
            var account = await db.UserAccounts.AsNoTracking().SingleAsync(row => row.Id == accountId
                && row.TenantId == tenantId, cancellation);
            var confirmation = _protector.Protect(JsonSerializer.Serialize(binding));
            await transaction.CommitAsync(cancellation);
            await http.Response.WriteAsJsonAsync(new
            {
                confirmation, client = client.Name, resource = resource.Uri.AbsoluteUri,
                account = string.IsNullOrWhiteSpace(account.DisplayName) ? account.Email ?? account.Id.ToString("D") : account.DisplayName,
                accountReference = account.Id.ToString("D"),
                trackingWriteRequested = request.HasScope(McpPermissions.TrackingWrite),
                renewalRequested = request.HasScope(Scopes.OfflineAccess), expiresAtUtc = expiry
            }, cancellation);
            http.Features.Set(new McpRemoteReply(200));
            return;
        }

        if (await pendingEntry.ExecuteUpdateAsync(update => update.SetProperty(row => row.Status, row => row.Status),
                cancellation) != 1 || expiry <= clock.GetUtcNow())
        { http.Response.StatusCode = 400; return; }
        var approved = action == "approve";
        if (approved)
        {
            var grant = await admission.CreateAsync(browser, request.GetScopes(), form["allow_tracking_write"] == "true",
                client.Id, cancellation);
            if (grant is null) { http.Response.StatusCode = 403; return; }
            var principal = grant.CreatePrincipal(Protocol, request.HasScope(Scopes.OfflineAccess));
            await Results.SignIn(principal, authenticationScheme: Protocol).ExecuteAsync(http);
            if (!await db.Set<McpProtocolToken>().AnyAsync(row => row.Authorization != null
                && row.Authorization.Id == grant.AuthorizationId && row.Type == TokenTypeIdentifiers.Private.AuthorizationCode
                && row.Status == Statuses.Valid, cancellation)) return;
        }
        else
        {
            var entry = await tokens.FindByIdAsync(id, cancellation);
            if (entry is null || !await tokens.TryRejectAsync(entry, cancellation)) return;
            await Results.Forbid(new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.AccessDenied
            }), [Protocol]).ExecuteAsync(http);
        }
        var status = approved ? Statuses.Redeemed : Statuses.Rejected;
        if (!await db.Set<McpProtocolToken>().AnyAsync(row => row.Id == id && row.Status == status, cancellation)
            || http.Response.StatusCode != 302 || !IsCallback(http.Response.Headers.Location, request, approved)
            || expiry <= clock.GetUtcNow()) return;
        await transaction.CommitAsync(cancellation);
        http.Features.Set(new McpRemoteReply(302));
    }

    private bool Matches(string proof, Confirmation binding)
    {
        try { return JsonSerializer.Deserialize<Confirmation>(_protector.Unprotect(proof)) == binding; }
        catch (Exception exception) when (exception is CryptographicException or JsonException or ArgumentException)
        { return false; }
    }

    private bool IsCallback(string? location, OpenIddictRequest request, bool approved)
    {
        if (location is null || !location.StartsWith(request.RedirectUri + "?", StringComparison.Ordinal)
            || !Uri.TryCreate(location, UriKind.Absolute, out var uri) || uri.Fragment.Length != 0) return false;
        var parameters = QueryHelpers.ParseQuery(uri.Query);
        return parameters.Count == 3 && parameters.All(item => item.Value.Count == 1)
            && parameters.GetValueOrDefault(Parameters.State) == request.State
            && parameters.GetValueOrDefault(Parameters.Iss) == rest.Issuer.AbsoluteUri
            && (approved ? !string.IsNullOrEmpty(parameters.GetValueOrDefault(Parameters.Code).ToString())
                : parameters.GetValueOrDefault(Parameters.Error) == Errors.AccessDenied);
    }

    private sealed record Confirmation(string RequestId, string Owner, DateTimeOffset Expiry);
}

internal static class McpRemoteConsentEndpoint
{
    /// <summary>POST /connect/authorize/consent processes private preview and browser decisions.</summary>
    internal static void MapMcpRemoteConsent(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost(McpRemoteResource.ConsentPath, (HttpContext context, McpRemoteConsent consent) => consent.ExecuteAsync(context));
}
