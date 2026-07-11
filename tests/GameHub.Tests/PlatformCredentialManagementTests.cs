using System.Net;
using System.Text;
using System.Text.Json;
using GameHub.Core.Data;
using GameHub.Core.Models.PlatformCredentials;
using GameHub.Core.Services;
using GameHub.Shared;
using GameHub.Shared.Authentication;
using GameHub.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameHub.Tests;

/// <summary>Core credential orchestration, activation policy, and tenant-isolation tests.</summary>
public sealed class PlatformCredentialManagementTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly MutableTenantContext _tenantContext = new() { TenantId = TenantA };
    private readonly CoreContext _db;
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;

    public PlatformCredentialManagementTests()
    {
        (_db, _connection) = DatabaseFixture.CreateCoreContext(_tenantContext);
        _db.Tenants.AddRange(
            new Tenant
            {
                Id = TenantA,
                Name = "Tenant A",
                PlatformSyncEnabled = false,
                CreatedAtUtc = DateTime.UtcNow
            },
            new Tenant
            {
                Id = TenantB,
                Name = "Tenant B",
                PlatformSyncEnabled = false,
                CreatedAtUtc = DateTime.UtcNow
            });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task UpsertAsync_VerifiedCredential_EnablesOnlyActiveTenant()
    {
        var handler = new SequenceHandler(
            JsonResponse(HttpStatusCode.OK, "{}"),
            CredentialStatusResponse((Platform.Steam, true)));
        var service = CreateService(handler);

        var result = await service.UpsertAsync(
            Platform.Steam,
            new UpsertPlatformCredentialRequest
            {
                Credentials = ParseCredentials(
                    "{\"apiKey\":\"tenant-a-secret\",\"steamId\":\"76561198012345678\"}")
            });

        _db.ChangeTracker.Clear();
        Assert.True(result.PlatformSyncEnabled);
        Assert.True((await _db.Tenants.SingleAsync(tenant => tenant.Id == TenantA)).PlatformSyncEnabled);
        Assert.False((await _db.Tenants.SingleAsync(tenant => tenant.Id == TenantB)).PlatformSyncEnabled);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(
                TenantA.ToString("D"),
                request.TenantHeader);
            Assert.Equal("test-service-key", request.ServiceKeyHeader);
        });
        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.Equal("/api/internal/platform-credentials/steam", handler.Requests[0].Path);
        Assert.Contains("tenant-a-secret", handler.Requests[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpsertAsync_EpicCredential_EnablesTenantAfterVaultVerification()
    {
        var handler = new SequenceHandler(
            JsonResponse(HttpStatusCode.OK, "{}"),
            CredentialStatusResponse((Platform.Epic, true)));
        var service = CreateService(handler);

        var result = await service.UpsertAsync(
            Platform.Epic,
            new UpsertPlatformCredentialRequest
            {
                Credentials = ParseCredentials("{\"refreshToken\":\"epic-refresh\"}")
            });

        _db.ChangeTracker.Clear();
        Assert.True(result.PlatformSyncEnabled);
        Assert.True((await _db.Tenants.SingleAsync(tenant => tenant.Id == TenantA)).PlatformSyncEnabled);
        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.Equal("/api/internal/platform-credentials/epic", handler.Requests[0].Path);
        Assert.Contains("epic-refresh", handler.Requests[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpsertAsync_EpicAuthorizationCode_ForwardsCodeAndReturnsSecretFreeStatus()
    {
        var handler = new SequenceHandler(
            JsonResponse(HttpStatusCode.OK, "{}"),
            CredentialStatusResponse((Platform.Epic, true)));
        var service = CreateService(handler);

        var result = await service.UpsertAsync(
            Platform.Epic,
            new UpsertPlatformCredentialRequest
            {
                Credentials = ParseCredentials("{\"authorizationCode\":\"short-lived-code\"}")
            });

        var resultJson = JsonSerializer.Serialize(result);
        Assert.True(result.PlatformSyncEnabled);
        Assert.Equal("/api/internal/platform-credentials/epic", handler.Requests[0].Path);
        Assert.Contains("short-lived-code", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("short-lived-code", resultJson, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh", resultJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpsertAsync_StatusVerificationFails_DoesNotEnableTenant()
    {
        var handler = new SequenceHandler(
            JsonResponse(HttpStatusCode.OK, "{}"),
            JsonResponse(HttpStatusCode.ServiceUnavailable, "{}"));
        var service = CreateService(handler);

        await Assert.ThrowsAsync<IngestPlatformCredentialException>(() =>
            service.UpsertAsync(
                Platform.Gog,
                new UpsertPlatformCredentialRequest
                {
                    Credentials = ParseCredentials(
                        "{\"cookie\":\"tenant-a-secret\",\"user\":\"tenant-a\"}")
                }));

        _db.ChangeTracker.Clear();
        Assert.False((await _db.Tenants.SingleAsync(tenant => tenant.Id == TenantA)).PlatformSyncEnabled);
    }

    [Fact]
    public async Task DeleteAsync_FinalCredential_DisablesOnlyActiveTenantBeforeDeletion()
    {
        await SetSyncEnabledAsync(TenantA, true);
        await SetSyncEnabledAsync(TenantB, true);
        var handler = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.NoContent),
            CredentialStatusResponse());
        var service = CreateService(handler);

        var result = await service.DeleteAsync(Platform.Psn);

        _db.ChangeTracker.Clear();
        Assert.False(result.PlatformSyncEnabled);
        Assert.False((await _db.Tenants.SingleAsync(tenant => tenant.Id == TenantA)).PlatformSyncEnabled);
        Assert.True((await _db.Tenants.SingleAsync(tenant => tenant.Id == TenantB)).PlatformSyncEnabled);
        Assert.Equal(HttpMethod.Delete, handler.Requests[0].Method);
        Assert.Equal("/api/internal/platform-credentials/psn", handler.Requests[0].Path);
    }

    [Fact]
    public async Task DeleteAsync_AnotherCredentialRemains_ReenablesActiveTenant()
    {
        await SetSyncEnabledAsync(TenantA, true);
        var handler = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.NoContent),
            CredentialStatusResponse((Platform.Gog, true)));
        var service = CreateService(handler);

        var result = await service.DeleteAsync(Platform.Steam);

        _db.ChangeTracker.Clear();
        Assert.True(result.PlatformSyncEnabled);
        Assert.True((await _db.Tenants.SingleAsync(tenant => tenant.Id == TenantA)).PlatformSyncEnabled);
    }

    [Fact]
    public async Task DeleteAsync_IngestFailure_LeavesActiveTenantFailClosed()
    {
        await SetSyncEnabledAsync(TenantA, true);
        var handler = new SequenceHandler(JsonResponse(
            HttpStatusCode.ServiceUnavailable,
            "{\"message\":\"secret-bearing upstream detail\"}"));
        var service = CreateService(handler);

        var exception = await Assert.ThrowsAsync<IngestPlatformCredentialException>(
            () => service.DeleteAsync(Platform.Steam));

        _db.ChangeTracker.Clear();
        Assert.False((await _db.Tenants.SingleAsync(tenant => tenant.Id == TenantA)).PlatformSyncEnabled);
        Assert.Equal("Platform credential storage is unavailable.", exception.SafeMessage);
        Assert.DoesNotContain("secret-bearing", exception.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetrieveAsync_EnabledTenant_UsesStoredDelegatedRoute()
    {
        await SetSyncEnabledAsync(TenantA, true);
        var handler = new SequenceHandler(JsonResponse(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(new StoredRetrievalResponse(true))));
        var service = CreateService(handler);

        await service.RetrieveAsync(Platform.Gog);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/retrieve/gog/stored", request.Path);
        Assert.Equal(TenantA.ToString("D"), request.TenantHeader);
        Assert.Equal("test-service-key", request.ServiceKeyHeader);
        var freshness = await _db.SyncMetadata.SingleAsync(item => item.Platform == Platform.Gog);
        Assert.NotNull(freshness.LastSuccessfulProviderRetrievalAtUtc);
    }

    [Fact]
    public async Task RetrieveAsync_DisabledTenant_RejectsBeforeCallingIngest()
    {
        var handler = new SequenceHandler();
        var service = CreateService(handler);

        await Assert.ThrowsAsync<TenantPlatformSyncDisabledException>(
            () => service.RetrieveAsync(Platform.Steam));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetRetrievalHistoryAsync_ActiveTenant_UsesInternalDelegation()
    {
        var handler = new SequenceHandler(JsonResponse(
            HttpStatusCode.OK,
            "[{\"id\":1,\"timestamp\":\"2026-06-21T10:00:00Z\",\"platform\":\"Steam\",\"status\":\"Success\",\"itemsProcessed\":42,\"errorMessage\":null}]"));
        var service = CreateService(handler);

        var logs = await service.GetRetrievalHistoryAsync("Steam", 500);

        var log = Assert.Single(logs);
        Assert.Equal(42, log.ItemsProcessed);
        Assert.Equal("/api/retrieve/history", handler.Requests[0].Path);
        Assert.Contains("limit=200", handler.Requests[0].Query, StringComparison.Ordinal);
        Assert.Contains("platform=Steam", handler.Requests[0].Query, StringComparison.Ordinal);
        Assert.Equal(TenantA.ToString("D"), handler.Requests[0].TenantHeader);
    }

    private PlatformCredentialManagementService CreateService(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://ingest.test") };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:IngestServiceKey"] = "test-service-key"
            })
            .Build();
        var ingestClient = new IngestClient(http, _tenantContext, configuration);
        return new PlatformCredentialManagementService(
            _db,
            _tenantContext,
            ingestClient,
            NullLogger<PlatformCredentialManagementService>.Instance,
            TimeProvider.System);
    }

    private async Task SetSyncEnabledAsync(Guid tenantId, bool enabled)
    {
        var tenant = await _db.Tenants.SingleAsync(item => item.Id == tenantId);
        tenant.PlatformSyncEnabled = enabled;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    private static JsonElement ParseCredentials(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static HttpResponseMessage CredentialStatusResponse(
        params (Platform Platform, bool IsConfigured)[] configured)
    {
        var configuredMap = configured.ToDictionary(item => item.Platform, item => item.IsConfigured);
        var statuses = Enum.GetValues<Platform>().Select(platform => new
        {
            platform = platform.ToString(),
            isConfigured = configuredMap.GetValueOrDefault(platform),
            schemaVersion = configuredMap.GetValueOrDefault(platform) ? 1 : (int?)null,
            updatedAtUtc = configuredMap.GetValueOrDefault(platform)
                ? "2026-06-21T10:00:00Z"
                : null
        });
        return JsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(statuses));
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class SequenceHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri?.AbsolutePath ?? "",
                request.RequestUri?.Query ?? "",
                request.Headers.GetValues(
                    TenantDelegationAuthenticationHandler.TenantHeaderName).Single(),
                request.Headers.GetValues(
                    TenantDelegationAuthenticationHandler.ServiceKeyHeaderName).Single(),
                body));

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No response was configured for the captured request.");
            }

            return _responses.Dequeue();
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        string Path,
        string Query,
        string TenantHeader,
        string ServiceKeyHeader,
        string Body);

    private sealed class MutableTenantContext : ITenantContext
    {
        public Guid TenantId { get; set; }
    }
}
