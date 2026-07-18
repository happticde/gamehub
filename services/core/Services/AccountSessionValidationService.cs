using GameHub.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace GameHub.Core.Services;

/// <summary>
/// Performs the live account/session-epoch check after GameHub JWT cryptographic validation.
/// Keeping this query explicit and testable prevents account-wide logout from depending only on
/// access-token expiry.
/// </summary>
public sealed class AccountSessionValidationService(CoreContext db)
{
    /// <summary>Returns true only for the exact active account, tenant, and session epoch.</summary>
    public Task<bool> IsActiveAsync(
        Guid accountId,
        Guid tenantId,
        long sessionVersion,
        CancellationToken cancellationToken = default) =>
        db.UserAccounts.AsNoTracking().AnyAsync(
            account => account.Id == accountId
                && account.TenantId == tenantId
                && account.SessionVersion == sessionVersion
                && account.Tenant != null
                && account.Tenant.DeletionRequestedAtUtc == null,
            cancellationToken);
}
