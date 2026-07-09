using System.Net;
using System.Text;
using System.Text.Json;
using GameHub.Ingest.Data;
using GameHub.Ingest.Models;
using GameHub.Ingest.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameHub.Tests;

public sealed class EpicGamesClientTests
{
    [Fact]
    public async Task ExchangeAuthorizationCodeAsync_SendsLauncherUserAgentAndForm()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "access_token": "epic-access-token",
                  "refresh_token": "epic-refresh-token",
                  "account_id": "account-1",
                  "displayName": "Player"
                }
                """,
                Encoding.UTF8,
                "application/json")
        });
        var client = CreateClient(handler);

        var credential = await client.ExchangeAuthorizationCodeAsync("short-lived-code");

        Assert.Equal("epic-refresh-token", credential.RefreshToken);
        Assert.NotNull(handler.LastRequest);
        Assert.Contains(
            "UELauncher/",
            handler.LastRequest.Headers.UserAgent.ToString(),
            StringComparison.Ordinal);
        Assert.Equal("Basic", handler.LastRequest.Headers.Authorization?.Scheme);
        Assert.Contains("grant_type=authorization_code", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("code=short-lived-code", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("token_type=eg1", handler.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExchangeAuthorizationCodeAsync_BadRequest_RaisesCredentialValidation()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                "{\"errorCode\":\"errors.com.epicgames.oauth.invalid_grant\"}",
                Encoding.UTF8,
                "application/json")
        });
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<PlatformCredentialValidationException>(
            () => client.ExchangeAuthorizationCodeAsync("short-lived-code"));

        Assert.Contains("invalid or expired", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchGamesAsync_RotatedRefreshToken_PersistsBeforeLibraryFetch()
    {
        using var fixture = new IngestScopeFixture();
        var rotationStore = new CapturingRefreshTokenRotationStore();
        var handler = new RecordingHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return JsonResponse(
                    """
                    {
                      "access_token": "epic-access-token",
                      "refresh_token": "new-refresh-token",
                      "account_id": "account-2",
                      "displayName": "Renamed Player"
                    }
                    """);
            }

            return JsonResponse("[]");
        });
        var client = CreateClient(
            handler,
            fixture.ScopeFactory,
            rotationStore);

        var games = await client.FetchGamesAsync(
            new EpicPlatformCredential("old-refresh-token", "account-1", "Player"));

        Assert.Empty(games);
        Assert.Equal("old-refresh-token", rotationStore.CurrentCredential?.RefreshToken);
        Assert.Equal("new-refresh-token", rotationStore.RefreshedCredential?.RefreshToken);
        Assert.Equal("account-2", rotationStore.RefreshedCredential?.AccountId);
        Assert.Equal("Renamed Player", rotationStore.RefreshedCredential?.DisplayName);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task FetchGamesAsync_RotationPersistenceFailure_WritesSanitizedAuditAndStops()
    {
        using var fixture = new IngestScopeFixture();
        const string oldRefreshToken = "old-refresh-token-secret";
        const string newRefreshToken = "new-refresh-token-secret";
        var rotationStore = new FailingRefreshTokenRotationStore(new InvalidOperationException(newRefreshToken));
        var handler = new RecordingHandler(request => JsonResponse(
            $$"""
            {
              "access_token": "epic-access-token",
              "refresh_token": "{{newRefreshToken}}",
              "account_id": "account-2",
              "displayName": "Renamed Player"
            }
            """));
        var client = CreateClient(
            handler,
            fixture.ScopeFactory,
            rotationStore);

        var exception = await Assert.ThrowsAsync<PlatformCredentialRotationException>(
            () => client.FetchGamesAsync(new EpicPlatformCredential(oldRefreshToken)));
        var audit = await fixture.ReadSingleAuditLogAsync();

        Assert.Equal("Epic refresh token rotation could not be persisted.", exception.Message);
        Assert.Equal("Error", audit.Status);
        Assert.Equal("Epic refresh token rotation could not be persisted.", audit.ErrorMessage);
        Assert.DoesNotContain(oldRefreshToken, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(newRefreshToken, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(oldRefreshToken, audit.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(newRefreshToken, audit.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task FetchGamesAsync_UnavailableCatalogMetadata_SkipsOnlyThatRecordAndWritesPartialSuccess()
    {
        using var fixture = new IngestScopeFixture();
        var handler = new RecordingHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return JsonResponse("""
                {
                  "access_token": "epic-access-token",
                  "refresh_token": "epic-refresh-token"
                }
                """);
            }

            if (request.RequestUri?.AbsolutePath.Contains("/launcher/api/public/assets/", StringComparison.Ordinal) == true)
            {
                return JsonResponse("""
                [
                  { "appName": "UnavailableCatalog", "namespace": "catalog-one", "catalogItemId": "catalog-one" },
                  { "appName": "AvailableCatalog", "namespace": "catalog-two", "catalogItemId": "catalog-two" }
                ]
                """);
            }

            if (request.RequestUri?.Query.Contains("id=catalog-one", StringComparison.Ordinal) == true)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            if (request.RequestUri?.Query.Contains("id=catalog-two", StringComparison.Ordinal) == true)
            {
                return JsonResponse("""
                {
                  "catalog-two": {
                    "title": "Available Catalog Game",
                    "categories": [{ "path": "games" }]
                  }
                }
                """);
            }

            throw new InvalidOperationException("Unexpected Epic request in test.");
        });
        var client = CreateClient(handler, fixture.ScopeFactory);

        var games = await client.FetchGamesAsync(new EpicPlatformCredential("epic-refresh-token"));
        var audit = await fixture.ReadSingleAuditLogAsync();

        var game = Assert.Single(games);
        Assert.Equal("AvailableCatalog", game.GetProperty("app_name").GetString());
        Assert.Equal("PartialSuccess", audit.Status);
        Assert.Equal(1, audit.GamesFound);
        Assert.Equal(
            "Some Epic catalog metadata records were unavailable or malformed and were skipped.",
            audit.ErrorMessage);
        Assert.Equal(IngestScopeFixture.TenantId, audit.TenantId);
        Assert.DoesNotContain("epic-refresh-token", audit.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(4, handler.RequestCount);
    }

    [Fact]
    public async Task FetchGamesAsync_RateLimitedLibraryRequest_WritesSanitizedRateLimitAudit()
    {
        using var fixture = new IngestScopeFixture();
        const string refreshToken = "epic-refresh-token-secret";
        var handler = new RecordingHandler(request => request.Method == HttpMethod.Post
            ? JsonResponse("""
            {
              "access_token": "epic-access-token",
              "refresh_token": "epic-refresh-token-secret"
            }
            """)
            : RateLimitedResponse());
        var client = CreateClient(handler, fixture.ScopeFactory);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.FetchGamesAsync(new EpicPlatformCredential(refreshToken)));
        var audit = await fixture.ReadSingleAuditLogAsync();

        Assert.Equal("RateLimited", audit.Status);
        Assert.Equal("Provider rate limit exceeded. Retry after 2030-01-02T03:04:05.0000000+00:00.", audit.ErrorMessage);
        Assert.Equal(IngestScopeFixture.TenantId, audit.TenantId);
        Assert.DoesNotContain(refreshToken, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(refreshToken, audit.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchGamesAsync_MalformedLauncherResponse_WritesSanitizedAudit()
    {
        using var fixture = new IngestScopeFixture();
        const string refreshToken = "epic-refresh-token-secret";
        var handler = new RecordingHandler(request => request.Method == HttpMethod.Post
            ? JsonResponse("""
            {
              "access_token": "epic-access-token",
              "refresh_token": "epic-refresh-token-secret"
            }
            """)
            : JsonResponse("not-valid-json"));
        var client = CreateClient(handler, fixture.ScopeFactory);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.FetchGamesAsync(new EpicPlatformCredential(refreshToken)));
        var audit = await fixture.ReadSingleAuditLogAsync();

        Assert.Equal("MalformedResponse", audit.Status);
        Assert.Equal("Epic provider returned an unexpected response.", audit.ErrorMessage);
        Assert.Equal(IngestScopeFixture.TenantId, audit.TenantId);
        Assert.DoesNotContain(refreshToken, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(refreshToken, audit.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void MapToIngestItem_ProviderContractFixtures_PreserveCompatibleRecordsAndSkipUnsupportedRecords()
    {
        using var fixture = LoadEpicContractFixture();
        foreach (var contractCase in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            var name = contractCase.GetProperty("name").GetString();
            var mapped = EpicLibraryItemMapper.MapToIngestItem(
                contractCase.GetProperty("asset"),
                contractCase.GetProperty("catalogMetadata"));
            var expected = contractCase.GetProperty("expected");

            if (!expected.GetProperty("isMapped").GetBoolean())
            {
                Assert.Null(mapped);
                continue;
            }

            Assert.True(mapped.HasValue, $"Expected contract fixture '{name}' to map.");
            Assert.Equal(expected.GetProperty("appName").GetString(), mapped.Value.GetProperty("app_name").GetString());
            Assert.Equal(expected.GetProperty("title").GetString(), mapped.Value.GetProperty("app_title").GetString());

            foreach (var property in expected.GetProperty("presentProperties").EnumerateArray())
            {
                var propertyName = property.GetString();
                Assert.False(string.IsNullOrWhiteSpace(propertyName), $"Fixture '{name}' has an empty property name.");
                Assert.True(
                    mapped.Value.TryGetProperty(propertyName!, out _),
                    $"Expected contract fixture '{name}' to include '{propertyName}'.");
            }

            foreach (var property in expected.GetProperty("absentProperties").EnumerateArray())
            {
                var propertyName = property.GetString();
                Assert.False(string.IsNullOrWhiteSpace(propertyName), $"Fixture '{name}' has an empty property name.");
                Assert.False(
                    mapped.Value.TryGetProperty(propertyName!, out _),
                    $"Expected contract fixture '{name}' to omit '{propertyName}'.");
            }
        }
    }

    [Fact]
    public void MapToIngestItem_LauncherAssetWithCatalogMetadata_UsesAppNameAndTitle()
    {
        using var asset = JsonDocument.Parse("""
        {
          "appName": "Anemone",
          "assetId": "asset-1",
          "namespace": "anemone",
          "catalogItemId": "catalog-1"
        }
        """);
        using var metadata = JsonDocument.Parse("""
        {
          "id": "catalog-1",
          "namespace": "anemone",
          "title": "World of Goo",
          "developerDisplayName": "2D Boy",
          "publisherDisplayName": "Tomorrow Corporation",
          "releaseInfo": [{ "date": "2008-10-13T00:00:00.000Z" }],
          "description": "A physics puzzle game about building with balls of goo.",
          "shortDescription": "Build strange towers with goo balls.",
          "categories": [{ "path": "games" }, { "path": "games/puzzle" }],
          "keyImages": [
            { "type": "DieselGameBox", "url": "https://cdn.example.test/world-of-goo-cover.jpg" },
            { "type": "DieselGameBoxWide", "url": "https://cdn.example.test/world-of-goo-key-art.jpg" }
          ]
        }
        """);

        var mapped = EpicLibraryItemMapper.MapToIngestItem(asset.RootElement, metadata.RootElement);

        Assert.NotNull(mapped);
        Assert.Equal("Anemone", mapped.Value.GetProperty("app_name").GetString());
        Assert.Equal("World of Goo", mapped.Value.GetProperty("app_title").GetString());
        Assert.Equal("anemone", mapped.Value.GetProperty("namespace").GetString());
        Assert.Equal("catalog-1", mapped.Value.GetProperty("catalog_item_id").GetString());
        Assert.Equal("asset-1", mapped.Value.GetProperty("asset_id").GetString());
        Assert.Equal("2D Boy", mapped.Value.GetProperty("developer").GetString());
        Assert.Equal("Tomorrow Corporation", mapped.Value.GetProperty("publisher").GetString());
        Assert.Equal("2008-10-13T00:00:00.000Z", mapped.Value.GetProperty("release_date").GetString());
        Assert.Equal(
            "A physics puzzle game about building with balls of goo.",
            mapped.Value.GetProperty("description").GetString());
        Assert.Equal("Build strange towers with goo balls.", mapped.Value.GetProperty("summary").GetString());
        var categories = mapped.Value.GetProperty("categories").EnumerateArray()
            .Select(category => category.GetString()!)
            .ToArray();
        Assert.Equal(new[] { "games", "games/puzzle" }, categories);
        Assert.Equal(
            "https://cdn.example.test/world-of-goo-cover.jpg",
            mapped.Value.GetProperty("cover_url").GetString());
        Assert.Equal(
            "https://cdn.example.test/world-of-goo-key-art.jpg",
            mapped.Value.GetProperty("key_art_url").GetString());
        Assert.True(mapped.Value.TryGetProperty("legendary_asset", out var originalAsset));
        Assert.True(mapped.Value.TryGetProperty("legendary_metadata", out var originalMetadata));
        Assert.Equal("catalog-1", originalAsset.GetProperty("catalogItemId").GetString());
        Assert.Equal("catalog-1", originalMetadata.GetProperty("id").GetString());
    }

    [Fact]
    public void MapToIngestItem_MissingOptionalMetadata_OmitsEmptyRawFields()
    {
        using var asset = JsonDocument.Parse("""
        {
          "appName": "MinimalGame",
          "assetId": "",
          "namespace": "minimal-game",
          "catalogItemId": "minimal-catalog"
        }
        """);
        using var metadata = JsonDocument.Parse("""
        {
          "id": "minimal-catalog",
          "namespace": "minimal-game",
          "title": "Minimal Game",
          "developerDisplayName": "",
          "publisherDisplayName": " ",
          "description": "",
          "shortDescription": " ",
          "categories": [{ "path": "games" }],
          "keyImages": [{ "type": "DieselGameBox", "url": "" }]
        }
        """);

        var mapped = EpicLibraryItemMapper.MapToIngestItem(asset.RootElement, metadata.RootElement);

        Assert.NotNull(mapped);
        Assert.False(mapped.Value.TryGetProperty("asset_id", out _));
        Assert.False(mapped.Value.TryGetProperty("developer", out _));
        Assert.False(mapped.Value.TryGetProperty("publisher", out _));
        Assert.False(mapped.Value.TryGetProperty("release_date", out _));
        Assert.False(mapped.Value.TryGetProperty("description", out _));
        Assert.False(mapped.Value.TryGetProperty("summary", out _));
        Assert.False(mapped.Value.TryGetProperty("cover_url", out _));
        Assert.False(mapped.Value.TryGetProperty("key_art_url", out _));
    }

    [Fact]
    public void MapToIngestItem_ProviderCredentialLikeMetadata_DoesNotPromoteSecrets()
    {
        using var asset = JsonDocument.Parse("""
        {
          "appName": "SecretSafeGame",
          "assetId": "asset-secret-safe",
          "namespace": "secret-safe",
          "catalogItemId": "secret-safe-catalog",
          "access_token": "asset-access-token"
        }
        """);
        using var metadata = JsonDocument.Parse("""
        {
          "id": "secret-safe-catalog",
          "namespace": "secret-safe",
          "title": "Secret Safe Game",
          "categories": [{ "path": "games" }],
          "access_token": "metadata-access-token",
          "refresh_token": "metadata-refresh-token",
          "authorizationCode": "metadata-authorization-code",
          "client_secret": "metadata-client-secret",
          "cookie": "metadata-cookie"
        }
        """);

        var mapped = EpicLibraryItemMapper.MapToIngestItem(asset.RootElement, metadata.RootElement);

        Assert.NotNull(mapped);
        Assert.False(mapped.Value.TryGetProperty("access_token", out _));
        Assert.False(mapped.Value.TryGetProperty("refresh_token", out _));
        Assert.False(mapped.Value.TryGetProperty("authorizationCode", out _));
        Assert.False(mapped.Value.TryGetProperty("client_secret", out _));
        Assert.False(mapped.Value.TryGetProperty("cookie", out _));
    }

    [Fact]
    public void MapToIngestItem_DlcRecord_ReturnsNull()
    {
        using var asset = JsonDocument.Parse("""
        {
          "appName": "BaseGameDlc",
          "assetId": "asset-dlc",
          "namespace": "game",
          "catalogItemId": "catalog-dlc"
        }
        """);
        using var metadata = JsonDocument.Parse("""
        {
          "id": "catalog-dlc",
          "namespace": "game",
          "title": "Base Game DLC",
          "mainGameItem": { "id": "catalog-base" },
          "categories": [{ "path": "addons" }]
        }
        """);

        Assert.Null(EpicLibraryItemMapper.MapToIngestItem(asset.RootElement, metadata.RootElement));
    }

    [Theory]
    [InlineData("addons")]
    [InlineData("addons/launchable")]
    [InlineData("mods")]
    public void MapToIngestItem_AddonOrModRecord_ReturnsNull(string category)
    {
        using var asset = JsonDocument.Parse("""
        {
          "appName": "TechnicalRecord",
          "assetId": "asset-technical",
          "namespace": "game",
          "catalogItemId": "catalog-technical"
        }
        """);
        using var metadata = JsonDocument.Parse($$"""
        {
          "id": "catalog-technical",
          "namespace": "game",
          "title": "Technical Record",
          "categories": [{ "path": "{{category}}" }]
        }
        """);

        Assert.Null(EpicLibraryItemMapper.MapToIngestItem(asset.RootElement, metadata.RootElement));
    }

    [Fact]
    public void MapToIngestItem_UnrealMarketplaceRecord_ReturnsNull()
    {
        using var asset = JsonDocument.Parse("""
        {
          "appName": "MarketplacePack",
          "assetId": "asset-ue",
          "namespace": "ue",
          "catalogItemId": "catalog-ue"
        }
        """);
        using var metadata = JsonDocument.Parse("""
        {
          "id": "catalog-ue",
          "namespace": "ue",
          "title": "Marketplace Pack",
          "categories": [{ "path": "games" }]
        }
        """);

        Assert.Null(EpicLibraryItemMapper.MapToIngestItem(asset.RootElement, metadata.RootElement));
    }

    [Fact]
    public void MapToIngestItem_RecordWithoutGameCategory_ReturnsNull()
    {
        using var asset = JsonDocument.Parse("""
        {
          "appName": "UnknownRecord",
          "assetId": "asset-unknown",
          "namespace": "game",
          "catalogItemId": "catalog-unknown"
        }
        """);
        using var metadata = JsonDocument.Parse("""
        {
          "id": "catalog-unknown",
          "namespace": "game",
          "title": "Unknown Record",
          "categories": []
        }
        """);

        Assert.Null(EpicLibraryItemMapper.MapToIngestItem(asset.RootElement, metadata.RootElement));
    }

    [Fact]
    public void MapToIngestItem_VariantAssetForSameGame_UsesStableAppNameExternalId()
    {
        using var asset = JsonDocument.Parse("""
        {
          "appName": "StableGameApp",
          "assetId": "variant-asset",
          "namespace": "stable-game",
          "catalogItemId": "stable-catalog"
        }
        """);
        using var metadata = JsonDocument.Parse("""
        {
          "id": "stable-catalog",
          "namespace": "stable-game",
          "title": "Stable Game",
          "categories": [{ "path": "games/edition/base" }]
        }
        """);

        var mapped = EpicLibraryItemMapper.MapToIngestItem(asset.RootElement, metadata.RootElement);

        Assert.NotNull(mapped);
        Assert.Equal("StableGameApp", mapped.Value.GetProperty("app_name").GetString());
        Assert.Equal("stable-catalog", mapped.Value.GetProperty("catalog_item_id").GetString());
        Assert.Equal("variant-asset", mapped.Value.GetProperty("asset_id").GetString());
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new()
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage RateLimitedResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
            new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero));
        return response;
    }

    private static JsonDocument LoadEpicContractFixture()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Epic",
            "epic-provider-contract-cases.json");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static EpicGamesClient CreateClient(
        RecordingHandler handler,
        IServiceScopeFactory? scopeFactory = null,
        IEpicRefreshTokenRotationStore? refreshTokenRotationStore = null)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return new EpicGamesClient(
            new HttpClient(handler),
            NullLogger<EpicGamesClient>.Instance,
            scopeFactory ?? services.GetRequiredService<IServiceScopeFactory>(),
            refreshTokenRotationStore ?? new NoopRefreshTokenRotationStore());
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string LastBody { get; private set; } = "";
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequest = request;
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return responseFactory(request);
        }
    }

    private sealed class IngestScopeFixture : IDisposable
    {
        public static readonly Guid TenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly ServiceProvider _provider;

        public IngestScopeFixture()
        {
            _connection.Open();
            _provider = new ServiceCollection()
                .AddSingleton<IIngestTenantContext>(new FixedIngestTenantContext(TenantId))
                .AddDbContext<IngestContext>(options => options.UseSqlite(_connection))
                .BuildServiceProvider();

            using var scope = _provider.CreateScope();
            scope.ServiceProvider.GetRequiredService<IngestContext>().Database.EnsureCreated();
        }

        public IServiceScopeFactory ScopeFactory => _provider.GetRequiredService<IServiceScopeFactory>();

        public async Task<IngestAuditLog> ReadSingleAuditLogAsync()
        {
            using var scope = _provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IngestContext>();
            return await db.IngestAuditLogs.SingleAsync();
        }

        public void Dispose()
        {
            _provider.Dispose();
            _connection.Dispose();
        }
    }

    private sealed class FixedIngestTenantContext(Guid tenantId) : IIngestTenantContext
    {
        public Guid TenantId { get; } = tenantId;
    }

    private sealed class NoopRefreshTokenRotationStore : IEpicRefreshTokenRotationStore
    {
        public Task PersistIfRotatedAsync(
            EpicPlatformCredential currentCredential,
            EpicPlatformCredential refreshedCredential,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class CapturingRefreshTokenRotationStore : IEpicRefreshTokenRotationStore
    {
        public EpicPlatformCredential? CurrentCredential { get; private set; }
        public EpicPlatformCredential? RefreshedCredential { get; private set; }

        public Task PersistIfRotatedAsync(
            EpicPlatformCredential currentCredential,
            EpicPlatformCredential refreshedCredential,
            CancellationToken cancellationToken = default)
        {
            CurrentCredential = currentCredential;
            RefreshedCredential = refreshedCredential;
            return Task.CompletedTask;
        }
    }

    private sealed class FailingRefreshTokenRotationStore(Exception innerException)
        : IEpicRefreshTokenRotationStore
    {
        public Task PersistIfRotatedAsync(
            EpicPlatformCredential currentCredential,
            EpicPlatformCredential refreshedCredential,
            CancellationToken cancellationToken = default) =>
            throw new PlatformCredentialRotationException(innerException);
    }
}
