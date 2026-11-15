using System.Data.Common;
using System.Net;
using System.Security.Claims;
using GameHub.Core.Data;
using GameHub.Core.Services;
using GameHub.Shared.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameHub.Tests;

public sealed partial class McpAuthorizationTests
{
    public static IEnumerable<object[]> TrackingRaceCases() =>
        from postgres in new[] { false, true }
        from writeWins in new[] { false, true }
        from change in new[] { "provider-logout", "epoch-revocation", "deletion-revocation", "family" }
        select new object[] { postgres, writeWins, change };

    [Theory]
    [MemberData(nameof(TrackingRaceCases))]
    public async Task Tracking_ConcurrentRevocation_CommitsOnlyBeforeWinningRevocation(
        bool postgres, bool writeWins, string change)
    {
        if (!IntegrationHost.CanRun(postgres)) return;
        await using var host = await IntegrationHost.CreateAsync(write: true, postgres: postgres);
        var token = await host.IssueAsync();
        var entered = Signal();
        var release = Signal();
        if (writeWins)
            host.BeforeTrackingSave = async cancellationToken =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            };
        else
            host.BeforeWrite = async _ =>
            {
                // Authorization has already validated the real reference access token here.
                entered.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(20));
            };

        var write = Task.Run(() => host.SendAsync(HttpMethod.Patch, "/api/library/101/personal", token,
            "{\"playStatus\":1,\"userRating\":4}"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            if (writeWins)
            {
                host.LifecycleStarting = Signal();
                var revoke = Task.Run(() => host.RevokeForTrackingAsync(change));
                await host.LifecycleStarting.Task.WaitAsync(TimeSpan.FromSeconds(20));
                // The write owns admission locks; the lifecycle transaction is already starting.
                Assert.False(revoke.IsCompleted);
                await Assert.ThrowsAsync<TimeoutException>(() => revoke.WaitAsync(TimeSpan.FromMilliseconds(200)));
                release.TrySetResult();
                Assert.Equal(HttpStatusCode.OK, (await write.WaitAsync(TimeSpan.FromSeconds(30))).Status);
                await revoke.WaitAsync(TimeSpan.FromSeconds(30));
            }
            else
            {
                await host.RevokeForTrackingAsync(change);
                release.TrySetResult();
                Assert.Equal(HttpStatusCode.Forbidden, (await write.WaitAsync(TimeSpan.FromSeconds(30))).Status);
            }
        }
        finally { release.TrySetResult(); }

        host.BeforeWrite = null;
        host.BeforeTrackingSave = null;
        var owned = await host.GameAsync(101);
        Assert.Equal(writeWins ? 4 : (int?)null, owned.UserRating);
        Assert.Equal(writeWins ? 1 : 0, (int)owned.PlayStatus);
        Assert.Equal(writeWins, owned.IsPlayStatusManual);
        var foreign = await host.GameAsync(202);
        Assert.Null(foreign.UserRating);
        Assert.Equal(0, (int)foreign.PlayStatus);
        Assert.False(foreign.IsPlayStatusManual);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendAsync(HttpMethod.Patch,
            "/api/library/101/personal", token, "{\"playStatus\":2,\"userRating\":5}")).Status);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Tracking_SaveOrCommitFailure_RollsBackAndReturnsNoSuccess(bool postgres, bool commitFailure)
    {
        if (!IntegrationHost.CanRun(postgres)) return;
        await using var host = await IntegrationHost.CreateAsync(write: true, postgres: postgres);
        var token = await host.IssueAsync();
        host.FailWriteCommit = commitFailure;
        host.FailTrackingSave = !commitFailure;
        var response = await host.SendAsync(HttpMethod.Patch, "/api/library/101/personal", token,
            "{\"playStatus\":1,\"userRating\":4}");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.Status);
        Assert.DoesNotContain("successfully", response.Body, StringComparison.Ordinal);
        Assert.Null((await host.GameAsync(101)).UserRating);
        Assert.False((await host.GameAsync(101)).IsPlayStatusManual);
        host.FailWriteCommit = false;
        host.FailTrackingSave = false;
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Patch,
            "/api/library/101/personal", token, "{\"playStatus\":1,\"userRating\":3}")).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tracking_RequestCancellation_RollsBackBeforeCommit(bool postgres)
    {
        if (!IntegrationHost.CanRun(postgres)) return;
        await using var host = await IntegrationHost.CreateAsync(write: true, postgres: postgres);
        var token = await host.IssueAsync();
        var entered = Signal();
        host.WriteCompleted = Signal();
        host.BeforeTrackingSave = async cancellationToken =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var write = host.SendAsync(HttpMethod.Patch, "/api/library/101/personal", token,
            "{\"playStatus\":1,\"userRating\":4}", cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await write);
        await host.WriteCompleted.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Null((await host.GameAsync(101)).UserRating);
        Assert.False((await host.GameAsync(101)).IsPlayStatusManual);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("foreign-request")]
    [InlineData("mixed-request")]
    [InlineData("storage-failure")]
    public async Task Tracking_MissingProofOrChangedRequestIdentity_DeniesMutation(string change)
    {
        await using var host = await IntegrationHost.CreateAsync(write: true);
        var token = await host.IssueAsync();
        host.BeforeWrite = async http =>
        {
            if (change == "missing") http.Features.Set<McpValidatedIdentityFeature>(null);
            else if (change == "storage-failure") await host.InvalidateAsync("database-outage");
            else if (change == "mixed-request")
                http.User.AddIdentity(new ClaimsIdentity([new Claim(GameHubClaimTypes.TenantId, Guid.NewGuid().ToString())], "other"));
            else
            {
                var identity = (ClaimsIdentity)http.User.Identity!;
                identity.RemoveClaim(identity.FindFirst(GameHubClaimTypes.TenantId)!);
                identity.AddClaim(new(GameHubClaimTypes.TenantId, Guid.NewGuid().ToString()));
            }
        };
        Assert.Equal(change == "storage-failure" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Forbidden,
            (await host.SendAsync(HttpMethod.Patch, "/api/library/101/personal", token,
                "{\"playStatus\":1,\"userRating\":4}")).Status);
        Assert.Null((await host.GameAsync(101)).UserRating);
        Assert.Null((await host.GameAsync(202)).UserRating);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tracking_LocalBrowserLogout_PreservesGrantUntilActualProviderLogout(bool postgres)
    {
        if (!IntegrationHost.CanRun(postgres)) return;
        await using var host = await IntegrationHost.CreateAsync(write: true, postgres: postgres);
        var token = await host.IssueAsync();
        await host.InvalidateAsync("browser-family");
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Patch,
            "/api/library/101/personal", token, "{\"playStatus\":1,\"userRating\":4}")).Status);
        await host.RevokeForTrackingAsync("provider-logout");
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendAsync(HttpMethod.Patch,
            "/api/library/101/personal", token, "{\"playStatus\":2,\"userRating\":5}")).Status);
        Assert.Equal(4, (await host.GameAsync(101)).UserRating);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tracking_WebAndOperatorCredentials_PreserveExistingMutationBehavior(bool browser)
    {
        await using var host = await IntegrationHost.CreateAsync(validation: false);
        using var request = new HttpRequestMessage(HttpMethod.Patch, "/api/library/101/personal");
        if (browser) request.Headers.Authorization = new("Bearer", host.BrowserToken());
        else request.Headers.Add("X-Api-Key", IntegrationHost.OperatorKey);
        request.Content = new StringContent("{\"playStatus\":1,\"userRating\":4,\"isIgnored\":true}",
            System.Text.Encoding.UTF8, "application/json");
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(4, (await host.GameAsync(101)).UserRating);
        Assert.True((await host.GameAsync(101)).IsIgnored);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed partial class IntegrationHost
    {
        public Func<HttpContext, Task>? BeforeWrite { get; set; }
        public Func<CancellationToken, Task>? BeforeTrackingSave { get; set; }
        public TaskCompletionSource? LifecycleStarting { get; set; }
        public TaskCompletionSource? WriteCompleted { get; set; }
        public bool FailWriteCommit { get; set; }
        public bool FailTrackingSave { get; set; }

        public async Task RevokeForTrackingAsync(string change)
        {
            if (change == "family") { await InvalidateAsync(change); return; }
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
            if (change == "provider-logout")
            {
                var service = new OidcBackChannelLogoutService(db, new TenantExecutionContext(),
                    TimeProvider.System, NullLogger<OidcBackChannelLogoutService>.Instance);
                await service.ProcessAsync(new("https://provider.example", null, Guid.NewGuid().ToString(),
                    DateTimeOffset.UtcNow, "tracking-test-sid"));
                return;
            }
            await using var transaction = await db.Database.BeginTransactionAsync();
            if (change == "deletion-revocation")
                await db.Tenants.Where(row => row.Id == TenantDefaults.DefaultTenantId)
                    .ExecuteUpdateAsync(update => update.SetProperty(row => row.DeletionRequestedAtUtc, DateTime.UtcNow));
            await db.UserAccounts.Where(row => row.Id == accountId)
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.SessionVersion, row => row.SessionVersion + 1));
            await AccountSessionRevocation.RevokeAllFamiliesAsync(db, accountId, TenantDefaults.DefaultTenantId, DateTime.UtcNow);
            await transaction.CommitAsync();
        }

        private sealed class WriteSaveInterceptor(IntegrationHost host) : SaveChangesInterceptor
        {
            public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
                InterceptionResult<int> result, CancellationToken cancellationToken = default)
            {
                if (eventData.Context!.ChangeTracker.Entries<UnifiedGame>().Any(entry => entry.State == EntityState.Modified))
                {
                    if (host.FailTrackingSave) throw new DbUpdateException("Synthetic save failure.");
                    if (host.BeforeTrackingSave is not null) await host.BeforeTrackingSave(cancellationToken);
                }
                return result;
            }
        }

        private sealed class WriteTransactionInterceptor(IntegrationHost host) : DbTransactionInterceptor
        {
            public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
                TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result,
                CancellationToken cancellationToken = default)
            {
                host.LifecycleStarting?.TrySetResult();
                return ValueTask.FromResult(result);
            }

            public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
                TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
            {
                if (host.FailWriteCommit && eventData.Context!.ChangeTracker.Entries<UnifiedGame>().Any())
                    throw new InvalidOperationException("Synthetic commit failure.");
                return ValueTask.FromResult(result);
            }
        }
    }
}
