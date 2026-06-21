using GameHub.Ingest.Data;
using Microsoft.EntityFrameworkCore;

namespace GameHub.Ingest.Services;

/// <summary>Idempotently removes every row owned by the delegated Ingest tenant.</summary>
public sealed class TenantDataDeletionService(
    IngestContext db,
    IIngestTenantContext tenantContext)
{
    /// <summary>
    /// Deletes raw platform rows, audit history, and encrypted credential material in one
    /// local transaction. Repeating the operation after success deletes zero rows and succeeds.
    /// </summary>
    public async Task DeleteCurrentTenantAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = tenantContext.TenantId;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.PlatformCredentials.IgnoreQueryFilters()
            .Where(row => row.TenantId == tenantId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.IngestAuditLogs.IgnoreQueryFilters()
            .Where(row => row.TenantId == tenantId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.SteamRawItems.IgnoreQueryFilters()
            .Where(row => row.TenantId == tenantId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.PsnRawItems.IgnoreQueryFilters()
            .Where(row => row.TenantId == tenantId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.GogRawItems.IgnoreQueryFilters()
            .Where(row => row.TenantId == tenantId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.EpicRawItems.IgnoreQueryFilters()
            .Where(row => row.TenantId == tenantId)
            .ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
