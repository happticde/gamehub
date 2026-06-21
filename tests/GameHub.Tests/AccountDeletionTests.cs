using System.Net;
using GameHub.Core.Data;
using GameHub.Core.Services;
using GameHub.Shared;
using GameHub.Shared.Authentication;
using GameHub.Tests.Helpers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameHub.Tests;

/// <summary>Cross-service account deletion, retry, and tenant-isolation tests.</summary>
public sealed class AccountDeletionTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private static readonly DateTimeOffset FixedNow =
        new(2026, 6, 21, 14, 0, 0, TimeSpan.Zero);

    private readonly MutableTenantContext _tenantContext = new();
    private readonly CoreContext _db;
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly AccountService _accounts;
    private readonly RefreshTokenService _refresh;
    private readonly SyncSchedulerStatus _syncStatus = new();

    public AccountDeletionTests()
    {
        (_db, _connection) = DatabaseFixture.CreateCoreContext(_tenantContext);
        var time = new AccountServiceTests.FixedTimeProvider(FixedNow);
        _accounts = new AccountService(_db, new PasswordHasher<UserAccount>(), time);
        _refresh = new RefreshTokenService(_db, AccountServiceTests.CreateJwtSettings(), time);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Delete_TenantA_RemovesOnlyTenantADataAndIsIdempotent()
    {
        var (accountA, accountB) = await SeedTwoTenantsAsync();
        await SeedCoreDataAsync(accountA.TenantId, "tenant-a");
        await SeedCoreDataAsync(accountB.TenantId, "tenant-b");
        var refreshA = await _refresh.IssueAsync(accountA);
        var refreshB = await _refresh.IssueAsync(accountB);
        _syncStatus.MarkFailed(accountA.TenantId, "tenant-a scheduler detail");
        _syncStatus.MarkCompleted(accountB.TenantId, 2);
        _tenantContext.TenantId = accountA.TenantId;
        var handler = new SequenceHandler(new HttpResponseMessage(HttpStatusCode.NoContent));
        var deletion = CreateDeletionService(handler);

        var result = await deletion.DeleteAsync(
            accountA.Id,
            accountA.TenantId,
            Password);
        var repeated = await deletion.DeleteAsync(
            accountA.Id,
            accountA.TenantId,
            Password);

        Assert.Equal(AccountDeletionResult.Deleted, result);
        Assert.Equal(AccountDeletionResult.AlreadyDeleted, repeated);
        Assert.Single(handler.Requests);
        Assert.Equal("/api/internal/tenant", handler.Requests[0].Path);
        Assert.Equal(accountA.TenantId.ToString("D"), handler.Requests[0].TenantId);
        Assert.False((await _refresh.RotateAsync(refreshA.RefreshToken)).Rotated);
        Assert.True((await _refresh.RotateAsync(refreshB.RefreshToken)).Rotated);

        Assert.False(await _db.Tenants.AnyAsync(tenant => tenant.Id == accountA.TenantId));
        Assert.False(await _db.UserAccounts.AnyAsync(account => account.Id == accountA.Id));
        Assert.Equal(0, await CountCoreRowsAsync(accountA.TenantId));
        Assert.True(await _db.Tenants.AnyAsync(tenant => tenant.Id == accountB.TenantId));
        Assert.True(await _db.UserAccounts.AnyAsync(account => account.Id == accountB.Id));
        Assert.Equal(9, await CountCoreRowsAsync(accountB.TenantId));
        Assert.Null(_syncStatus.GetSnapshot(accountA.TenantId).LastResult);
        Assert.Equal("Success", _syncStatus.GetSnapshot(accountB.TenantId).LastResult);
    }

    [Fact]
    public async Task Delete_TenantAIdentityCannotDeleteTenantB()
    {
        var (accountA, accountB) = await SeedTwoTenantsAsync();
        await SeedCoreDataAsync(accountB.TenantId, "tenant-b");
        _tenantContext.TenantId = accountA.TenantId;
        var handler = new SequenceHandler(new HttpResponseMessage(HttpStatusCode.NoContent));
        var deletion = CreateDeletionService(handler);

        var result = await deletion.DeleteAsync(accountB.Id, accountA.TenantId, Password);

        Assert.Equal(AccountDeletionResult.IdentityMismatch, result);
        Assert.Empty(handler.Requests);
        Assert.True(await _db.UserAccounts.AnyAsync(account => account.Id == accountB.Id));
        Assert.Equal(9, await CountCoreRowsAsync(accountB.TenantId));
    }

    [Fact]
    public async Task Delete_IngestFailure_RemainsPendingAndRetryCompletes()
    {
        var (accountA, accountB) = await SeedTwoTenantsAsync();
        await SeedCoreDataAsync(accountA.TenantId, "tenant-a");
        var refreshA = await _refresh.IssueAsync(accountA);
        var refreshB = await _refresh.IssueAsync(accountB);
        _tenantContext.TenantId = accountA.TenantId;
        var handler = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            new HttpResponseMessage(HttpStatusCode.NoContent));
        var deletion = CreateDeletionService(handler);

        var failed = await deletion.DeleteAsync(accountA.Id, accountA.TenantId, Password);

        Assert.Equal(AccountDeletionResult.IngestCleanupFailed, failed);
        _db.ChangeTracker.Clear();
        var pendingTenant = await _db.Tenants.SingleAsync(tenant => tenant.Id == accountA.TenantId);
        Assert.Equal(FixedNow.UtcDateTime, pendingTenant.DeletionRequestedAtUtc);
        Assert.False(pendingTenant.PlatformSyncEnabled);
        Assert.True(await _db.UserAccounts.AnyAsync(account => account.Id == accountA.Id));
        Assert.Equal(9, await CountCoreRowsAsync(accountA.TenantId));
        Assert.False((await _refresh.RotateAsync(refreshA.RefreshToken)).Rotated);
        Assert.True((await _refresh.RotateAsync(refreshB.RefreshToken)).Rotated);

        var retried = await deletion.DeleteAsync(accountA.Id, accountA.TenantId, Password);

        Assert.Equal(AccountDeletionResult.Deleted, retried);
        Assert.Equal(2, handler.Requests.Count);
        Assert.False(await _db.Tenants.AnyAsync(tenant => tenant.Id == accountA.TenantId));
    }

    private async Task<(UserAccount AccountA, UserAccount AccountB)> SeedTwoTenantsAsync()
    {
        var tenantA = await _accounts.ProvisionTenantAsync(
            "Tenant A",
            "tenant-a@example.com",
            Password,
            "Tenant A Owner");
        var tenantB = await _accounts.ProvisionTenantAsync(
            "Tenant B",
            "tenant-b@example.com",
            Password,
            "Tenant B Owner");
        return (tenantA.Account!, tenantB.Account!);
    }

    private async Task SeedCoreDataAsync(Guid tenantId, string marker)
    {
        _tenantContext.TenantId = tenantId;
        var tag = new Tag { Name = $"{marker}-tag" };
        var game = new UnifiedGame
        {
            Title = marker,
            Slug = marker,
            Tags = [tag],
            Items =
            [
                new LibraryItem
                {
                    Platform = Platform.Steam,
                    ExternalId = $"{marker}-item"
                }
            ]
        };
        _db.UnifiedGames.Add(game);
        await _db.SaveChangesAsync();
        _db.ManualLinks.Add(new ManualLink
        {
            Platform = Platform.Steam,
            ExternalId = $"{marker}-manual",
            UnifiedGameId = game.Id
        });
        _db.EnrichmentAuditLogs.Add(new EnrichmentAuditLog
        {
            UnifiedGameId = game.Id,
            ProviderName = "test",
            Status = "Success"
        });
        _db.SyncMetadata.Add(new SyncMetadata
        {
            Platform = Platform.Steam,
            LastSyncedAt = FixedNow.UtcDateTime
        });
        _db.SyncAuditLogs.Add(new SyncAuditLog
        {
            Platform = Platform.Steam,
            Status = "Success"
        });
        _db.ActivityLogs.Add(new ActivityLog
        {
            EventType = "test",
            Message = marker
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    private async Task<int> CountCoreRowsAsync(Guid tenantId) =>
        await _db.Set<TagAssignment>().IgnoreQueryFilters().CountAsync(row => row.TenantId == tenantId)
        + await _db.LibraryItems.IgnoreQueryFilters().CountAsync(row => row.TenantId == tenantId)
        + await _db.ManualLinks.IgnoreQueryFilters().CountAsync(row => row.TenantId == tenantId)
        + await _db.EnrichmentAuditLogs.IgnoreQueryFilters().CountAsync(row => row.TenantId == tenantId)
        + await _db.SyncMetadata.IgnoreQueryFilters().CountAsync(row => row.TenantId == tenantId)
        + await _db.SyncAuditLogs.IgnoreQueryFilters().CountAsync(row => row.TenantId == tenantId)
        + await _db.ActivityLogs.IgnoreQueryFilters().CountAsync(row => row.TenantId == tenantId)
        + await _db.Tags.IgnoreQueryFilters().CountAsync(row => row.TenantId == tenantId)
        + await _db.UnifiedGames.IgnoreQueryFilters().CountAsync(row => row.TenantId == tenantId);

    private AccountDeletionService CreateDeletionService(HttpMessageHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:IngestServiceKey"] = "account-deletion-test-key"
            })
            .Build();
        var client = new IngestClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://ingest.test") },
            _tenantContext,
            configuration);
        return new AccountDeletionService(
            _db,
            _accounts,
            client,
            _syncStatus,
            NullLogger<AccountDeletionService>.Instance);
    }

    private sealed class MutableTenantContext : ITenantContext
    {
        public Guid TenantId { get; set; } = TenantDefaults.DefaultTenantId;
    }

    private sealed class SequenceHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<CapturedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.RequestUri?.AbsolutePath ?? "",
                request.Headers.GetValues(
                    TenantDelegationAuthenticationHandler.TenantHeaderName).Single()));
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed record CapturedRequest(string Path, string TenantId);
}
