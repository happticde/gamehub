using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GameHub.Core.Mcp;
using Xunit;

namespace GameHub.Tests;

public sealed class McpClientMetadataTests
{
    private static readonly McpRemoteClient Codex = McpRemoteClient.Find("https://chatgpt.com/oauth/codex/client.json")!;

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("192.0.2.1", false)]
    [InlineData("::ffff:169.254.0.1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("1.1.1.1", true)]
    [InlineData("2606:4700::1111", true)]
    public void IsPublic_SpecialUseAndGlobalAddresses_AreDistinguished(string text, bool accepted) =>
        Assert.Equal(accepted, McpClientMetadata.IsPublic(IPAddress.Parse(text)));

    [Fact]
    public async Task Metadata_CachePolicyAndNegativeWindow_BoundRepeatFetches()
    {
        var clock = new FixtureClock();
        var source = new FixtureMetadata { MaxAge = TimeSpan.FromSeconds(5) };
        using var metadata = new McpClientMetadata(new HttpClient(source), clock);
        Assert.NotNull(await metadata.GetRedirectsAsync(Codex, default));
        clock.Now = clock.Now.AddSeconds(4);
        Assert.NotNull(await metadata.GetRedirectsAsync(Codex, default));
        Assert.Equal(1, source.Fetches);

        source.Redirects = ["https://unsupported.example/callback"];
        clock.Now = clock.Now.AddSeconds(2);
        Assert.Null(await metadata.GetRedirectsAsync(Codex, default));
        Assert.Null(await metadata.GetRedirectsAsync(Codex, default));
        Assert.Equal(2, source.Fetches);
        clock.Now = clock.Now.AddSeconds(31);
        source.Redirects = ["http://localhost/callback"];
        source.NoStore = true;
        Assert.Equal(source.Redirects, await metadata.GetRedirectsAsync(Codex, default));
        Assert.Equal(source.Redirects, await metadata.GetRedirectsAsync(Codex, default));
        Assert.Equal(4, source.Fetches);
    }

    [Fact]
    public async Task Metadata_UntrustedIdentityOrOversizedDocument_IsUnavailable()
    {
        var clock = new FixtureClock();
        var source = new FixtureMetadata { Identity = "https://other.example/client.json" };
        using var metadata = new McpClientMetadata(new HttpClient(source), clock);
        Assert.Null(await metadata.GetRedirectsAsync(Codex, default));
        source.Identity = Codex.Id;
        source.Extra = new string(' ', 17 * 1024);
        clock.Now = clock.Now.AddSeconds(31);
        Assert.Null(await metadata.GetRedirectsAsync(Codex, default));
    }

    private sealed class FixtureClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FixtureMetadata : HttpMessageHandler
    {
        internal string[] Redirects { get; set; } = ["http://localhost/callback"];
        internal string Identity { get; set; } = Codex.Id;
        internal string Extra { get; set; } = "";
        internal bool NoStore { get; set; }
        internal TimeSpan MaxAge { get; init; } = TimeSpan.FromHours(1);
        internal int Fetches { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(Codex.Id, request.RequestUri?.AbsoluteUri);
            Fetches++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    client_id = Identity, application_type = "native", redirect_uris = Redirects,
                    token_endpoint_auth_method = "none", grant_types = new[] { "authorization_code", "refresh_token" },
                    response_types = new[] { "code" }, client_name = Extra
                }),
                Headers = { CacheControl = new CacheControlHeaderValue { MaxAge = MaxAge, NoStore = NoStore } }
            });
        }
    }
}
