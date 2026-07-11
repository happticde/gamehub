using GameHub.Shared;

namespace GameHub.Core.Models.ProviderSync;

/// <summary>Secret-free actionable state for one platform's next provider refresh.</summary>
public sealed record ProviderSyncStatus(
    Platform Platform,
    bool IsConfigured,
    DateTimeOffset? CooldownUntil);

/// <summary>Secret-free actionable provider refresh state for the authenticated tenant.</summary>
public sealed record ProviderSyncStatusResponse(IReadOnlyList<ProviderSyncStatus> Providers);
