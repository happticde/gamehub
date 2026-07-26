using GameHub.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace GameHub.Core.Services;

/// <summary>
/// Performs the live account, epoch, and durable local-session check after GameHub JWT
/// cryptographic validation.
/// </summary>
public sealed class AccountSessionValidationService(CoreContext db)
{
    /// <summary>
    /// Returns true only for the exact active account, tenant, epoch, and (for normal access)
    /// durable local session. A null session is accepted only for narrowly scoped compatibility
    /// tokens after the bearer handler has restricted their route.
    /// </summary>
    public Task<bool> IsActiveAsync(
        Guid accountId,
        Guid tenantId,
        long sessionVersion,
        Guid? accountSessionId = null,
        CancellationToken cancellationToken = default) =>
        accountSessionId is null
            ? db.UserAccounts.AsNoTracking().AnyAsync(
                account => account.Id == accountId
                    && account.TenantId == tenantId
                    && account.SessionVersion == sessionVersion
                    && account.Tenant != null
                    && account.Tenant.DeletionRequestedAtUtc == null,
                cancellationToken)
            : db.AccountSessions.AsNoTracking().AnyAsync(
                session => session.Id == accountSessionId
                    && session.UserAccountId == accountId
                    && session.TenantId == tenantId
                    && session.RevokedAtUtc == null
                    && session.UserAccount != null
                    && session.UserAccount.SessionVersion == sessionVersion
                    && session.UserAccount.Tenant != null
                    && session.UserAccount.Tenant.DeletionRequestedAtUtc == null,
                cancellationToken);
}
