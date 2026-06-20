using System.Reflection;
using System.Text.Json;
using GameHub.Ingest.Controllers;
using GameHub.Ingest.Data;
using GameHub.Ingest.Services;
using GameHub.Shared;
using GameHub.Shared.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameHub.Tests;

/// <summary>Authorization and tenant-isolation coverage for vault-backed retrieval.</summary>
public sealed class StoredPlatformRetrievalTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<IngestContext> _options;
    private readonly PlatformCredentialProtector _protector = CreateProtector();

    public StoredPlatformRetrievalTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<IngestContext>()
            .UseSqlite(_connection)
            .Options;
        using var db = CreateContext(TenantA);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public void StoredEndpoint_RequiresInternalTenantDelegationScheme()
    {
        var method = typeof(RetrieveController).GetMethod(
            nameof(RetrieveController.RetrieveStored));
        var authorize = method?.GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(authorize);
        Assert.Equal(
            TenantDelegationAuthenticationHandler.SchemeName,
            authorize.AuthenticationSchemes);
        Assert.NotEqual(
            ApiKeyAuthenticationHandler.SchemeName,
            authorize.AuthenticationSchemes);
    }

    [Fact]
    public async Task StoredSteamRetrieve_TwoTenants_UsesOnlyDelegatedTenantCredential()
    {
        await StoreSteam(TenantA, "secret-a", "111");
        await StoreSteam(TenantB, "secret-b", "222");
        await using var db = CreateContext(TenantA);
        var steam = new CapturingSteamFetcher();
        var controller = CreateController(db, TenantA, steam);

        var result = await controller.RetrieveStored(Platform.Steam, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("secret-a", steam.ApiKey);
        Assert.Equal("111", steam.SteamId);
        var responseJson = JsonSerializer.Serialize(ok.Value);
        Assert.DoesNotContain("secret-a", responseJson, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-b", responseJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoredRetrieve_MissingCredential_ReturnsNotFoundWithoutCallingFetcher()
    {
        await using var db = CreateContext(TenantA);
        var steam = new CapturingSteamFetcher();
        var controller = CreateController(db, TenantA, steam);

        var result = await controller.RetrieveStored(Platform.Steam, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
        Assert.Null(steam.ApiKey);
    }

    [Fact]
    public async Task StoredRetrieve_Epic_ReturnsBadRequestWithoutReadingVault()
    {
        await using var db = CreateContext(TenantA);
        var controller = CreateController(db, TenantA, new CapturingSteamFetcher());

        var result = await controller.RetrieveStored(Platform.Epic, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    private async Task StoreSteam(Guid tenantId, string apiKey, string steamId)
    {
        await using var db = CreateContext(tenantId);
        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(new { apiKey, steamId }));
        await CreateCredentialService(db, tenantId).UpsertAsync(
            Platform.Steam,
            1,
            document.RootElement.Clone());
    }

    private RetrieveController CreateController(
        IngestContext db,
        Guid tenantId,
        ISteamFetcher steamFetcher) =>
        new(
            db,
            steamFetcher,
            new UnusedGogFetcher(),
            new UnusedPsnFetcher(),
            CreateCredentialService(db, tenantId),
            NullLogger<RetrieveController>.Instance,
            NullLoggerFactory.Instance);

    private PlatformCredentialService CreateCredentialService(IngestContext db, Guid tenantId) =>
        new(
            db,
            new FixedIngestTenantContext(tenantId),
            _protector,
            TimeProvider.System);

    private IngestContext CreateContext(Guid tenantId) =>
        new(_options, new FixedIngestTenantContext(tenantId));

    private static PlatformCredentialProtector CreateProtector()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Credentials:ActiveKeyVersion"] = "1",
                ["Credentials:Keys:1"] = Convert.ToBase64String(
                    Enumerable.Repeat((byte)0x5A, 32).ToArray())
            })
            .Build();
        return new PlatformCredentialProtector(
            PlatformCredentialKeyRing.FromConfiguration(configuration));
    }

    private sealed class FixedIngestTenantContext(Guid tenantId) : IIngestTenantContext
    {
        public Guid TenantId { get; } = tenantId;
    }

    private sealed class CapturingSteamFetcher : ISteamFetcher
    {
        public string? ApiKey { get; private set; }
        public string? SteamId { get; private set; }

        public Task<List<JsonElement>> FetchGamesAsync(string apiKey, string steamId)
        {
            ApiKey = apiKey;
            SteamId = steamId;
            return Task.FromResult(new List<JsonElement>());
        }
    }

    private sealed class UnusedGogFetcher : IGogFetcher
    {
        public Task<List<JsonElement>> FetchGamesAsync(string gogAlCookie, string username) =>
            throw new InvalidOperationException("GOG fetcher must not be called in this test.");
    }

    private sealed class UnusedPsnFetcher : IPsnFetcher
    {
        public Task<List<JsonElement>> FetchGamesAsync(Dictionary<string, string> cookies) =>
            throw new InvalidOperationException("PSN fetcher must not be called in this test.");
    }
}
