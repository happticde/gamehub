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
          "categories": [{ "path": "games" }]
        }
        """);

        var mapped = EpicLibraryItemMapper.MapToIngestItem(asset.RootElement, metadata.RootElement);

        Assert.NotNull(mapped);
        Assert.Equal("Anemone", mapped.Value.GetProperty("app_name").GetString());
        Assert.Equal("World of Goo", mapped.Value.GetProperty("app_title").GetString());
        Assert.Equal("anemone", mapped.Value.GetProperty("namespace").GetString());
        Assert.Equal("catalog-1", mapped.Value.GetProperty("catalog_item_id").GetString());
        Assert.Equal("asset-1", mapped.Value.GetProperty("asset_id").GetString());
        Assert.True(mapped.Value.TryGetProperty("legendary_asset", out var originalAsset));
        Assert.True(mapped.Value.TryGetProperty("legendary_metadata", out var originalMetadata));
        Assert.Equal("catalog-1", originalAsset.GetProperty("catalogItemId").GetString());
        Assert.Equal("catalog-1", originalMetadata.GetProperty("id").GetString());
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
        private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

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
