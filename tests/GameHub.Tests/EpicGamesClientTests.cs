using System.Net;
using System.Text;
using System.Text.Json;
using GameHub.Ingest.Services;
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

    private static EpicGamesClient CreateClient(RecordingHandler handler)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return new EpicGamesClient(
            new HttpClient(handler),
            NullLogger<EpicGamesClient>.Instance,
            services.GetRequiredService<IServiceScopeFactory>());
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string LastBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return responseFactory(request);
        }
    }
}
