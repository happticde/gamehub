using GameHub.Shared;

namespace GameHub.Core.Models.ProviderSync;

/// <summary>Closed, secret-free readiness state for a platform's next provider retrieval.</summary>
public enum ProviderFreshnessStatus
{
    NotConfigured,
    NeverRefreshed,
    Fresh,
    Stale,
    Cooldown
}

/// <summary>Secret-free local sync state for one supported provider.</summary>
public sealed record ProviderSyncStatus(
    Platform Platform,
    bool IsConfigured,
    DateTime? LastSuccessfulRefreshAt,
    DateTime? FreshUntil,
    ProviderFreshnessStatus FreshnessStatus,
    DateTimeOffset? CooldownUntil);

/// <summary>Secret-free local sync readiness state for all supported providers.</summary>
public sealed record ProviderSyncStatusResponse(IReadOnlyList<ProviderSyncStatus> Providers);
