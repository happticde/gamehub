using System.Net;
using System.Text;
using System.Text.Json;
using GameHub.Core.Data;
using GameHub.Core.Models.ProviderSync;
using GameHub.Core.Services;
using GameHub.Shared;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace GameHub.Tests;

/// <summary>Verifies the secret-free, tenant-scoped provider refresh blocker read model.</summary>
public sealed class ProviderSyncStatusServiceTests
{
    private static readonly Guid TenantA = TenantDefaults.DefaultTenantId;
    private static readonly Guid TenantB = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset Now = new(2030, 2, 3, 4, 5, 6, TimeSpan.Zero);
    private readonly MutableTenantContext _tenantContext = new(TenantA);
    private readonly MutableTimeProvider _timeProvider = new(Now);

    [Fact]
    public async Task GetStatusAsync_ReturnsConfigurationStateForEverySupportedPlatform()
    {
        var result = await CreateService(Platform.Steam, Platform.Epic).GetStatusAsync();

        Assert.Equal(4, result.Providers.Count);
        Assert.True(result.Providers.Single(item => item.Platform == Platform.Steam).IsConfigured);
        Assert.True(result.Providers.Single(item => item.Platform == Platform.Epic).IsConfigured);
        Assert.False(result.Providers.Single(item => item.Platform == Platform.Gog).IsConfigured);
        Assert.False(result.Providers.Single(item => item.Platform == Platform.Psn).IsConfigured);
    }

    [Fact]
    public async Task GetStatusAsync_ActiveAndElapsedCooldown_ExposesOnlyActiveCooldown()
    {
        var cooldown = new StoredRetrievalCooldown(_timeProvider);
        cooldown.SetRetryAfter(TenantA, Platform.Steam, Now.AddMinutes(5));
        var service = CreateService(cooldown, Platform.Steam);

        var active = (await service.GetStatusAsync()).Providers.Single(item => item.Platform == Platform.Steam);
        Assert.Equal(Now.AddMinutes(5), active.CooldownUntil);

        _timeProvider.UtcNow = Now.AddMinutes(6);
        var elapsed = (await service.GetStatusAsync()).Providers.Single(item => item.Platform == Platform.Steam);
        Assert.Null(elapsed.CooldownUntil);
    }

    [Fact]
    public async Task GetStatusAsync_NotConfiguredPlatform_HidesHistoricalCooldown()
    {
        var cooldown = new StoredRetrievalCooldown(_timeProvider);
        cooldown.SetRetryAfter(TenantA, Platform.Steam, Now.AddMinutes(5));

        var steam = (await CreateService(cooldown).GetStatusAsync())
            .Providers.Single(item => item.Platform == Platform.Steam);

        Assert.False(steam.IsConfigured);
        Assert.Null(steam.CooldownUntil);
    }

    [Fact]
    public async Task GetStatusAsync_CooldownUsesAuthenticatedTenantOnly()
    {
        var cooldown = new StoredRetrievalCooldown(_timeProvider);
        cooldown.SetRetryAfter(TenantA, Platform.Steam, Now.AddMinutes(5));
        var service = CreateService(cooldown, Platform.Steam);

        var tenantA = (await service.GetStatusAsync()).Providers.Single(item => item.Platform == Platform.Steam);
        _tenantContext.TenantId = TenantB;
        var tenantB = (await service.GetStatusAsync()).Providers.Single(item => item.Platform == Platform.Steam);

        Assert.Equal(Now.AddMinutes(5), tenantA.CooldownUntil);
        Assert.Null(tenantB.CooldownUntil);
    }

    [Fact]
    public async Task GetStatusAsync_DoesNotMutateCooldownOrExposeSecretsOrIdentifiers()
    {
        var cooldown = new StoredRetrievalCooldown(_timeProvider);
        cooldown.SetRetryAfter(TenantA, Platform.Epic, Now.AddMinutes(5));

        var result = await CreateService(cooldown, Platform.Epic).GetStatusAsync();

        Assert.True(cooldown.TryGetRetryAfter(TenantA, Platform.Epic, out var retryAfterUtc));
        Assert.Equal(Now.AddMinutes(5), retryAfterUtc);
        var responseJson = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("Tenant", responseJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", responseJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", responseJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", responseJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(typeof(ProviderSyncStatus).GetProperties(),
            property => property.Name.Contains("Id", StringComparison.OrdinalIgnoreCase));
    }

    private ProviderSyncStatusService CreateService(params Platform[] configuredPlatforms) =>
        CreateService(new StoredRetrievalCooldown(_timeProvider), configuredPlatforms);

    private ProviderSyncStatusService CreateService(
        StoredRetrievalCooldown cooldown,
        params Platform[] configuredPlatforms)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:IngestServiceKey"] = "test-service-key"
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
            _tenantContext,
            client,
            cooldown);
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
