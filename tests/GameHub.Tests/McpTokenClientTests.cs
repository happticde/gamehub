using System.Net;
using System.Text.Json;
using GameHub.Mcp;
using GameHub.Mcp.Authentication;
using GameHub.Mcp.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Xunit;

namespace GameHub.Tests;

public sealed class McpTokenClientTests
{
    [Fact]
    public async Task Tools_AllSeven_UseOnlySelectedBearerAndAllowedOperations()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new TokenFixture();
        await fixture.SaveAsync(write: true);
        var paths = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("https://issuer.example", request.RequestUri!.GetLeftPart(UriPartial.Authority));
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("first-access", request.Headers.Authorization?.Parameter);
            Assert.False(request.Headers.Contains("X-Api-Key"));
            paths.Add(request.Method + " " + request.RequestUri.AbsolutePath);
            return Task.FromResult(Json(request.Method == HttpMethod.Get
                ? new { playStatus = "Playing", userRating = 3 } : new { playStatus = "Completed", userRating = 4 }));
        }));
        var tools = new LibraryTools(fixture.Api(http));
        await tools.SearchLibrary(search: "https://foreign.example/?token=untrusted");
        await tools.GetGame(42);
        await tools.GetRandomGame();
        await tools.GetStatistics();
        await tools.ListTags();
        await tools.SetPlayStatus(42, "Completed");
        await tools.SetRating(42, 4);
        Assert.Equal(new[] { "GET /api/library", "GET /api/library/42", "GET /api/library/random",
            "GET /api/library/stats", "GET /api/library/tags", "GET /api/library/42",
            "PATCH /api/library/42/personal", "GET /api/library/42", "PATCH /api/library/42/personal" }, paths);
    }

    [Fact]
    public async Task Refresh_ConcurrentClientsAndRestart_RotateOnceAndRereadReplacement()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new TokenFixture();
        await fixture.SaveAsync(expired: true);
        var requests = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            Interlocked.Increment(ref requests);
            Assert.Equal("https://issuer.example/connect/token", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("X-Api-Key"));
            var body = await request.Content!.ReadAsStringAsync();
            Assert.Contains("grant_type=refresh_token", body);
            Assert.Contains("client_id=gamehub-mcp-stdio", body);
            Assert.Contains("refresh_token=first-refresh", body);
            Assert.Contains("resource=https%3A%2F%2Fissuer.example%2Fapi", body);
            Assert.Contains("scope=library%3Aread+offline_access", body);
            await Task.Delay(150);
            return Tokens();
        }));
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
            new McpTokenProvider(fixture.Settings, http).GetAccessTokenAsync(false)));
        Assert.All(results, token => Assert.Equal("next-access", token));
        Assert.Equal(1, requests);
        Assert.Equal("next-access", await new McpTokenProvider(fixture.Settings, http).GetAccessTokenAsync(false));
        using var selected = await new McpCredentialStore(fixture.Settings).LockAsync();
        Assert.False(selected.RequiresRecovery);
        Assert.Equal("next-refresh", (await selected.ReadAsync())!.RefreshToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_OmittedOrReorderedScope_PreservesExplicitlyRequestedConsent(bool omit)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new TokenFixture();
        await fixture.SaveAsync(expired: true);
        using var http = new HttpClient(new Handler(_ => Task.FromResult(omit
            ? Json(new { access_token = "next-access", refresh_token = "next-refresh", token_type = "Bearer", expires_in = 600 })
            : Tokens(scope: "offline_access library:read"))));
        Assert.Equal("next-access", await new McpTokenProvider(fixture.Settings, http).GetAccessTokenAsync(false));
        using var stored = await new McpCredentialStore(fixture.Settings).LockAsync();
        Assert.Equal(new[] { "library:read", "offline_access" }, (await stored.ReadAsync())!.Scope.Split(' ').Order().ToArray());
    }

    [Theory]
    [InlineData("lost")]
    [InlineData("cancelled")]
    [InlineData("redirect")]
    [InlineData("invalid_grant")]
    [InlineData("outage")]
    [InlineData("malformed")]
    [InlineData("oversized")]
    [InlineData("duplicate")]
    [InlineData("scope-widening")]
    [InlineData("scope-loss")]
    [InlineData("scope-null")]
    [InlineData("same-refresh")]
    [InlineData("bad-expiry")]
    [InlineData("save-failure")]
    public async Task Refresh_UncertainOrInvalidResult_PersistsRecoveryAcrossRestartWithoutRetry(string fault)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new TokenFixture();
        await fixture.SaveAsync(expired: true);
        await fixture.SaveAsync(connection: "other");
        var requests = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            requests++;
            switch (fault)
            {
                case "lost": throw new HttpRequestException("sensitive-network-detail");
                case "cancelled": throw new OperationCanceledException("sensitive-cancellation");
                case "redirect": return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect));
                case "invalid_grant": return Task.FromResult(Json(new { error = "invalid_grant", error_description = "sensitive-body" }, 400));
                case "outage": return Task.FromResult(Json(new { error = "temporarily_unavailable" }, 503));
                case "malformed": return Task.FromResult(Raw("{broken-sensitive-body"));
                case "oversized": return Task.FromResult(Raw(new string('x', 9000)));
                case "duplicate": return Task.FromResult(Raw("{\"scope\":\"library:read offline_access\",\"scope\":\"library:read tracking:write offline_access\"}"));
                case "scope-widening": return Task.FromResult(Tokens(scope: "library:read tracking:write offline_access"));
                case "scope-loss": return Task.FromResult(Tokens(scope: "library:read"));
                case "scope-null": return Task.FromResult(Tokens(scope: null!));
                case "same-refresh": return Task.FromResult(Tokens(refresh: "first-refresh"));
                case "bad-expiry": return Task.FromResult(Tokens(seconds: 601));
                case "save-failure":
                    var path = fixture.CredentialPath;
                    File.Delete(path); Directory.CreateDirectory(path); break;
            }
            return Task.FromResult(Tokens());
        }));
        var exception = await Assert.ThrowsAsync<McpLoginException>(() =>
            new McpTokenProvider(fixture.Settings, http).GetAccessTokenAsync(false));
        Assert.Contains("logout, then login", exception.Message);
        Assert.DoesNotContain("sensitive", exception.Message);
        Assert.DoesNotContain("first-", exception.Message);
        await Assert.ThrowsAsync<McpLoginException>(() =>
            new McpTokenProvider(fixture.Settings, http).GetAccessTokenAsync(false));
        Assert.Equal(1, requests);
        using (var selected = await new McpCredentialStore(fixture.Settings).LockAsync()) Assert.True(selected.RequiresRecovery);
        Assert.Equal("first-access", await new McpTokenProvider(fixture.ForConnection("other"), http).GetAccessTokenAsync(false));
    }

    [Fact]
    public async Task Refresh_InterruptedBeforeSend_RequiresExplicitRecoveryAndLogoutClearsMarker()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new TokenFixture();
        await fixture.SaveAsync(expired: true);
        using (var selected = await new McpCredentialStore(fixture.Settings).LockAsync()) selected.BeginRefresh();
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("/connect/revoke", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Raw(""));
        }));
        await Assert.ThrowsAsync<McpLoginException>(() => new McpTokenProvider(fixture.Settings, http).GetAccessTokenAsync(false));
        var terminal = new StringWriter();
        Assert.Equal(0, await new McpLoginCommands(fixture.Settings, http, terminal).RunAsync("status"));
        Assert.Contains("requires recovery", terminal.ToString());
        Assert.Equal(0, await new McpLoginCommands(fixture.Settings, http, terminal).RunAsync("logout"));
        using var afterLogout = await new McpCredentialStore(fixture.Settings).LockAsync();
        Assert.False(afterLogout.RequiresRecovery);
        Assert.Null(await afterLogout.ReadAsync());
    }

    [Fact]
    public async Task Logout_InterruptedAfterCredentialRemoval_ClearsOrphanedRenewalStateBeforeLogin()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new TokenFixture();
        await fixture.SaveAsync(expired: true);
        using (var selected = await new McpCredentialStore(fixture.Settings).LockAsync()) selected.BeginRefresh();
        File.Delete(fixture.CredentialPath);
        using var http = new HttpClient(new Handler(_ => throw new InvalidOperationException("No network expected")));
        var terminal = new StringWriter();
        Assert.Equal(1, await new McpLoginCommands(fixture.Settings, http, terminal).RunAsync("login"));
        Assert.Equal(2, await new McpLoginCommands(fixture.Settings, http, terminal).RunAsync("logout"));
        Assert.Contains("Remote revocation could not be confirmed", terminal.ToString());
        await fixture.SaveAsync();
        Assert.Equal("first-access", await new McpTokenProvider(fixture.Settings, http).GetAccessTokenAsync(false));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("read-only")]
    public async Task Tools_MissingCredentialsOrWriteConsent_NeverUseConfiguredOperatorKey(string fault)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new TokenFixture();
        if (fault == "read-only") await fixture.SaveAsync();
        using var http = new HttpClient(new Handler(_ => throw new InvalidOperationException("No network expected")));
        var tools = new LibraryTools(fixture.Api(http));
        var exception = await Assert.ThrowsAsync<McpException>(() => fault == "missing"
            ? tools.GetStatistics() : tools.SetRating(42, 3));
        Assert.Contains("login", exception.Message);
        Assert.DoesNotContain("operator-fixture", exception.Message);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(302)]
    [InlineData(503)]
    [InlineData(999)]
    public async Task Write_RejectionRedirectOrLostReceipt_NeverRefreshesFallsBackOrReplays(int status)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new TokenFixture();
        await fixture.SaveAsync(write: true);
        var requests = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            requests++;
            Assert.False(request.Headers.Contains("X-Api-Key"));
            if (request.Method == HttpMethod.Get) return Task.FromResult(Json(new { playStatus = "Playing", userRating = 3 }));
            Assert.Equal(HttpMethod.Patch, request.Method);
            if (status == 999) throw new HttpRequestException("sensitive-fixture");
            return Task.FromResult(Json(new { error = "sensitive-fixture" }, status));
        }));
        var action = () => new LibraryTools(fixture.Api(http)).SetRating(42, 4);
        if (status is 401 or 403)
            Assert.DoesNotContain("sensitive", (await Assert.ThrowsAsync<McpException>(action)).Message);
        else Assert.StartsWith("Error:", await action());
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task Tracking_LockSpansReadAndWrite_ConcurrentLogoutWaitsForSameOwner()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new TokenFixture();
        await fixture.SaveAsync(write: true);
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.Equal("first-access", request.Headers.Authorization?.Parameter);
            if (request.Method == HttpMethod.Get) { read.SetResult(); await release.Task; }
            return Json(new { playStatus = "Playing", userRating = 3 });
        }));
        var operation = new LibraryTools(fixture.Api(http)).SetRating(42, 4);
        await read.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var waiter = new McpCredentialStore(fixture.Settings).LockAsync();
        try { await Task.Delay(100); Assert.False(waiter.IsCompleted); }
        finally { release.SetResult(); }
        Assert.DoesNotContain("Error:", await operation);
        using var acquired = await waiter;
        Assert.NotNull(await acquired.ReadAsync());
    }

    private static HttpResponseMessage Json(object body, int status = 200) => Raw(JsonSerializer.Serialize(body), status);
    private static HttpResponseMessage Raw(string body, int status = 200) => new((HttpStatusCode)status) { Content = new StringContent(body) };
    private static HttpResponseMessage Tokens(string scope = "library:read offline_access", string refresh = "next-refresh", int seconds = 600)
        => Json(new { access_token = "next-access", refresh_token = refresh, scope, token_type = "Bearer", expires_in = seconds });

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }

    private sealed class TokenFixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "gamehub-renewal-" + Guid.NewGuid().ToString("N"));
        internal McpLoginSettings Settings => ForConnection("primary");
        internal McpLoginSettings ForConnection(string name) => new("https://issuer.example/", "https://issuer.example/api", name, directory);
        internal string CredentialPath => Directory.GetFiles(directory, "credential.json", SearchOption.AllDirectories)
            .Single(path => File.ReadAllText(path).Contains("\"Connection\":\"primary\"", StringComparison.Ordinal));
        internal CoreApiClient Api(HttpClient http) => new(http, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mcp:AuthenticationMode"] = "User", ["Mcp:Issuer"] = Settings.Issuer.AbsoluteUri,
            ["Mcp:Resource"] = Settings.Resource, ["Mcp:Connection"] = Settings.Connection,
            ["Mcp:CredentialDirectory"] = Settings.Directory,
            ["Core:BaseUrl"] = "http://untrusted.example/", ["Core:ApiKey"] = "operator-fixture"
        }).Build(), NullLogger<CoreApiClient>.Instance);
        internal async Task SaveAsync(bool expired = false, bool write = false, string connection = "primary")
        {
            var settings = ForConnection(connection);
            using var selected = await new McpCredentialStore(settings).LockAsync();
            await selected.WriteAsync(new McpCredential
            {
                Issuer = settings.Issuer.AbsoluteUri, Resource = settings.Resource, Connection = settings.Connection,
                AccessToken = "first-access", RefreshToken = "first-refresh",
                Scope = write ? "library:read tracking:write offline_access" : "library:read offline_access",
                AccessExpiresAt = expired ? DateTimeOffset.UtcNow.AddSeconds(-1) : DateTimeOffset.UtcNow.AddMinutes(10)
            });
        }
        public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
