using System.Net;
using System.Text.Json;
using GameHub.Core.Services;
using GameHub.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Xunit;

namespace GameHub.Tests;

/// <summary>
/// Tests for the IgdbClient. Verifies that it correctly handles Twitch OAuth,
/// constructs IGDB queries, and parses the responses into GameMetadata.
/// Uses a mocked HttpMessageHandler to intercept and mock API responses.
/// </summary>
public class IgdbClientTests
{
    private readonly IConfiguration _config;

    public IgdbClientTests()
    {
        var inMemorySettings = new Dictionary<string, string?> {
            {"Igdb:ClientId", "test-client-id"},
            {"Igdb:ClientSecret", "test-client-secret"}
        };

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();
    }

    private HttpClient CreateMockHttpClient(Func<HttpRequestMessage, HttpResponseMessage> handlerFunc)
    {
        var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) => handlerFunc(request));

        return new HttpClient(handlerMock.Object);
    }

    [Fact]
    public async Task SearchAsync_ValidTitle_ReturnsMappedMetadata()
    {
        // Arrange
        var httpClient = CreateMockHttpClient(request =>
        {
            if (request.RequestUri?.ToString().Contains("oauth2/token") == true)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { access_token = "mock-token", expires_in = 3600 }))
                };
            }

            if (request.RequestUri?.ToString().Contains("v4/games") == true)
            {
                // Verify the authorization header was set
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("mock-token", request.Headers.Authorization?.Parameter);
                Assert.Equal("test-client-id", request.Headers.GetValues("Client-ID").First());

                var responseJson = """
                [
                    {
                        "id": 1337,
                        "cover": { "url": "//images.igdb.com/igdb/image/upload/t_thumb/co123.jpg" },
                        "summary": "Mock summary",
                        "genres": [{ "name": "Action" }, { "name": "RPG" }],
                        "first_release_date": 1432000000,
                        "aggregated_rating": 85.5
                    }
                ]
                """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseJson)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var client = new IgdbClient(httpClient, _config, NullLogger<IgdbClient>.Instance);

        // Act
        var result = await client.SearchAsync("The Witcher");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(1337, result.ProviderId);
        Assert.Equal("https://images.igdb.com/igdb/image/upload/t_cover_big/co123.jpg", result.CoverUrl); // Thumbnail converted to cover_big
        Assert.Equal("Mock summary", result.Summary);
        Assert.NotNull(result.Genres);
        Assert.Equal(2, result.Genres.Count);
        Assert.Contains("Action", result.Genres);
        Assert.Contains("RPG", result.Genres);
        Assert.Equal(85.5, result.Rating);
        Assert.NotNull(result.FirstReleaseDate);
        Assert.Equal(2015, result.FirstReleaseDate.Value.Year); // 1432000000 is ~May 2015
    }

    [Fact]
    public async Task SearchAsync_NoResults_ReturnsNull()
    {
        // Arrange
        var httpClient = CreateMockHttpClient(request =>
        {
            if (request.RequestUri?.ToString().Contains("oauth2/token") == true)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { access_token = "mock-token", expires_in = 3600 }))
                };
            }

            if (request.RequestUri?.ToString().Contains("v4/games") == true)
            {
                // Return empty array
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var client = new IgdbClient(httpClient, _config, NullLogger<IgdbClient>.Instance);

        // Act
        var result = await client.SearchAsync("NonExistentGame123");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetByIdAsync_ValidId_ReturnsMappedMetadata()
    {
        // Arrange
        var httpClient = CreateMockHttpClient(request =>
        {
            if (request.RequestUri?.ToString().Contains("oauth2/token") == true)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { access_token = "mock-token", expires_in = 3600 }))
                };
            }

            if (request.RequestUri?.ToString().Contains("v4/games") == true)
            {
                var requestBody = request.Content?.ReadAsStringAsync().Result ?? "";
                Assert.Contains("where id = 42;", requestBody); // Verify it queries by ID

                var responseJson = """
                [
                    {
                        "id": 42,
                        "summary": "Fetched by ID",
                        "aggregated_rating": 99.9
                    }
                ]
                """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseJson)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var client = new IgdbClient(httpClient, _config, NullLogger<IgdbClient>.Instance);

        // Act
        var result = await client.GetByIdAsync(42);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(42, result.ProviderId);
        Assert.Equal("Fetched by ID", result.Summary);
        Assert.Equal(99.9, result.Rating);
        Assert.Null(result.CoverUrl); // Was missing in JSON
        Assert.Empty(result.Genres);   // Replace Assert.Null with Assert.Empty
    }

    [Fact]
    public async Task GetTokenAsync_CachesTokenUntilExpiry()
    {
        // Arrange
        int tokenRequestCount = 0;

        var httpClient = CreateMockHttpClient(request =>
        {
            if (request.RequestUri?.ToString().Contains("oauth2/token") == true)
            {
                tokenRequestCount++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { access_token = "mock-token", expires_in = 3600 }))
                };
            }

            if (request.RequestUri?.ToString().Contains("v4/games") == true)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var client = new IgdbClient(httpClient, _config, NullLogger<IgdbClient>.Instance);

        // Act
        await client.SearchAsync("Game 1");
        await client.SearchAsync("Game 2");
        await client.SearchAsync("Game 3");

        // Assert
        Assert.Equal(1, tokenRequestCount); // Token should only be requested once and cached for the next two
    }
}
