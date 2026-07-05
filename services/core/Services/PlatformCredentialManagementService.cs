using GameHub.Core.Data;
using GameHub.Core.Models.PlatformCredentials;
using GameHub.Shared;
using Microsoft.EntityFrameworkCore;

namespace GameHub.Core.Services;

/// <summary>
/// Manages the active tenant's encrypted Ingest credentials and derives whether platform sync
/// is safe to run. Browser input can never set <see cref="Tenant.PlatformSyncEnabled"/> directly.
/// </summary>
public sealed class PlatformCredentialManagementService(
    CoreContext db,
    ITenantContext tenantContext,
    IngestClient ingestClient,
    ILogger<PlatformCredentialManagementService> logger)
{
    private static readonly HashSet<Platform> StoredRetrievalPlatforms =
    [
        Platform.Steam,
        Platform.Gog,
        Platform.Psn,
        Platform.Epic
    ];

    /// <summary>Returns secret-free vault status and the current server-side activation state.</summary>
    public async Task<PlatformCredentialManagementResponse> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var statuses = await ingestClient.ListPlatformCredentialStatusesAsync(cancellationToken);
        var enabled = await GetTenantQuery(cancellationToken)
            .Select(tenant => tenant.PlatformSyncEnabled)
            .SingleAsync(cancellationToken);
        return new PlatformCredentialManagementResponse(enabled, statuses);
    }

    /// <summary>
    /// Stores one supported credential, verifies the resulting vault state, then derives sync
    /// activation from that state. A failed verification never enables a disabled tenant.
    /// </summary>
    public async Task<PlatformCredentialManagementResponse> UpsertAsync(
        Platform platform,
        UpsertPlatformCredentialRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireStoredRetrievalPlatform(platform);

        await ingestClient.UpsertPlatformCredentialAsync(platform, request, cancellationToken);
        var statuses = await ingestClient.ListPlatformCredentialStatusesAsync(cancellationToken);
        var enabled = HasConfiguredStoredCredential(statuses);
        await SetPlatformSyncEnabledAsync(enabled, cancellationToken);

        logger.LogInformation(
            "Updated encrypted {Platform} credential and set platform sync to {PlatformSyncEnabled} for tenant {TenantId}",
            platform,
            enabled,
            tenantContext.TenantId);
        return new PlatformCredentialManagementResponse(enabled, statuses);
    }

    /// <summary>
    /// Disables sync before deleting a credential, then enables it again only after Ingest proves
    /// another supported credential remains. Any downstream failure therefore stays fail-closed.
    /// </summary>
    public async Task<PlatformCredentialManagementResponse> DeleteAsync(
        Platform platform,
        CancellationToken cancellationToken = default)
    {
        RequireStoredRetrievalPlatform(platform);

        await SetPlatformSyncEnabledAsync(false, cancellationToken);
        await ingestClient.DeletePlatformCredentialAsync(platform, cancellationToken);

        var statuses = await ingestClient.ListPlatformCredentialStatusesAsync(cancellationToken);
        var enabled = HasConfiguredStoredCredential(statuses);
        if (enabled)
        {
            await SetPlatformSyncEnabledAsync(true, cancellationToken);
        }

        logger.LogInformation(
            "Deleted encrypted {Platform} credential and set platform sync to {PlatformSyncEnabled} for tenant {TenantId}",
            platform,
            enabled,
            tenantContext.TenantId);
        return new PlatformCredentialManagementResponse(enabled, statuses);
    }

    /// <summary>Runs immediate Ingest retrieval using only a verified stored credential.</summary>
    public async Task RetrieveAsync(
        Platform platform,
        CancellationToken cancellationToken = default)
    {
        RequireStoredRetrievalPlatform(platform);
        var tenant = await GetTenantQuery(cancellationToken).SingleAsync(cancellationToken);
        if (!tenant.PlatformSyncEnabled)
        {
            throw new TenantPlatformSyncDisabledException(tenantContext.TenantId);
        }

        if (!await ingestClient.RetrieveStoredAsync(platform, cancellationToken))
        {
            // A missing credential means Core's activation state is stale. Disable immediately
            // so subsequent scheduled or manual sync attempts stay fail-closed.
            await SetPlatformSyncEnabledAsync(false, cancellationToken);
            throw new PlatformCredentialMissingException(platform);
        }

        logger.LogInformation(
            "Retrieved {Platform} library using the encrypted credential for tenant {TenantId}",
            platform,
            tenantContext.TenantId);
    }

    /// <summary>Returns tenant-scoped Ingest audit history through trusted delegation.</summary>
    public Task<IReadOnlyList<PlatformRetrievalAuditLogResponse>> GetRetrievalHistoryAsync(
        string? platform,
        int limit,
        CancellationToken cancellationToken = default) =>
        ingestClient.GetRetrievalHistoryAsync(platform, Math.Clamp(limit, 1, 200), cancellationToken);

    private IQueryable<Tenant> GetTenantQuery(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return db.Tenants.Where(tenant => tenant.Id == tenantContext.TenantId);
    }

    private async Task SetPlatformSyncEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        var tenant = await GetTenantQuery(cancellationToken).SingleAsync(cancellationToken);
        if (tenant.PlatformSyncEnabled == enabled)
        {
            return;
        }

        tenant.PlatformSyncEnabled = enabled;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static bool HasConfiguredStoredCredential(
        IEnumerable<PlatformCredentialStatusResponse> statuses) =>
        statuses.Any(status =>
            status.IsConfigured && StoredRetrievalPlatforms.Contains(status.Platform));

    private static void RequireStoredRetrievalPlatform(Platform platform)
    {
        if (!StoredRetrievalPlatforms.Contains(platform))
        {
            throw new UnsupportedPlatformCredentialException(platform);
        }
    }
}

/// <summary>Raised when the public Core API is asked to manage an unsupported platform.</summary>
public sealed class UnsupportedPlatformCredentialException(Platform platform)
    : ArgumentException($"Stored credentials are not supported for {platform}.", nameof(platform));

/// <summary>Raised when Core's state and Ingest's tenant vault no longer agree.</summary>
public sealed class PlatformCredentialMissingException(Platform platform)
    : InvalidOperationException($"No stored credential is configured for {platform}.");
