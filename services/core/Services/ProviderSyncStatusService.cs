using GameHub.Core.Data;
using GameHub.Core.Models.ProviderSync;
using GameHub.Shared;
using Microsoft.EntityFrameworkCore;

namespace GameHub.Core.Services;

/// <summary>
/// Builds a tenant-scoped, secret-free snapshot of locally known provider sync readiness.
/// It reads vault configuration state but never decrypts credentials, contacts a provider, or
/// starts retrieval.
/// </summary>
public sealed class ProviderSyncStatusService(
    CoreContext db,
    ITenantContext tenantContext,
    IngestClient ingestClient,
    StoredRetrievalCooldown retrievalCooldown,
    IConfiguration configuration,
    TimeProvider timeProvider)
{
    private static readonly Platform[] SupportedPlatforms =
    [
        Platform.Steam,
        Platform.Gog,
        Platform.Psn,
        Platform.Epic
    ];

    private readonly TimeSpan _freshnessTtl = ProviderFreshnessWindow.FromConfiguration(configuration);

    /// <summary>Returns existing local state for the authenticated tenant without mutating it.</summary>
    public async Task<ProviderSyncStatusResponse> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var credentialStatuses = await ingestClient.ListPlatformCredentialStatusesAsync(cancellationToken);
        var configuredByPlatform = credentialStatuses
            .Where(status => SupportedPlatforms.Contains(status.Platform))
            .GroupBy(status => status.Platform)
            .ToDictionary(group => group.Key, group => group.Any(status => status.IsConfigured));
        var metadataByPlatform = await db.SyncMetadata
            .AsNoTracking()
            .Where(metadata => SupportedPlatforms.Contains(metadata.Platform))
            .ToDictionaryAsync(metadata => metadata.Platform, cancellationToken);

        var now = timeProvider.GetUtcNow();
        var providers = SupportedPlatforms
            .Select(platform => CreateStatus(
                platform,
                configuredByPlatform.GetValueOrDefault(platform),
                metadataByPlatform.GetValueOrDefault(platform)?.LastSuccessfulProviderRetrievalAtUtc,
                now))
            .ToArray();

        return new ProviderSyncStatusResponse(providers);
    }

    private ProviderSyncStatus CreateStatus(
        Platform platform,
        bool isConfigured,
        DateTime? lastSuccessfulRefreshAt,
        DateTimeOffset now)
    {
        var lastRefreshUtc = AsUtc(lastSuccessfulRefreshAt);
        var freshUntil = lastRefreshUtc?.Add(_freshnessTtl);
        DateTimeOffset cooldownUntil = default;

        // These checks are intentionally ordered. A missing credential is more actionable than a
        // historical cooldown; an active cooldown takes precedence over freshness calculations.
        var freshnessStatus = !isConfigured
            ? ProviderFreshnessStatus.NotConfigured
            : retrievalCooldown.TryPeekActiveRetryAfter(
                tenantContext.TenantId,
                platform,
                out cooldownUntil)
                ? ProviderFreshnessStatus.Cooldown
                : lastRefreshUtc is null
                    ? ProviderFreshnessStatus.NeverRefreshed
                    : freshUntil > now.UtcDateTime
                        ? ProviderFreshnessStatus.Fresh
                        : ProviderFreshnessStatus.Stale;

        return new ProviderSyncStatus(
            platform,
            isConfigured,
            lastRefreshUtc,
            freshUntil,
            freshnessStatus,
            freshnessStatus == ProviderFreshnessStatus.Cooldown ? cooldownUntil.ToUniversalTime() : null);
    }

    private static DateTime? AsUtc(DateTime? value) =>
        value is { } timestamp
            ? DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)
            : null;
}
