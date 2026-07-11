using GameHub.Core.Data;
using GameHub.Core.Models.ProviderSync;
using GameHub.Shared;

namespace GameHub.Core.Services;

/// <summary>
/// Builds tenant-scoped, secret-free actionable blockers for the next provider refresh.
/// It reads vault configuration state but never decrypts credentials, contacts a provider, or
/// starts retrieval.
/// </summary>
public sealed class ProviderSyncStatusService(
    ITenantContext tenantContext,
    IngestClient ingestClient,
    StoredRetrievalCooldown retrievalCooldown)
{
    private static readonly Platform[] SupportedPlatforms =
    [
        Platform.Steam,
        Platform.Gog,
        Platform.Psn,
        Platform.Epic
    ];

    /// <summary>Returns existing actionable state for the authenticated tenant without mutating it.</summary>
    public async Task<ProviderSyncStatusResponse> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var credentialStatuses = await ingestClient.ListPlatformCredentialStatusesAsync(cancellationToken);
        var configuredByPlatform = credentialStatuses
            .Where(status => SupportedPlatforms.Contains(status.Platform))
            .GroupBy(status => status.Platform)
            .ToDictionary(group => group.Key, group => group.Any(status => status.IsConfigured));
        var providers = SupportedPlatforms
            .Select(platform => CreateStatus(
                platform,
                configuredByPlatform.GetValueOrDefault(platform)))
            .ToArray();

        return new ProviderSyncStatusResponse(providers);
    }

    private ProviderSyncStatus CreateStatus(
        Platform platform,
        bool isConfigured)
    {
        var cooldownUntil = isConfigured
            && retrievalCooldown.TryPeekActiveRetryAfter(
                tenantContext.TenantId,
                platform,
                out var activeCooldown)
            ? activeCooldown.ToUniversalTime()
            : (DateTimeOffset?)null;

        return new ProviderSyncStatus(
            platform,
            isConfigured,
            cooldownUntil);
    }
}
