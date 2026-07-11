using System.Net;
using System.Text;
using System.Text.Json;
using GameHub.Core.Data;
using GameHub.Core.Models.ProviderSync;
using GameHub.Core.Services;
using GameHub.Shared;
using GameHub.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace GameHub.Tests;

/// <summary>Verifies the secret-free, tenant-scoped provider readiness read model.</summary>
public sealed class ProviderSyncStatusServiceTests : IDisposable
{
    private static readonly Guid TenantA = TenantDefaults.DefaultTenantId;
    private static readonly Guid TenantB = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset Now = new(2030, 2, 3, 4, 5, 6, TimeSpan.Zero);
    private readonly MutableTenantContext _tenantContext = new(TenantA);
    private readonly MutableTimeProvider _timeProvider = new(Now);
    private readonly CoreContext _db;
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;

    public ProviderSyncStatusServiceTests()
    {
        (_db, _connection) = DatabaseFixture.CreateCoreContext(_tenantContext);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task GetStatusAsync_UsesConfiguredFreshnessBoundary()
    {
        await SeedRefreshAsync(Platform.Steam, Now.AddMinutes(-14).UtcDateTime);
        await SeedRefreshAsync(Platform.Gog, Now.AddMinutes(-15).UtcDateTime);

        var result = await CreateService(15, Platform.Steam, Platform.Gog).GetStatusAsync();

        var steam = result.Providers.Single(item => item.Platform == Platform.Steam);
        var gog = result.Providers.Single(item => item.Platform == Platform.Gog);
        Assert.Equal(ProviderFreshnessStatus.Fresh, steam.FreshnessStatus);
        Assert.Equal(Now.AddMinutes(1).UtcDateTime, steam.FreshUntil);
        Assert.Equal(ProviderFreshnessStatus.Stale, gog.FreshnessStatus);
        Assert.Equal(Now.UtcDateTime, gog.FreshUntil);
    }

    [Fact]
    public async Task GetStatusAsync_NeverRefreshedAndNotConfigured_ReturnsDistinctStates()
    {
        await SeedRefreshAsync(Platform.Steam, Now.AddMinutes(-1).UtcDateTime);

        var result = await CreateService(30, Platform.Gog).GetStatusAsync();

        var steam = result.Providers.Single(item => item.Platform == Platform.Steam);
        var gog = result.Providers.Single(item => item.Platform == Platform.Gog);
        Assert.Equal(ProviderFreshnessStatus.NotConfigured, steam.FreshnessStatus);
        Assert.Null(steam.CooldownUntil);
        Assert.Equal(ProviderFreshnessStatus.NeverRefreshed, gog.FreshnessStatus);
        Assert.Null(gog.LastSuccessfulRefreshAt);
        Assert.Null(gog.FreshUntil);
    }

    [Fact]
    public async Task GetStatusAsync_ActiveAndElapsedCooldown_UsesLocalReadOnlyState()
    {
        await SeedRefreshAsync(Platform.Steam, Now.AddMinutes(-1).UtcDateTime);
        var cooldown = new StoredRetrievalCooldown(_timeProvider);
        cooldown.SetRetryAfter(TenantA, Platform.Steam, Now.AddMinutes(5));
        var service = CreateService(30, cooldown, Platform.Steam);

        var active = (await service.GetStatusAsync()).Providers.Single(item => item.Platform == Platform.Steam);
        Assert.Equal(ProviderFreshnessStatus.Cooldown, active.FreshnessStatus);
        Assert.Equal(Now.AddMinutes(5), active.CooldownUntil);

        _timeProvider.UtcNow = Now.AddMinutes(6);
        var elapsed = (await service.GetStatusAsync()).Providers.Single(item => item.Platform == Platform.Steam);
        Assert.Equal(ProviderFreshnessStatus.Fresh, elapsed.FreshnessStatus);
        Assert.Null(elapsed.CooldownUntil);
        Assert.False(cooldown.TryPeekActiveRetryAfter(TenantA, Platform.Steam, out _));
    }

    [Fact]
    public async Task GetStatusAsync_NotConfiguredTakesPriorityOverCooldown()
    {
        await SeedRefreshAsync(Platform.Steam, Now.AddMinutes(-1).UtcDateTime);
        var cooldown = new StoredRetrievalCooldown(_timeProvider);
        cooldown.SetRetryAfter(TenantA, Platform.Steam, Now.AddMinutes(5));

        var result = await CreateService(30, cooldown).GetStatusAsync();
        var steam = result.Providers.Single(item => item.Platform == Platform.Steam);

        Assert.Equal(ProviderFreshnessStatus.NotConfigured, steam.FreshnessStatus);
        Assert.Null(steam.CooldownUntil);
    }

    [Fact]
    public async Task GetStatusAsync_IsolatedByAuthenticatedTenant()
    {
        _db.Tenants.Add(new Tenant
        {
            Id = TenantB,
            Name = "Tenant B",
            CreatedAtUtc = Now.UtcDateTime
        });
        await _db.SaveChangesAsync();
        await SeedRefreshAsync(Platform.Steam, Now.AddMinutes(-1).UtcDateTime);

        _tenantContext.TenantId = TenantB;
        await SeedRefreshAsync(Platform.Steam, Now.AddHours(-1).UtcDateTime);

        _tenantContext.TenantId = TenantA;
        var service = CreateService(30, Platform.Steam);
        var tenantA = (await service.GetStatusAsync()).Providers.Single(item => item.Platform == Platform.Steam);

        _tenantContext.TenantId = TenantB;
        var tenantB = (await service.GetStatusAsync()).Providers.Single(item => item.Platform == Platform.Steam);

        Assert.Equal(ProviderFreshnessStatus.Fresh, tenantA.FreshnessStatus);
        Assert.Equal(ProviderFreshnessStatus.Stale, tenantB.FreshnessStatus);
        Assert.Equal(Now.AddMinutes(-1).UtcDateTime, tenantA.LastSuccessfulRefreshAt);
        Assert.Equal(Now.AddHours(-1).UtcDateTime, tenantB.LastSuccessfulRefreshAt);
    }

    [Fact]
    public async Task GetStatusAsync_DoesNotMutateOrExposeSecretsOrIdentifiers()
    {
        await SeedRefreshAsync(Platform.Epic, Now.AddMinutes(-1).UtcDateTime);
        _db.ChangeTracker.Clear();
        var beforeCount = await _db.SyncMetadata.CountAsync();

        var result = await CreateService(30, Platform.Epic).GetStatusAsync();

        Assert.Equal(beforeCount, await _db.SyncMetadata.CountAsync());
        Assert.False(_db.ChangeTracker.HasChanges());
        var responseJson = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("Tenant", responseJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", responseJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", responseJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", responseJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(typeof(ProviderSyncStatus).GetProperties(),
            property => property.Name.Contains("Id", StringComparison.OrdinalIgnoreCase));
    }

    private ProviderSyncStatusService CreateService(
        int freshnessMinutes,
        params Platform[] configuredPlatforms) =>
        CreateService(
            freshnessMinutes,
            new StoredRetrievalCooldown(_timeProvider),
            configuredPlatforms);

    private ProviderSyncStatusService CreateService(
        int freshnessMinutes,
        StoredRetrievalCooldown cooldown,
        params Platform[] configuredPlatforms)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:IngestServiceKey"] = "test-service-key",
                ["Sync:ProviderFreshnessMinutes"] = freshnessMinutes.ToString()
            })
            .Build();
        var client = new IngestClient(
            new HttpClient(new CredentialStatusHandler(configuredPlatforms))
            {
                BaseAddress = new Uri("http://ingest.test")
            },
            _tenantContext,
            configuration);
        return new ProviderSyncStatusService(
            _db,
            _tenantContext,
            client,
            cooldown,
            configuration,
            _timeProvider);
    }

    private async Task SeedRefreshAsync(Platform platform, DateTime refreshedAtUtc)
    {
        _db.SyncMetadata.Add(new SyncMetadata
        {
            Platform = platform,
            LastSyncedAt = DateTime.UnixEpoch,
            LastSuccessfulProviderRetrievalAtUtc = refreshedAtUtc
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    private sealed class CredentialStatusHandler(params Platform[] configuredPlatforms) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/internal/platform-credentials", request.RequestUri?.AbsolutePath);
            var configured = configuredPlatforms.ToHashSet();
            var body = JsonSerializer.Serialize(Enum.GetValues<Platform>().Select(platform => new
            {
                platform = platform.ToString(),
                isConfigured = configured.Contains(platform),
                schemaVersion = configured.Contains(platform) ? 1 : (int?)null,
                updatedAtUtc = configured.Contains(platform) ? Now.UtcDateTime : (DateTime?)null
            }));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class MutableTenantContext(Guid tenantId) : ITenantContext
    {
        public Guid TenantId { get; set; } = tenantId;
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
