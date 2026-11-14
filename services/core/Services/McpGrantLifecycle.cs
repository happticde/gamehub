using GameHub.Core.Data;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;

namespace GameHub.Core.Services;

/// <summary>
/// Shared grant/protocol mutations. Call only inside the surrounding lifecycle transaction.
/// Issuance, live validation, race barriers and the protocol manager's cache policy are later gates.
/// </summary>
public static class McpGrantLifecycle
{
    /// <summary>Closes grants whose exact owner-bound families have been revoked.</summary>
    public static Task RevokeEndedFamiliesAsync(
        CoreContext db, Guid accountId, Guid tenantId, DateTime revokedAtUtc,
        CancellationToken cancellationToken = default) =>
        RevokeAsync(db, db.McpGrants.Where(grant => grant.UserAccountId == accountId
            && grant.TenantId == tenantId && grant.AccountSession != null
            && grant.AccountSession.RevokedAtUtc != null), revokedAtUtc, cancellationToken);

    /// <summary>Closes every connection derived from one verified provider row, even after local logout.</summary>
    public static async Task RevokeProviderAsync(
        CoreContext db, Guid providerSessionId, Guid accountId, Guid tenantId, DateTime revokedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var grants = db.McpGrants.Where(grant => grant.OidcProviderSessionId == providerSessionId
            && grant.UserAccountId == accountId && grant.TenantId == tenantId);
        var families = grants.Select(grant => grant.AccountSessionId);
        await db.AccountSessions.Where(family => families.Contains(family.Id)
                && family.UserAccountId == accountId && family.TenantId == tenantId
                && family.RevokedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.RevokedAtUtc, revokedAtUtc),
                cancellationToken);
        await RevokeAsync(db, grants, revokedAtUtc, cancellationToken);
    }

    /// <summary>Removes owned protocol credentials before provider provenance is reduced to a tombstone.</summary>
    public static async Task DeleteOwnedAsync(
        CoreContext db, Guid accountId, Guid tenantId, CancellationToken cancellationToken = default)
    {
        // Include owned authorizations without a grant too, so partial cleanup never leaves
        // protocol state retaining an account reference. Pending, unapproved codes have no owner.
        var authorizations = db.Set<McpProtocolAuthorization>()
            .Where(row => row.UserAccountId == accountId && row.TenantId == tenantId);
        var ids = authorizations.Select(row => row.Id);
        await db.Set<McpProtocolToken>()
            .Where(row => row.Authorization != null && ids.Contains(row.Authorization.Id))
            .ExecuteDeleteAsync(cancellationToken);
        await db.McpGrants.Where(row => row.UserAccountId == accountId && row.TenantId == tenantId)
            .ExecuteDeleteAsync(cancellationToken);
        await authorizations.ExecuteDeleteAsync(cancellationToken);
    }

    private static async Task RevokeAsync(
        CoreContext db, IQueryable<McpGrant> grants, DateTime revokedAtUtc,
        CancellationToken cancellationToken)
    {
        var ids = grants.Select(row => row.ProtocolAuthorizationId);
        var concurrencyToken = Guid.NewGuid().ToString();
        await db.Set<McpProtocolToken>()
            .Where(row => row.Authorization != null && ids.Contains(row.Authorization.Id))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Status, OpenIddictConstants.Statuses.Revoked)
                .SetProperty(row => row.ConcurrencyToken, concurrencyToken), cancellationToken);
        await db.Set<McpProtocolAuthorization>().Where(row => ids.Contains(row.Id))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Status, OpenIddictConstants.Statuses.Revoked)
                .SetProperty(row => row.ConcurrencyToken, concurrencyToken), cancellationToken);
        await grants.Where(row => row.RevokedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.RevokedAtUtc, revokedAtUtc),
                cancellationToken);
    }
}
