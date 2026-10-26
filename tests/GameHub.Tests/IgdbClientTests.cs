using System.Net;
using System.Text.Json;
using GameHub.Core.Services;
using GameHub.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
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
        => CreateMockHttpClient(request => Task.FromResult(handlerFunc(request)));

    private HttpClient CreateMockHttpClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> handlerFunc)
    {
        var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handlerMock.Protected().Setup("Dispose", ItExpr.IsAny<bool>());
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .Returns((HttpRequestMessage request, CancellationToken _) => handlerFunc(request));

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

        var client = new IgdbClient(httpClient, _config, NullLogger<IgdbClient>.Instance, new MemoryCache(new MemoryCacheOptions()));

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

        var client = new IgdbClient(httpClient, _config, NullLogger<IgdbClient>.Instance, new MemoryCache(new MemoryCacheOptions()));

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

        var client = new IgdbClient(httpClient, _config, NullLogger<IgdbClient>.Instance, new MemoryCache(new MemoryCacheOptions()));

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

        var client = new IgdbClient(httpClient, _config, NullLogger<IgdbClient>.Instance, new MemoryCache(new MemoryCacheOptions()));

        // Act
        await client.SearchAsync("Game 1");
        await client.SearchAsync("Game 2");
        await client.SearchAsync("Game 3");

        // Assert
        Assert.Equal(1, tokenRequestCount); // Token should only be requested once and cached for the next two
    }

    [Fact]
    public async Task GetByIdAsync_OAuthCredentials_UsesEncodedBodyWithoutQueryString()
    {
        const string clientId = "client&with=reserved+characters";
        const string clientSecret = "secret&with=reserved+# characters";
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Igdb:ClientId"] = clientId,
            ["Igdb:ClientSecret"] = clientSecret
        }).Build();
        var tokenRequests = 0;
        using var http = CreateMockHttpClient(async request =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv")
            {
                tokenRequests++;
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("https://id.twitch.tv/oauth2/token", request.RequestUri.AbsoluteUri);
                Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
                var form = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(
                    await request.Content.ReadAsStringAsync());
                Assert.Equal(clientId, form["client_id"]);
                Assert.Equal(clientSecret, form["client_secret"]);
                Assert.Equal("client_credentials", form["grant_type"]);
                return JsonResponse("{\"access_token\":\"mock-token\",\"expires_in\":3600}");
            }

            Assert.Equal("mock-token", request.Headers.Authorization!.Parameter);
            return JsonResponse("[]");
        });
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var client = new IgdbClient(http, config, NullLogger<IgdbClient>.Instance, cache);

        await client.GetByIdAsync(42);

        Assert.Equal(1, tokenRequests);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "provider-secret-must-not-be-logged")]
    [InlineData(HttpStatusCode.OK, "{\"access_token\":\"\",\"expires_in\":3600}")]
    [InlineData(HttpStatusCode.OK, "{\"access_token\":\"expired-token\",\"expires_in\":0}")]
    public async Task GetByIdAsync_UnusableOAuthResponse_DoesNotSendGameRequestOrLogResponseBody(
        HttpStatusCode status,
        string body)
    {
        var logger = new RecordingLogger();
        var tokenContent = new TrackingContent(body);
        var requests = 0;
        using var http = CreateMockHttpClient(request =>
        {
            requests++;
            Assert.Equal("id.twitch.tv", request.RequestUri!.Host);
            return new HttpResponseMessage(status) { Content = tokenContent };
        });
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var client = new IgdbClient(http, _config, logger, cache);

        Assert.Null(await client.GetByIdAsync(42));

        Assert.Equal(1, requests);
        Assert.True(tokenContent.IsDisposed);
        Assert.DoesNotContain(logger.Messages, message => message.Contains(body, StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetByIdAsync_MissingCredentials_DoesNotContactProvider()
    {
        using var http = CreateMockHttpClient(_ => Task.FromException<HttpResponseMessage>(
            new InvalidOperationException("Unexpected HTTP request")));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var client = new IgdbClient(http, new ConfigurationBuilder().Build(), NullLogger<IgdbClient>.Instance, cache);

        Assert.Null(await client.GetByIdAsync(42));
    }

    [Fact]
    public async Task GetByIdAsync_UnauthorizedThenSuccess_RetriesOnceAndDisposesEveryResponse()
    {
        var contents = new List<TrackingContent>();
        var tokenRequests = 0;
        var gameRequests = 0;
        using var http = CreateMockHttpClient(request =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv")
            {
                tokenRequests++;
                var tokenContent = new TrackingContent(
                    JsonSerializer.Serialize(new { access_token = $"token-{tokenRequests}", expires_in = 3600 }));
                contents.Add(tokenContent);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = tokenContent };
            }

            gameRequests++;
            Assert.Equal($"token-{gameRequests}", request.Headers.Authorization!.Parameter);
            var content = new TrackingContent(gameRequests == 1 ? "expired" : "[{\"id\":42}]");
            contents.Add(content);
            return new HttpResponseMessage(gameRequests == 1 ? HttpStatusCode.Unauthorized : HttpStatusCode.OK)
            {
                Content = content
            };
        });
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var client = new IgdbClient(http, _config, NullLogger<IgdbClient>.Instance, cache);

        Assert.Equal(42, (await client.GetByIdAsync(42))!.ProviderId);
        Assert.Equal(2, tokenRequests);
        Assert.Equal(2, gameRequests);
        Assert.All(contents, content => Assert.True(content.IsDisposed));
    }

    [Fact]
    public async Task GetByIdAsync_ProviderError_LogsOnlyStatusAndDisposesResponse()
    {
        const string sensitiveBody = "provider-secret-must-not-be-logged";
        var logger = new RecordingLogger();
        var failureContent = new TrackingContent(sensitiveBody);
        using var http = CreateMockHttpClient(request => request.RequestUri!.Host == "id.twitch.tv"
            ? JsonResponse("{\"access_token\":\"mock-token\",\"expires_in\":3600}")
            : new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = failureContent });
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var client = new IgdbClient(http, _config, logger, cache);

        Assert.Null(await client.GetByIdAsync(42));
        Assert.True(failureContent.IsDisposed);
        Assert.Contains(logger.Messages, message => message.Contains("BadGateway", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, message => message.Contains(sensitiveBody, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Search_SpecialCharacters_KeepsTitleInsideOneStringLiteral(bool searchMany)
    {
        foreach (var title in new[] { "A \"quoted\" game", "A \\path", "A \\\"; limit 500; //" })
        {
            string? query = null;
            using var http = CreateMockHttpClient(async request =>
            {
                if (request.RequestUri!.Host == "id.twitch.tv")
                    return JsonResponse("{\"access_token\":\"mock-token\",\"expires_in\":3600}");

                query = await request.Content!.ReadAsStringAsync();
                return JsonResponse("[{\"id\":42,\"name\":\"Example\"}]");
            });
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var client = new IgdbClient(http, _config, NullLogger<IgdbClient>.Instance, cache);

            if (searchMany)
                await client.SearchManyAsync(title);
            else
                await client.SearchAsync(title);

            Assert.NotNull(query);
            Assert.StartsWith("search ", query);
            // Parse the wire literal independently to prove that quotes/backslashes round-trip
            // as data and cannot terminate the search clause.
            var endOfLiteral = query.LastIndexOf("; fields ", StringComparison.Ordinal);
            Assert.Equal(title, JsonSerializer.Deserialize<string>(query["search ".Length..endOfLiteral]));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Search_HtmlAndQuerySyntax_UsesFixedPlainTextProviderRequest(bool searchMany)
    {
        const string title = """<script>alert("probe")</script> \"; limit 500; // <img src=x onerror=alert(1)>""";
        var providerRequests = 0;
        using var http = CreateMockHttpClient(async request =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv")
                return JsonResponse("{\"access_token\":\"mock-token\",\"expires_in\":3600}");

            providerRequests++;
            Assert.Equal(new Uri("https://api.igdb.com/v4/games"), request.RequestUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("text/plain", request.Content!.Headers.ContentType?.MediaType);
            Assert.Equal("utf-8", request.Content.Headers.ContentType?.CharSet);
            var query = await request.Content.ReadAsStringAsync();
            var endOfLiteral = query.LastIndexOf("; fields ", StringComparison.Ordinal);
            Assert.StartsWith("search ", query);
            Assert.True(endOfLiteral > "search ".Length);
            Assert.Equal(title, JsonSerializer.Deserialize<string>(query["search ".Length..endOfLiteral]));
            Assert.Contains("<script>", query, StringComparison.Ordinal);
            Assert.DoesNotContain("&lt;script&gt;", query, StringComparison.Ordinal);
            Assert.EndsWith(searchMany ? "; limit 7;" : "; limit 5;", query);
            return JsonResponse("[{\"id\":42,\"name\":\"Example\"}]");
        });
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var client = new IgdbClient(http, _config, NullLogger<IgdbClient>.Instance, cache);

        if (searchMany)
            Assert.Single(await client.SearchManyAsync(title, 7));
        else
            Assert.NotNull(await client.SearchAsync(title));

        Assert.Equal(1, providerRequests);
    }

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body)
    };

    private sealed class TrackingContent(string body) : StringContent(body)
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class RecordingLogger : ILogger<IgdbClient>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
