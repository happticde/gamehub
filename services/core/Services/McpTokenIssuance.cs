using System.Security.Claims;
using GameHub.Core.Data;
using GameHub.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace GameHub.Core.Services;

// Server-only state shared with the buffering transaction. An error commits only confirmed
// replay revocation; ordinary failed issuance rolls back protocol consumption and credentials.
internal sealed class McpTokenTransaction
{
    internal McpGrantAdmissionResult? Grant { get; set; }
    internal bool RefreshReuseRevoked { get; set; }
}

/// <summary>Admits token issuance before OpenIddict consumes the validated source credential.</summary>
public sealed class McpTokenIssuance(McpGrantAdmission admission, IHttpContextAccessor accessor)
    : IOpenIddictServerHandler<ProcessSignInContext>
{
    /// <inheritdoc/>
    public async ValueTask HandleAsync(ProcessSignInContext context)
    {
        if (context.EndpointType != OpenIddictServerEndpointType.Token) return;
        var http = accessor.HttpContext ?? throw new InvalidOperationException("A protocol request is required.");
        var state = http.Features.Get<McpTokenTransaction>();
        var authentication = context.Transaction.GetProperty<ProcessAuthenticationContext>(
            typeof(ProcessAuthenticationContext).FullName!);
        var refresh = context.Request.IsRefreshTokenGrantType();
        var principal = refresh ? authentication?.RefreshTokenPrincipal : authentication?.DeviceCodePrincipal;
        if (state is null || principal is null
            || (!refresh && !context.Request.IsDeviceCodeGrantType()))
        {
            context.Reject(Errors.InvalidGrant);
            return;
        }
        state.Grant = await admission.AdmitAsync(principal, refresh
            ? TokenTypeIdentifiers.RefreshToken : TokenTypeIdentifiers.Private.DeviceCode,
            cancellationToken: http.RequestAborted);
        if (state.Grant is null)
        {
            context.Reject(Errors.InvalidGrant);
            return;
        }
        // Only the library-validated source may supply ownership and consent. A passthrough
        // endpoint cannot replace the owner or add claims/permissions to the outgoing principal.
        context.Principal = principal.Clone();
        foreach (var claim in context.Principal.Claims)
            if (claim.Type is GameHubClaimTypes.TenantId or GameHubClaimTypes.AccountSessionId
                or GameHubClaimTypes.SessionVersion)
                claim.SetDestinations(Destinations.AccessToken);
    }
}

/// <summary>Clamps prepared credentials before OpenIddict creates or protects their token entries.</summary>
public sealed class McpTokenExpiry(IHttpContextAccessor accessor, TimeProvider clock)
    : IOpenIddictServerHandler<ProcessSignInContext>
{
    /// <inheritdoc/>
    public ValueTask HandleAsync(ProcessSignInContext context)
    {
        if (context.EndpointType != OpenIddictServerEndpointType.Token) return default;
        var grant = accessor.HttpContext?.Features.Get<McpTokenTransaction>()?.Grant;
        var now = clock.GetUtcNow();
        if (grant is null || grant.ExpiresAtUtc <= now.UtcDateTime)
        {
            context.Reject(Errors.InvalidGrant);
            return default;
        }
        foreach (var principal in new[] { context.AccessTokenPrincipal, context.RefreshTokenPrincipal })
        {
            if (principal is null) continue;
            var limit = principal == context.AccessTokenPrincipal ? now.AddMinutes(10) : now.AddDays(30);
            if (grant.ExpiresAtUtc < limit.UtcDateTime) limit = new DateTimeOffset(grant.ExpiresAtUtc);
            if (principal.GetExpirationDate() is not { } expiration || expiration > limit)
                principal.SetExpirationDate(limit);
        }
        return default;
    }
}

/// <summary>Handles confirmed refresh reuse after cryptographic checks and before entry rejection.</summary>
public sealed class McpRefreshReuse(CoreContext db, McpGrantAdmission admission, IHttpContextAccessor accessor)
    : IOpenIddictServerHandler<ValidateTokenContext>
{
    /// <inheritdoc/>
    public async ValueTask HandleAsync(ValidateTokenContext context)
    {
        if (context.EndpointType != OpenIddictServerEndpointType.Token
            || context.Principal is not { } principal || context.TokenId is not { } tokenId)
            return;
        var http = accessor.HttpContext ?? throw new InvalidOperationException("A protocol request is required.");
        var token = await db.Set<McpProtocolToken>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == tokenId, http.RequestAborted);
        if (token?.Status != Statuses.Redeemed) return;
        // Never use a reference-string lookup alone as proof of credential possession. This
        // event runs after decryption, issuer/type/lifetime/presenter and proof checks. Admission
        // independently binds the exact original owner, grant, resource and stored refresh row.
        var state = http.Features.Get<McpTokenTransaction>();
        if (state is not null && context.Request is { } request && request.IsRefreshTokenGrantType()
            && principal.HasTokenType(TokenTypeIdentifiers.RefreshToken))
            state.RefreshReuseRevoked = await admission.RevokeReusedRefreshAsync(principal, http.RequestAborted);
        // Suppress the library's token-chain-only replay mutation: GameHub revokes the owned
        // family above, while device-code replay simply fails without ending a connection.
        context.Reject(Errors.InvalidToken);
    }
}
