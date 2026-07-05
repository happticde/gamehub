using System.Text.Json;
using GameHub.Ingest.Services;
using Xunit;

namespace GameHub.Tests;

public sealed class EpicGamesClientTests
{
    [Fact]
    public void MapToIngestItem_LibraryRecordWithMetadata_UsesAppNameAndTitle()
    {
        using var document = JsonDocument.Parse("""
        {
          "appName": "Anemone",
          "namespace": "anemone",
          "catalogItemId": "catalog-1",
          "metadata": {
            "title": "World of Goo"
          }
        }
        """);

        var mapped = EpicLibraryItemMapper.MapToIngestItem(document.RootElement);

        Assert.NotNull(mapped);
        Assert.Equal("Anemone", mapped.Value.GetProperty("app_name").GetString());
        Assert.Equal("World of Goo", mapped.Value.GetProperty("app_title").GetString());
        Assert.True(mapped.Value.TryGetProperty("legendary_library_item", out var original));
        Assert.Equal("catalog-1", original.GetProperty("catalogItemId").GetString());
    }

    [Fact]
    public void MapToIngestItem_RecordWithoutAppName_ReturnsNull()
    {
        using var document = JsonDocument.Parse("""
        {
          "metadata": {
            "title": "Missing App Name"
          }
        }
        """);

        Assert.Null(EpicLibraryItemMapper.MapToIngestItem(document.RootElement));
    }
}
