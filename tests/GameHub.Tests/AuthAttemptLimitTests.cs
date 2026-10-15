using System.Security.Claims;
using GameHub.Core.Models.Auth;
using GameHub.Core.Services;
using GameHub.Shared.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace GameHub.Tests;

/// <summary>Regression coverage for users sharing the same BFF source address.</summary>
public sealed class AuthAttemptLimitTests
{
    [Fact]
    public void UniqueAnonymousInputs_CannotGrowMemoryOrEvictLiveAccountBudgets()
    {
        var clock = new AttemptClock();
        using var limiter = new AuthAttemptLimiter(clock);
        for (var index = 0; index < AuthAttemptLimiter.MaximumPartitions; index++)
        {
            Assert.True(limiter.TryAcquireAttempt("opaque-bucket-" + index));
        }
        Assert.False(limiter.TryAcquireAttempt("overflow-bucket"));
        Assert.True(limiter.TryAcquireAttempt("opaque-bucket-0"));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(limiter.TryAcquireAttempt("overflow-bucket"));
        for (var index = 0; index < 9; index++) Assert.True(limiter.TryAcquireAttempt("overflow-bucket"));
        Assert.False(limiter.TryAcquireAttempt("overflow-bucket"));
    }

    private sealed class AttemptClock : TimeProvider
    {
        private long _ticks;
        public override long GetTimestamp() => _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;
    }

    [Fact]
    public async Task PasswordAttempts_ExhaustedAccountDoesNotBlockOtherAccountOrRefresh()
    {
        using var limiter = new AuthAttemptLimiter();
        var filter = new AuthAttemptLimitFilter(limiter);
        for (var attempt = 0; attempt < 10; attempt++) Assert.True(await Execute(filter, new TokenRequest("alice@example.com", "invalid")));
        Assert.False(await Execute(filter, new TokenRequest(" ALICE@example.com ", "invalid")));
        Assert.True(await Execute(filter, new TokenRequest("bob@example.com", "invalid")));
        Assert.True(await Execute(filter, new RefreshTokenRequest("synthetic-refresh-secret")));
    }

    [Fact]
    public async Task RefreshAttempts_ExhaustedTokenDoesNotBlockOtherSession()
    {
        using var limiter = new AuthAttemptLimiter();
        var filter = new AuthAttemptLimitFilter(limiter);
        for (var attempt = 0; attempt < 10; attempt++) Assert.True(await Execute(filter, new RefreshTokenRequest("first-session")));
        Assert.False(await Execute(filter, new RefreshTokenRequest("first-session")));
        Assert.True(await Execute(filter, new RefreshTokenRequest("second-session")));
    }

    [Fact]
    public void AccountBuckets_IncludeTrustedTenantAndRetainNoPlaintextSecrets()
    {
        var first = Context(new TokenRequest("private@example.com", "private-password"));
        first.HttpContext.User = Identity("tenant-a", "owner");
        var second = Context(new TokenRequest("private@example.com", "private-password"));
        second.HttpContext.User = Identity("tenant-b", "owner");
        Assert.NotEqual(AuthAttemptLimitFilter.Partition(first), AuthAttemptLimitFilter.Partition(second));
        var refresh = AuthAttemptLimitFilter.Partition(Context(new RefreshTokenRequest("refresh-secret")));
        Assert.DoesNotContain("refresh-secret", refresh);
        Assert.DoesNotContain("private@example.com", AuthAttemptLimitFilter.Partition(Context(new TokenRequest("private@example.com", "private-password"))));
    }

    private static ClaimsPrincipal Identity(string tenant, string account) => new(new ClaimsIdentity(new[]
    {
        new Claim(ClaimTypes.NameIdentifier,account),new Claim(GameHubClaimTypes.TenantId,tenant)
    }, "validated-test-principal"));

    private static ActionExecutingContext Context(object model) => new(
        new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()),
        new List<IFilterMetadata>(), new Dictionary<string, object?> { ["request"] = model }, new object());

    private static async Task<bool> Execute(AuthAttemptLimitFilter filter, object model)
    {
        var context = Context(model);
        var ran = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            ran = true;
            return Task.FromResult(new ActionExecutedContext(context, new List<IFilterMetadata>(), context.Controller));
        });
        if (!ran) Assert.Equal(429, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
        return ran;
    }
}
