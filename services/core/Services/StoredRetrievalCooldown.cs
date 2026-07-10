using System.Collections.Concurrent;
using GameHub.Shared;

namespace GameHub.Core.Services;

/// <summary>Tracks transient provider cooldowns per tenant and platform without persisting them.</summary>
public sealed class StoredRetrievalCooldown(TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<(Guid TenantId, Platform Platform), DateTimeOffset> _retryAfterUtc = [];

    /// <summary>Returns an active cooldown and removes entries that have elapsed.</summary>
    public bool TryGetRetryAfter(Guid tenantId, Platform platform, out DateTimeOffset retryAfterUtc)
    {
        var key = (tenantId, platform);
        if (_retryAfterUtc.TryGetValue(key, out retryAfterUtc)
            && retryAfterUtc > timeProvider.GetUtcNow())
        {
            return true;
        }

        _retryAfterUtc.TryRemove(key, out _);
        retryAfterUtc = default;
        return false;
    }

    /// <summary>Stores the latest provider-directed retry time for the selected tenant and platform.</summary>
    public void SetRetryAfter(Guid tenantId, Platform platform, DateTimeOffset retryAfterUtc)
    {
        if (retryAfterUtc <= timeProvider.GetUtcNow())
        {
            return;
        }

        _retryAfterUtc.AddOrUpdate(
            (tenantId, platform),
            retryAfterUtc,
            (_, existing) => existing >= retryAfterUtc ? existing : retryAfterUtc);
    }

    /// <summary>Clears a cooldown after a later retrieval succeeds or no credential remains.</summary>
    public void Clear(Guid tenantId, Platform platform) =>
        _retryAfterUtc.TryRemove((tenantId, platform), out _);
}

/// <summary>Signals that Ingest rejected stored retrieval until a provider-supplied retry time.</summary>
public sealed class StoredRetrievalRateLimitedException(
    Platform platform,
    DateTimeOffset? retryAfterUtc)
    : HttpRequestException("Stored platform retrieval is rate limited.")
{
    /// <summary>Platform whose stored retrieval was rate limited.</summary>
    public Platform Platform { get; } = platform;

    /// <summary>Provider-supplied UTC retry time, when available.</summary>
    public DateTimeOffset? RetryAfterUtc { get; } = retryAfterUtc;
}
