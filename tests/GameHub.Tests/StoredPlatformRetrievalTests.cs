using System.Reflection;
using System.Text.Json;
using GameHub.Ingest.Controllers;
using GameHub.Ingest.Data;
using GameHub.Ingest.Models;
using GameHub.Ingest.Services;
using GameHub.Shared;
using GameHub.Shared.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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
    public async Task StoredEpicRetrieve_UsesDelegatedTenantRefreshTokenWithoutReturningSecrets()
    {
        await StoreEpic(TenantA, "epic-refresh-a");
        await StoreEpic(TenantB, "epic-refresh-b");
        await using var db = CreateContext(TenantA);
        var epic = new CapturingEpicFetcher();
        var controller = CreateController(
            db,
            TenantA,
            new CapturingSteamFetcher(),
            new UnusedPsnFetcher(),
            epic);

        var result = await controller.RetrieveStored(Platform.Epic, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("epic-refresh-a", epic.Credential?.RefreshToken);
        var responseJson = JsonSerializer.Serialize(ok.Value);
        Assert.DoesNotContain("epic-refresh-a", responseJson, StringComparison.Ordinal);
        Assert.DoesNotContain("epic-refresh-b", responseJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoredEpicRetrieve_RotationPersistenceFailure_ReturnsSanitizedFailureWithoutLoggingSecrets()
    {
        const string oldRefreshToken = "old-refresh-token-secret";
        const string newRefreshToken = "new-refresh-token-secret";
        await StoreEpic(TenantA, oldRefreshToken);
        await using var db = CreateContext(TenantA);
        var logger = new CapturingLogger<RetrieveController>();
        var controller = new RetrieveController(
            db,
            new CapturingSteamFetcher(),
            new UnusedGogFetcher(),
            new UnusedPsnFetcher(),
            new ThrowingEpicFetcher(new PlatformCredentialRotationException(
                new InvalidOperationException(newRefreshToken))),
            CreateCredentialService(db, TenantA),
            logger,
            NullLoggerFactory.Instance);

        var result = await controller.RetrieveStored(Platform.Epic, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        var responseJson = JsonSerializer.Serialize(objectResult.Value);
        Assert.Equal(502, objectResult.StatusCode);
        Assert.Contains("Platform retrieval failed.", responseJson, StringComparison.Ordinal);
        Assert.DoesNotContain(oldRefreshToken, responseJson, StringComparison.Ordinal);
        Assert.DoesNotContain(newRefreshToken, responseJson, StringComparison.Ordinal);
        Assert.DoesNotContain(oldRefreshToken, string.Join("\n", logger.Messages), StringComparison.Ordinal);
        Assert.DoesNotContain(newRefreshToken, string.Join("\n", logger.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoredPsnRetrieve_UsesDelegatedTenantCookieMapWithoutReturningSecrets()
    {
        await StorePsn(TenantA, new Dictionary<string, string>
        {
            ["pdccws_p"] = "secret-pdccws-a",
            ["npsso"] = "secret-npsso-a",
            ["sb-cdn.session"] = "secret-session-a"
        });
        await StorePsn(TenantB, new Dictionary<string, string>
        {
            ["npsso"] = "secret-npsso-b"
        });
        await using var db = CreateContext(TenantA);
        var psn = new CapturingPsnFetcher();
        var controller = CreateController(db, TenantA, new CapturingSteamFetcher(), psn);

        var result = await controller.RetrieveStored(Platform.Psn, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("secret-pdccws-a", psn.Cookies?["pdccws_p"]);
        Assert.Equal("secret-npsso-a", psn.Cookies?["npsso"]);
        Assert.Equal("secret-session-a", psn.Cookies?["sb-cdn.session"]);
        var responseJson = JsonSerializer.Serialize(ok.Value);
        Assert.DoesNotContain("secret-pdccws-a", responseJson, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-npsso-a", responseJson, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-npsso-b", responseJson, StringComparison.Ordinal);
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

    private async Task StorePsn(Guid tenantId, Dictionary<string, string> cookies)
    {
        await using var db = CreateContext(tenantId);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { cookies }));
        await CreateCredentialService(db, tenantId).UpsertAsync(
            Platform.Psn,
            1,
            document.RootElement.Clone());
    }

    private async Task StoreEpic(Guid tenantId, string refreshToken)
    {
        await using var db = CreateContext(tenantId);
        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(new { refreshToken }));
        await CreateCredentialService(db, tenantId).UpsertAsync(
            Platform.Epic,
            1,
            document.RootElement.Clone());
    }

    private RetrieveController CreateController(
        IngestContext db,
        Guid tenantId,
        ISteamFetcher steamFetcher) =>
        CreateController(db, tenantId, steamFetcher, new UnusedPsnFetcher());

    private RetrieveController CreateController(
        IngestContext db,
        Guid tenantId,
        ISteamFetcher steamFetcher,
        IPsnFetcher psnFetcher) =>
        CreateController(db, tenantId, steamFetcher, psnFetcher, new UnusedEpicFetcher());

    private RetrieveController CreateController(
        IngestContext db,
        Guid tenantId,
        ISteamFetcher steamFetcher,
        IPsnFetcher psnFetcher,
        IEpicFetcher epicFetcher) =>
        new(
            db,
            steamFetcher,
            new UnusedGogFetcher(),
            psnFetcher,
            epicFetcher,
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

    private sealed class UnusedEpicFetcher : IEpicFetcher
    {
        public Task<List<JsonElement>> FetchGamesAsync(
            EpicPlatformCredential credential,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Epic fetcher must not be called in this test.");
    }

    private sealed class CapturingPsnFetcher : IPsnFetcher
    {
        public Dictionary<string, string>? Cookies { get; private set; }

        public Task<List<JsonElement>> FetchGamesAsync(Dictionary<string, string> cookies)
        {
            Cookies = new Dictionary<string, string>(cookies);
            return Task.FromResult(new List<JsonElement>());
        }
    }

    private sealed class CapturingEpicFetcher : IEpicFetcher
    {
        public EpicPlatformCredential? Credential { get; private set; }

        public Task<List<JsonElement>> FetchGamesAsync(
            EpicPlatformCredential credential,
            CancellationToken cancellationToken = default)
        {
            Credential = credential;
            return Task.FromResult(new List<JsonElement>());
        }
    }

    private sealed class ThrowingEpicFetcher(Exception exception) : IEpicFetcher
    {
        public Task<List<JsonElement>> FetchGamesAsync(
            EpicPlatformCredential credential,
            CancellationToken cancellationToken = default) =>
            throw exception;
    }

    private sealed class CapturingLogger<T> : ILogger<T>, IDisposable
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull =>
            this;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            if (exception is not null)
            {
                Messages.Add(exception.Message);
            }
        }

        public void Dispose()
        {
        }
    }
}
