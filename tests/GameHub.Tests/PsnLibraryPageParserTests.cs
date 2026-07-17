using System.Text.Json;
using GameHub.Ingest.Services;
using Xunit;

namespace GameHub.Tests;

public sealed class PsnLibraryPageParserTests
{
    [Fact]
    public void Parse_ProviderContractFixtures_PreservesCompatiblePagesAndRejectsUnsafeVariants()
    {
        using var fixture = LoadContractFixture();

        foreach (var contractCase in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            var name = contractCase.GetProperty("name").GetString();
            var expected = contractCase.GetProperty("expected");

            if (!expected.GetProperty("isValid").GetBoolean())
            {
                var exception = Assert.Throws<PsnProviderContractException>(
                    () => PsnLibraryPageParser.Parse(contractCase.GetProperty("response")));

                Assert.Equal("PSN provider returned an unexpected response.", exception.Message);
                Assert.DoesNotContain("provider-secret", exception.Message, StringComparison.Ordinal);
                continue;
            }

            var page = PsnLibraryPageParser.Parse(contractCase.GetProperty("response"));
            var expectedIds = expected.GetProperty("gameIds")
                .EnumerateArray()
                .Select(id => id.GetString())
                .ToArray();
            var actualIds = page.Games
                .Select(ReadTitleId)
                .ToArray();

            Assert.Equal(expected.GetProperty("isLast").GetBoolean(), page.IsLast);
            Assert.Equal(expectedIds, actualIds);
            Assert.True(name is not null, "Every PSN contract case must have a name.");
        }
    }

    [Fact]
    public void Parse_DisposedSourceDocument_ReturnsDetachedGameRecords()
    {
        JsonElement game;
        using (var document = JsonDocument.Parse(
                   """
                   {
                     "data": {
                       "purchasedTitlesRetrieve": {
                         "games": [{ "titleId": "CUSA00001", "name": "Detached Game" }],
                         "pageInfo": { "isLast": true }
                       }
                     }
                   }
                   """))
        {
            game = Assert.Single(PsnLibraryPageParser.Parse(document.RootElement).Games);
        }

        Assert.Equal("Detached Game", game.GetProperty("name").GetString());
    }

    private static string? ReadTitleId(JsonElement game)
    {
        if (game.TryGetProperty("titleId", out var titleId))
        {
            return titleId.GetString();
        }

        return game.TryGetProperty("npTitleId", out var npTitleId)
            ? npTitleId.GetString()
            : null;
    }

    private static JsonDocument LoadContractFixture()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Psn",
            "psn-provider-contract-cases.json");
        return JsonDocument.Parse(File.ReadAllText(path));
    }
}
