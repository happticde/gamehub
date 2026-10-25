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
    [InlineData("steam", "network", 502)]
    [InlineData("steam", "json", 502)]
    [InlineData("steam", "unexpected", 500)]
    [InlineData("gog", "network", 502)]
    [InlineData("gog", "json", 502)]
    [InlineData("gog", "unexpected", 500)]
    public async Task RetrieveFailure_UserControlledIdentifierAndException_AreNotLogged(
        string platform,
        string failure,
        int expectedStatus)
    {
        var (db, connection) = DatabaseFixture.CreateIngestContext();
        await using var database = db;
        await using var openConnection = connection;
        var logger = new RecordingLogger<RetrieveController>();
        Exception exception = failure switch
        {
            "network" => new HttpRequestException(InjectedLogLine),
            "json" => new JsonException(InjectedLogLine),
            _ => new InvalidOperationException(InjectedLogLine)
        };
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
        Assert.NotEmpty(logger.Records);
        Assert.All(logger.Records, entry =>
        {
            Assert.DoesNotContain(InjectedLogLine, entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("forged log entry", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("steam-api-secret", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("gog-cookie-secret", entry.Message, StringComparison.Ordinal);
            Assert.Null(entry.Exception);
            Assert.DoesNotContain(entry.Properties, property =>
                property.Value?.ToString()?.Contains("forged log entry", StringComparison.Ordinal) == true);
        });
    }
}
