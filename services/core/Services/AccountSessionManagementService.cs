using GameHub.Core.Data;
using GameHub.Core.Models.Auth;
using Microsoft.EntityFrameworkCore;

namespace GameHub.Core.Services;

/// <summary>
/// Lists and revokes provider-neutral GameHub session families for an already validated account,
/// tenant, and current local session.
/// </summary>
public sealed class AccountSessionManagementService(
    CoreContext db,
    TimeProvider timeProvider)
{
    /// <summary>
    /// Lists only active families owned by the exact signed account/tenant pair. A missing current
    /// family fails closed because the caller's access token is no longer a current session.
    /// </summary>
    public async Task<IReadOnlyList<AccountSessionResponse>?> ListActiveAsync(
        Guid accountId,
        Guid tenantId,
        Guid currentAccountSessionId,
        CancellationToken cancellationToken = default)
    {
        var rows = await db.AccountSessions
            .AsNoTracking()
            .Where(session => session.UserAccountId == accountId
                && session.TenantId == tenantId
                && session.RevokedAtUtc == null)
            .OrderByDescending(session => session.Id == currentAccountSessionId)
            .ThenByDescending(session => session.CreatedAtUtc)
            .Select(session => new
            {
                session.Id,
                session.CreatedAtUtc,
                IsCurrent = session.Id == currentAccountSessionId
            })
            .ToListAsync(cancellationToken);
        var sessions = rows
            .Select(session => new AccountSessionResponse(
                session.Id,
                new DateTimeOffset(
                    DateTime.SpecifyKind(session.CreatedAtUtc, DateTimeKind.Utc)),
                session.IsCurrent))
            .ToList();

        return sessions.Any(session => session.IsCurrent)
            ? sessions
            : null;
    }

    /// <summary>
    /// Atomically revokes one exact owned family and all of its refresh/local OIDC state.
    /// Unknown, foreign, and already revoked identifiers return the same successful no-op.
    /// </summary>
    public async Task RevokeAsync(
        Guid accountId,
        Guid tenantId,
        Guid accountSessionId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await AccountSessionRevocation.RevokeFamilyAsync(
            db,
            accountSessionId,
            accountId,
            tenantId,
            timeProvider.GetUtcNow().UtcDateTime,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Atomically revokes every owned active family except the current one. Returns false only
    /// when the supplied current family is not active for the exact account/tenant pair.
    /// </summary>
    public async Task<bool> RevokeOthersAsync(
        Guid accountId,
        Guid tenantId,
        Guid currentAccountSessionId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var currentIsActive = await db.AccountSessions
            .AsNoTracking()
            .AnyAsync(
                session => session.Id == currentAccountSessionId
                    && session.UserAccountId == accountId
                    && session.TenantId == tenantId
                    && session.RevokedAtUtc == null,
                cancellationToken);
        if (!currentIsActive)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await AccountSessionRevocation.RevokeOtherFamiliesAsync(
            db,
            currentAccountSessionId,
            accountId,
            tenantId,
            timeProvider.GetUtcNow().UtcDateTime,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
