using GameHub.Core.Services;
using GameHub.Shared;
using Xunit;

namespace GameHub.Tests;

public sealed class StoredRetrievalCooldownTests
{
    [Fact]
    public void SetRetryAfter_IsolatedByTenantAndPlatformAndExpiresWithoutPersistence()
    {
        var now = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var time = new MutableTimeProvider(now);
        var cooldown = new StoredRetrievalCooldown(time);
        var tenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var tenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var retryAfterUtc = now.AddMinutes(5);

        cooldown.SetRetryAfter(tenantA, Platform.Epic, retryAfterUtc);

        Assert.True(cooldown.TryGetRetryAfter(tenantA, Platform.Epic, out var recordedRetryAfter));
        Assert.Equal(retryAfterUtc, recordedRetryAfter);
        Assert.False(cooldown.TryGetRetryAfter(tenantB, Platform.Epic, out _));
        Assert.False(cooldown.TryGetRetryAfter(tenantA, Platform.Steam, out _));

        time.UtcNow = retryAfterUtc;

        Assert.False(cooldown.TryGetRetryAfter(tenantA, Platform.Epic, out _));
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
