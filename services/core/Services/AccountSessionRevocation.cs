using GameHub.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace GameHub.Core.Services;

/// <summary>
/// Central database mutation boundary for local session-family invalidation.
///
/// Callers must execute these methods inside the transaction that owns their surrounding
/// lifecycle operation. Keeping the family, refresh rows, and optional local OIDC mapping in one
/// place prevents different logout paths from applying contradictory revocation semantics.
/// </summary>
public static class AccountSessionRevocation
{
    /// <summary>
    /// Revokes one active family owned by the exact account/tenant pair. Unknown, foreign, and
    /// already revoked identifiers are indistinguishable no-ops.
    /// </summary>
    public static async Task<bool> RevokeFamilyAsync(
        CoreContext db,
        Guid accountSessionId,
        Guid accountId,
        Guid tenantId,
        DateTime revokedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var revokedFamilies = await db.AccountSessions
            .Where(session => session.Id == accountSessionId
                && session.UserAccountId == accountId
                && session.TenantId == tenantId
                && session.RevokedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(session => session.RevokedAtUtc, revokedAtUtc),
                cancellationToken);
        if (revokedFamilies != 1)
        {
            return false;
        }

        await db.RefreshTokenSessions
            .Where(session => session.AccountSessionId == accountSessionId
                && session.UserAccountId == accountId
                && session.RevokedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(session => session.RevokedAtUtc, revokedAtUtc),
                cancellationToken);
        await db.OidcProviderSessions
            .Where(session => session.AccountSessionId == accountSessionId
                && session.UserAccountId == accountId
                && session.TenantId == tenantId
                && session.RevokedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(session => session.RevokedAtUtc, revokedAtUtc),
                cancellationToken);
        return true;
    }

    /// <summary>
    /// Revokes every active family except the exact current family without changing the account
    /// epoch. The current access and refresh credentials therefore remain valid.
    /// </summary>
    public static async Task<int> RevokeOtherFamiliesAsync(
        CoreContext db,
        Guid currentAccountSessionId,
        Guid accountId,
        Guid tenantId,
        DateTime revokedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var revokedFamilies = await db.AccountSessions
            .Where(session => session.Id != currentAccountSessionId
                && session.UserAccountId == accountId
                && session.TenantId == tenantId
                && session.RevokedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(session => session.RevokedAtUtc, revokedAtUtc),
                cancellationToken);

        await db.RefreshTokenSessions
            .Where(session => session.AccountSessionId != currentAccountSessionId
                && session.UserAccountId == accountId
                && session.RevokedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(session => session.RevokedAtUtc, revokedAtUtc),
                cancellationToken);
        await db.OidcProviderSessions
            .Where(session => session.AccountSessionId != currentAccountSessionId
                && session.UserAccountId == accountId
                && session.TenantId == tenantId
                && session.RevokedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(session => session.RevokedAtUtc, revokedAtUtc),
                cancellationToken);
        return revokedFamilies;
    }

    /// <summary>
    /// Revokes all local families and their refresh/provider-session state for one exact account.
    /// Account-wide callers remain responsible for advancing the durable account epoch.
    /// </summary>
    public static async Task RevokeAllFamiliesAsync(
        CoreContext db,
        Guid accountId,
        Guid tenantId,
        DateTime revokedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await db.AccountSessions
            .Where(session => session.UserAccountId == accountId
                && session.TenantId == tenantId
                && session.RevokedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(session => session.RevokedAtUtc, revokedAtUtc),
                cancellationToken);
        await db.RefreshTokenSessions
            .Where(session => session.UserAccountId == accountId
                && session.RevokedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(session => session.RevokedAtUtc, revokedAtUtc),
                cancellationToken);
        await db.OidcProviderSessions
            .Where(session => session.UserAccountId == accountId
                && session.TenantId == tenantId
                && session.RevokedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(session => session.RevokedAtUtc, revokedAtUtc),
                cancellationToken);
    }
}
