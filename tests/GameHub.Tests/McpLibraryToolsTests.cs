using System.Net;
using System.Text.Json;
using GameHub.Mcp;
using GameHub.Mcp.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameHub.Tests;

/// <summary>Verifies that single-field MCP edits preserve the other personal tracking field.</summary>
public sealed class McpLibraryToolsTests
{
    [Fact]
    public async Task SetPlayStatus_ExistingRating_PreservesRating()
    {
        JsonElement? patch = null;
        using var http = CreateClient(async request =>
        {
            if (request.Method == HttpMethod.Get)
                return JsonResponse("{\"playStatus\":\"Played\",\"userRating\":5}");

            Assert.Equal(HttpMethod.Patch, request.Method);
            patch = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync());
            return JsonResponse("{\"updated\":true}");
        });

        var result = await CreateTools(http).SetPlayStatus(42, "Completed");

        Assert.DoesNotContain("Error:", result);
        Assert.NotNull(patch);
        Assert.Equal("Completed", patch.Value.GetProperty("playStatus").GetString());
        Assert.Equal(5, patch.Value.GetProperty("userRating").GetInt32());
    }

    [Theory]
    [InlineData(3)]
    [InlineData(null)]
    public async Task SetRating_ExistingStatus_PreservesStatusAndAllowsExplicitClear(int? rating)
    {
        JsonElement? patch = null;
        using var http = CreateClient(async request =>
        {
            if (request.Method == HttpMethod.Get)
                return JsonResponse("{\"playStatus\":\"Playing\",\"userRating\":5}");

            Assert.Equal(HttpMethod.Patch, request.Method);
            patch = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync());
            return JsonResponse("{\"updated\":true}");
        });

        var result = await CreateTools(http).SetRating(42, rating);

        Assert.DoesNotContain("Error:", result);
        Assert.NotNull(patch);
        Assert.Equal("Playing", patch.Value.GetProperty("playStatus").GetString());
        Assert.Equal(rating, patch.Value.GetProperty("userRating").Deserialize<int?>());
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"playStatus\":99,\"userRating\":5}")]
    [InlineData("{\"playStatus\":\"Unknown\",\"userRating\":5}")]
    [InlineData("{\"playStatus\":\"Played\",\"userRating\":10}")]
    public async Task PersonalTracking_MalformedCurrentState_DoesNotWriteDefaults(string currentState)
    {
        var writes = 0;
        using var http = CreateClient(request =>
        {
            if (request.Method != HttpMethod.Get) writes++;
            return Task.FromResult(JsonResponse(currentState));
        });
        var tools = CreateTools(http);

        Assert.StartsWith("Error:", await tools.SetPlayStatus(42, "Completed"));
        Assert.StartsWith("Error:", await tools.SetRating(42, 4));
        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task SetPlayStatus_UndefinedNumericStatus_DoesNotSendRequest()
    {
        using var http = CreateClient(_ => throw new InvalidOperationException("Unexpected request"));

        Assert.StartsWith("Error:", await CreateTools(http).SetPlayStatus(42, "99"));
    }

    private static LibraryTools CreateTools(HttpClient http) => new(new CoreApiClient(
        http,
        new ConfigurationBuilder().Build(),
        NullLogger<CoreApiClient>.Instance));

    private static HttpClient CreateClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) =>
        new(new StubHandler(handler));

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body)
    };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request);
    }
}
