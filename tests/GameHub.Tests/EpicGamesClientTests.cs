using System.Text.Json;
using GameHub.Ingest.Services;
using Xunit;

namespace GameHub.Tests;

public sealed class EpicGamesClientTests
{
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
}
