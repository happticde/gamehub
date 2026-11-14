using System.Globalization;
using System.Security.Claims;
using GameHub.Core.Data;
using GameHub.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;

namespace GameHub.Core.Services;

/// <summary>Checks owned consent and live session/provider state after protocol token validation.</summary>
public sealed class McpSessionValidationService(CoreContext db)
{
    /// <summary>Rechecks durable state on every use, without cached protocol entities.</summary>
    public async Task<bool> IsActiveAsync(
        ClaimsPrincipal principal, McpRestResource resource, CancellationToken cancellationToken = default)
    {
        string? One(string type) => principal.FindAll(type).Select(claim => claim.Value).ToArray()
            is [var value] ? value : null;
        if (!Guid.TryParse(One(OpenIddictConstants.Claims.Subject), out var accountId) || accountId == Guid.Empty
            || !Guid.TryParse(One(GameHubClaimTypes.TenantId), out var tenantId) || tenantId == Guid.Empty
            || !Guid.TryParse(One(GameHubClaimTypes.AccountSessionId), out var familyId) || familyId == Guid.Empty
            || !long.TryParse(One(GameHubClaimTypes.SessionVersion), NumberStyles.None,
                CultureInfo.InvariantCulture, out var epoch)
            || One(OpenIddictConstants.Claims.Private.Issuer) != resource.Issuer.AbsoluteUri
            || One(OpenIddictConstants.Claims.Private.Audience) != resource.Resource.AbsoluteUri
            || principal.GetTokenId() is not { Length: > 0 } tokenId
            || principal.GetAuthorizationId() is not { Length: > 0 } authorizationId)
            return false;

        var grant = await db.McpGrants.AsNoTracking()
            .Include(row => row.AccountSession)!.ThenInclude(row => row!.UserAccount)!
                .ThenInclude(row => row!.Tenant)
            .Include(row => row.OidcProviderSession)
            .Include(row => row.ProtocolAuthorization)!.ThenInclude(row => row!.Application)
            .SingleOrDefaultAsync(row => row.UserAccountId == accountId && row.TenantId == tenantId
                && row.AccountSessionId == familyId && row.ProtocolAuthorizationId == authorizationId,
                cancellationToken);
        var now = DateTime.UtcNow;
        if (grant is null || grant.RevokedAtUtc is not null || grant.CreatedAtUtc > now || grant.ExpiresAtUtc <= now
            || grant.Resource != resource.Resource.AbsoluteUri || grant.ApprovedSessionVersion != epoch
            || grant.Scopes is not (McpPermissions.LibraryRead or McpPermissions.ReadAndWrite)
            || grant.AccountSession is not { RevokedAtUtc: null, UserAccount: { Tenant: { DeletionRequestedAtUtc: null } } account }
            || grant.AccountSession.UserAccountId != accountId || grant.AccountSession.TenantId != tenantId
            || account.Id != accountId || account.TenantId != tenantId || account.SessionVersion != epoch
            || grant.OidcProviderSession is not { ReissueBlockedAtUtc: null, ExternalIdentityId: not null, AccountSessionId: not null } provider
            || provider.UserAccountId != accountId || provider.TenantId != tenantId
            || provider.AccountSessionId == familyId
            || grant.ProtocolAuthorization is not { Status: OpenIddictConstants.Statuses.Valid } authorization
            || authorization.UserAccountId != accountId || authorization.TenantId != tenantId
            || authorization.AccountSessionId != familyId || authorization.Subject != principal.GetClaim(OpenIddictConstants.Claims.Subject)
            || authorization.Application?.ClientId != grant.ClientId
            || !principal.GetPresenters().SequenceEqual([grant.ClientId]))
            return false;

        var scopes = principal.GetScopes();
        if (!scopes.Contains(McpPermissions.LibraryRead)
            || scopes.Any(scope => scope != OpenIddictConstants.Scopes.OfflineAccess
                && !grant.Scopes.Split(' ').Contains(scope, StringComparer.Ordinal)))
            return false;
        return await db.Set<McpProtocolToken>().AsNoTracking().AnyAsync(token => token.Id == tokenId
            && token.Authorization != null && token.Authorization.Id == authorizationId
            && token.Application != null && token.Application.Id == authorization.Application!.Id
            && token.Subject == principal.GetClaim(OpenIddictConstants.Claims.Subject) && token.Status == OpenIddictConstants.Statuses.Valid
            && token.Type == OpenIddictConstants.TokenTypeIdentifiers.AccessToken
            && token.ExpirationDate != null && token.ExpirationDate > now,
            cancellationToken);
    }
}
