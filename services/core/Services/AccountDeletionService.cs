using GameHub.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace GameHub.Core.Services;

/// <summary>
/// Coordinates fail-closed account deletion across Ingest and Core. The durable preparation
/// state is written first, Ingest is cleaned second, and Core commits its local deletion last.
/// That ordering makes every partial failure visible and safely retryable.
/// </summary>
public sealed class AccountDeletionService(
    CoreContext db,
    AccountService accounts,
    IngestClient ingest,
    SyncSchedulerStatus syncStatus,
    ILogger<AccountDeletionService> logger)
{
    /// <summary>Deletes the account and its tenant using only signed identity values.</summary>
    public async Task<AccountDeletionResult> DeleteAsync(
        Guid accountId,
        Guid tenantId,
        string? currentPassword,
        CancellationToken cancellationToken = default)
    {
        var preparation = await accounts.PrepareDeletionAsync(
            accountId,
            tenantId,
            currentPassword,
            cancellationToken);
        switch (preparation)
        {
            case AccountDeletionPreparationResult.InvalidCurrentPassword:
                return AccountDeletionResult.InvalidCurrentPassword;
            case AccountDeletionPreparationResult.IdentityMismatch:
                return AccountDeletionResult.IdentityMismatch;
            case AccountDeletionPreparationResult.AlreadyDeleted:
                return AccountDeletionResult.AlreadyDeleted;
        }

        try
        {
            await ingest.DeleteTenantDataAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Ingest cleanup failed for tenant {TenantId}; Core deletion remains pending",
                tenantId);
            return AccountDeletionResult.IngestCleanupFailed;
        }

        try
        {
            await DeleteCoreTenantAsync(accountId, tenantId, cancellationToken);
            syncStatus.RemoveTenant(tenantId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Core cleanup failed after Ingest cleanup for tenant {TenantId}; deletion remains retryable",
                tenantId);
            return AccountDeletionResult.CoreCleanupFailed;
        }

        logger.LogInformation(
            "Deleted account {AccountId} and tenant data {TenantId}",
            accountId,
            tenantId);
        return AccountDeletionResult.Deleted;
    }

    private async Task DeleteCoreTenantAsync(
        Guid accountId,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        await db.Set<TagAssignment>().IgnoreQueryFilters()
            .Where(row => row.TenantId == tenantId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.LibraryItems.IgnoreQueryFilters()
            .Where(row => row.TenantId == tenantId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.ManualLinks.IgnoreQueryFilters()
            .Where(row => row.TenantId == tenantId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.EnrichmentAuditLogs.IgnoreQueryFilters()
            .Where(row => row.TenantId == tenantId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.SyncMetadata.IgnoreQueryFilters()
            .Where(row => row.TenantId == tenantId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.SyncAuditLogs.IgnoreQueryFilters()
            .Where(row => row.TenantId == tenantId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.ActivityLogs.IgnoreQueryFilters()
            .Where(row => row.TenantId == tenantId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.Tags.IgnoreQueryFilters()
            .Where(row => row.TenantId == tenantId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.UnifiedGames.IgnoreQueryFilters()
            .Where(row => row.TenantId == tenantId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.RefreshTokenSessions
            .Where(row => row.UserAccountId == accountId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.UserAccounts
            .Where(row => row.Id == accountId && row.TenantId == tenantId)
            .ExecuteDeleteAsync(cancellationToken);

        if (tenantId == TenantDefaults.DefaultTenantId)
        {
            // The compatibility boundary is seeded by the original tenant migration. Keep its
            // empty shell so legacy installations can bootstrap a replacement owner later.
            await db.Tenants
                .Where(tenant => tenant.Id == tenantId)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(tenant => tenant.PlatformSyncEnabled, false)
                        .SetProperty(tenant => tenant.DeletionRequestedAtUtc, (DateTime?)null),
                    cancellationToken);
        }
        else
        {
            await db.Tenants
                .Where(tenant => tenant.Id == tenantId)
                .ExecuteDeleteAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }
}

/// <summary>Cross-service deletion outcome mapped to a stable HTTP response.</summary>
public enum AccountDeletionResult
{
    Deleted,
    AlreadyDeleted,
    InvalidCurrentPassword,
    IdentityMismatch,
    IngestCleanupFailed,
    CoreCleanupFailed
}
