using System.Text.Json;
using GameHub.Ingest.Services;
using Xunit;

namespace GameHub.Tests;

public sealed class GogLibraryPageParserTests
{
    [Fact]
    public void Parse_ProviderContractFixtures_PreservesCompatiblePagesAndRejectsUnsafeVariants()
    {
        using var fixture = LoadContractFixture();

        foreach (var contractCase in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            var name = contractCase.GetProperty("name").GetString();
            Assert.False(string.IsNullOrWhiteSpace(name), "Every GOG contract case must have a name.");

            var expected = contractCase.GetProperty("expected");
            var currentPage = contractCase.GetProperty("currentPage").GetInt32();

            if (!expected.GetProperty("isValid").GetBoolean())
            {
                var exception = Assert.Throws<GogProviderContractException>(
                    () => GogLibraryPageParser.Parse(
                        contractCase.GetProperty("response"),
                        currentPage));

                Assert.Equal("GOG provider returned an unexpected response.", exception.Message);
                Assert.DoesNotContain("provider-secret", exception.Message, StringComparison.Ordinal);
                continue;
            }

            var page = GogLibraryPageParser.Parse(contractCase.GetProperty("response"), currentPage);
            var expectedIds = expected.GetProperty("gameIds")
                .EnumerateArray()
                .Select(id => id.GetString())
                .ToArray();
            var actualIds = page.Games
                .Select(ReadGameId)
                .ToArray();

            Assert.Equal(expected.GetProperty("totalPages").GetInt32(), page.TotalPages);
            Assert.Equal(expectedIds, actualIds);
        }
    }

    [Fact]
    public void Parse_DisposedSourceDocument_ReturnsDetachedGameRecords()
    {
        JsonElement game;
        using (var document = JsonDocument.Parse(
                   """
                   {
                     "pages": 1,
                     "_embedded": {
                       "items": [
                         { "game": { "id": 1495134320, "title": "Detached Game" } }
                       ]
                     }
                   }
                   """))
        {
            game = Assert.Single(GogLibraryPageParser.Parse(document.RootElement, 1).Games);
        }

        Assert.Equal("Detached Game", game.GetProperty("title").GetString());
    }

    private static string? ReadGameId(JsonElement game)
    {
        var id = game.GetProperty("id");
        return id.ValueKind == JsonValueKind.Number
            ? id.GetInt64().ToString()
            : id.GetString();
    }

    private static JsonDocument LoadContractFixture()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Gog",
            "gog-provider-contract-cases.json");
        return JsonDocument.Parse(File.ReadAllText(path));
    }
}
