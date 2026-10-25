using System.Text.Json;
using GameHub.Ingest.Controllers;
using GameHub.Ingest.Services;
using GameHub.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace GameHub.Tests;

public sealed class RetrieveControllerLoggingTests
{
    private const string InjectedLogLine = "retrieval-user-7c9824\r\nCRITICAL forged log entry";

    [Theory]
    [InlineData("steam", "network", 502, "Failed to fetch from Steam API.")]
    [InlineData("steam", "json", 502, "Steam API returned unexpected data.")]
    [InlineData("steam", "unexpected", 500, "Internal retrieval error.")]
    [InlineData("gog", "network", 502, "Failed to fetch from GOG API.")]
    [InlineData("gog", "json", 502, "GOG API returned unexpected data.")]
    [InlineData("gog", "unexpected", 500, "Internal retrieval error.")]
    public async Task RetrieveFailure_UserControlledIdentifierAndException_AreNotLogged(
        string platform,
        string failure,
        int expectedStatus,
        string expectedMessage)
    {
        var (db, connection) = DatabaseFixture.CreateIngestContext();
        await using var database = db;
        await using var openConnection = connection;
        var logger = new RecordingLogger<RetrieveController>();
        var exceptionMessage = $"steam-api-secret gog-cookie-secret {InjectedLogLine}";
        var innerException = new Exception("exception-inner-secret");
        Exception exception = failure switch
        {
            "network" => new HttpRequestException(exceptionMessage, innerException),
            "json" => new JsonException(exceptionMessage, innerException),
            _ => new InvalidOperationException(exceptionMessage, innerException)
        };
        exception.Data["credential"] = "exception-data-secret";
        var steam = new Mock<ISteamFetcher>(MockBehavior.Strict);
        var gog = new Mock<IGogFetcher>(MockBehavior.Strict);

        if (platform == "steam")
        {
            steam.Setup(fetcher => fetcher.FetchGamesAsync("steam-api-secret", InjectedLogLine))
                .ThrowsAsync(exception);
        }
        else
        {
            gog.Setup(fetcher => fetcher.FetchGamesAsync("gog-cookie-secret", InjectedLogLine))
                .ThrowsAsync(exception);
        }

        var controller = new RetrieveController(
            db,
            steam.Object,
            gog.Object,
            Mock.Of<IPsnFetcher>(),
            Mock.Of<IEpicFetcher>(),
            credentials: null!,
            logger,
            NullLoggerFactory.Instance);

        var result = platform == "steam"
            ? await controller.RetrieveSteam(new SteamRetrieveRequest
            {
                ApiKey = "steam-api-secret",
                SteamId = InjectedLogLine
            })
            : await controller.RetrieveGog(new GogRetrieveRequest
            {
                Cookie = "gog-cookie-secret",
                User = InjectedLogLine
            });

        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(expectedStatus, response.StatusCode);
        var body = JsonSerializer.SerializeToElement(response.Value);
        Assert.Equal(expectedMessage, body.GetProperty("Message").GetString());
        Assert.Single(body.EnumerateObject());
        Assert.NotEmpty(logger.Records);
        Assert.All(logger.Records, entry =>
        {
            Assert.DoesNotContain(InjectedLogLine, entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("forged log entry", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("steam-api-secret", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("gog-cookie-secret", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("exception-inner-secret", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("exception-data-secret", entry.Message, StringComparison.Ordinal);
            Assert.Null(entry.Exception);
            Assert.DoesNotContain(entry.Properties, property =>
                property.Value?.ToString()?.Contains("forged log entry", StringComparison.Ordinal) == true);
        });
    }
}
