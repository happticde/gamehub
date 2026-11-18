using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameHub.Core.Data;
using GameHub.Core.Mcp;
using GameHub.Core.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GameHub.Tests;

public sealed partial class McpDeviceStartupTests
{
    private const string CodexId = "https://chatgpt.com/oauth/codex/client.json";
    private const string HermesId = "https://nousresearch.github.io/hermes-agent/docs/oauth/client-metadata.json";
    private const string RemoteResource = StartupFixture.Issuer + "mcp";
    private const string NativeState = "fixture-native-state";
    private static readonly string Verifier = Base64UrlTextEncoder.Encode(SHA256.HashData("test-verifier"u8));

    [Theory]
    [InlineData(false, CodexId, "http://127.0.0.1:43127/callback")]
    [InlineData(true, CodexId, "http://127.0.0.1:43127/callback")]
    [InlineData(false, HermesId, "http://localhost:27892/callback")]
    [InlineData(true, HermesId, "http://localhost:27892/callback")]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task RemoteAuthorization_NativeProfiles_IssueOneExactOwnerBoundGrant(bool postgres, string clientId, string callback)
    {
        if (postgres && string.IsNullOrWhiteSpace(Postgres)) return;
        await using var fixture = new StartupFixture(postgres);
        var generated = ConfigureManagedRemote(fixture);
        var stub = new RemoteMetadataFixture();
        using var parent = fixture.Factory();
        using var factory = parent.WithWebHostBuilder(web => web.ConfigureServices(services =>
            services.AddSingleton(_ => new McpClientMetadata(new HttpClient(stub), TimeProvider.System))));
        using var http = factory.CreateClient(new() { BaseAddress = new Uri("http://core.internal"), AllowAutoRedirect = false });
        http.DefaultRequestHeaders.Add(McpManagedDeployment.TransportHeader, generated.TransportKey);
        http.DefaultRequestHeaders.Add(McpManagedDeployment.GenerationHeader, generated.Generation);
        var browser = await SeedAsync(factory);
        var other = await SeedAsync(factory, Guid.NewGuid());

        using var unknown = await http.GetAsync(StartUri("https://unknown.example/client.json", callback));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        using var badMethod = await http.GetAsync(StartUri(clientId, callback, challengeMethod: "plain"));
        Assert.Equal(HttpStatusCode.BadRequest, badMethod.StatusCode);
        using var badRedirect = await http.GetAsync(StartUri(clientId, "https://unauthorized.example/callback"));
        Assert.Equal(HttpStatusCode.BadRequest, badRedirect.StatusCode);
        if (clientId == HermesId)
        {
            using var wrongPort = await http.GetAsync(StartUri(clientId, "http://localhost:27895/callback"));
            Assert.Equal(HttpStatusCode.BadRequest, wrongPort.StatusCode);
        }
        Assert.Empty(stub.Requests);

        using var initial = await http.GetAsync(StartUri(clientId, callback));
        Assert.Equal(HttpStatusCode.Redirect, initial.StatusCode);
        var review = initial.Headers.Location!;
        Assert.Equal(StartupFixture.Issuer + "mcp/authorize", review.GetLeftPart(UriPartial.Path));
        var pending = QueryHelpers.ParseQuery(review.Query)["request_uri"].ToString();
        Assert.Matches("^urn:ietf:params:oauth:request_uri:[a-zA-Z0-9_-]{43}$", pending);
        Assert.Equal(new[] { clientId }, stub.Requests);

        using var unsigned = await ConsentAsync(http, generated, null, clientId, pending, "preview");
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        using var preview = await ConsentAsync(http, generated, browser.Token, clientId, pending, "preview");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var display = await preview.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(RemoteResource, display.GetProperty("resource").GetString());
        Assert.Equal(browser.Account.ToString("D"), display.GetProperty("accountReference").GetString());
        Assert.True(display.GetProperty("trackingWriteRequested").GetBoolean());
        var proof = display.GetProperty("confirmation").GetString()!;
        using var switched = await ConsentAsync(http, generated, other.Token, clientId, pending, "approve", proof);
        Assert.False(switched.IsSuccessStatusCode);
        var decisions = await Task.WhenAll(
            ConsentAsync(http, generated, browser.Token, clientId, pending, "approve", proof),
            ConsentAsync(http, generated, browser.Token, clientId, pending, "approve", proof));
        Uri reply;
        using (decisions[0])
        using (decisions[1])
        {
            reply = Assert.Single(decisions, decision => decision.StatusCode == HttpStatusCode.Redirect).Headers.Location!;
            Assert.Null(Assert.Single(decisions, decision => decision.StatusCode != HttpStatusCode.Redirect).Headers.Location);
        }
        Assert.Equal(callback, reply.GetLeftPart(UriPartial.Path));
        var returned = QueryHelpers.ParseQuery(reply.Query);
        Assert.Equal(StartupFixture.Issuer, returned["iss"].ToString());
        Assert.Equal(NativeState, returned["state"].ToString());
        var code = returned["code"].ToString();
        Assert.NotEmpty(code);
        using var reusedConfirmation = await ConsentAsync(http, generated, browser.Token, clientId, pending, "approve", proof);
        Assert.False(reusedConfirmation.IsSuccessStatusCode);
        Assert.Null(reusedConfirmation.Headers.Location);

        foreach (var (resource, proofVerifier, redirect) in new[]
        {
            (StartupFixture.Resource, Verifier, callback),
            (RemoteResource, new string('A', 43), callback),
            (RemoteResource, Verifier, "http://localhost:43129/callback")
        })
        {
            using var refused = await RedeemAsync(http, clientId, code, proofVerifier, redirect, resource);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }
        using var exchange = await RedeemAsync(http, clientId, code, Verifier, callback, RemoteResource);
        Assert.Equal(HttpStatusCode.OK, exchange.StatusCode);
        var result = await exchange.Content.ReadFromJsonAsync<JsonElement>();
        var access = result.GetProperty("access_token").GetString()!;
        var refresh = result.GetProperty("refresh_token").GetString()!;
        Assert.DoesNotContain("tracking:write", result.GetProperty("scope").GetString());
        using var replayedCode = await RedeemAsync(http, clientId, code, Verifier, callback, RemoteResource);
        Assert.Equal(HttpStatusCode.BadRequest, replayedCode.StatusCode);
        using var wrongSurface = await BearerGetAsync(http, "/api/library", access);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongSurface.StatusCode);

        using var widened = await http.PostAsync(McpDeviceProtocol.TokenPath, Form(("client_id", clientId),
            ("grant_type", "refresh_token"), ("resource", RemoteResource), ("refresh_token", refresh),
            ("scope", "library:read tracking:write")));
        Assert.Equal(HttpStatusCode.BadRequest, widened.StatusCode);
        using var refreshed = await http.PostAsync(McpDeviceProtocol.TokenPath, Form(("client_id", clientId),
            ("grant_type", "refresh_token"), ("resource", RemoteResource), ("refresh_token", refresh)));
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        Assert.Single(stub.Requests);
        var next = await refreshed.Content.ReadFromJsonAsync<JsonElement>();
        using var replayedRefresh = await http.PostAsync(McpDeviceProtocol.TokenPath, Form(("client_id", clientId),
            ("grant_type", "refresh_token"), ("resource", RemoteResource), ("refresh_token", refresh)));
        Assert.Equal(HttpStatusCode.BadRequest, replayedRefresh.StatusCode);
        using var rejectedSuccessor = await http.PostAsync(McpDeviceProtocol.TokenPath, Form(("client_id", clientId),
            ("grant_type", "refresh_token"), ("resource", RemoteResource),
            ("refresh_token", next.GetProperty("refresh_token").GetString()!)));
        Assert.Equal(HttpStatusCode.BadRequest, rejectedSuccessor.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
        var grant = await db.McpGrants.AsNoTracking().SingleAsync();
        Assert.Equal(browser.Account, grant.UserAccountId);
        Assert.Equal(clientId, grant.ClientId);
        Assert.Equal(RemoteResource, grant.Resource);
        Assert.Equal(McpPermissions.LibraryRead, grant.Scopes);
        Assert.NotNull(grant.RevokedAtUtc);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static McpManagedState ConfigureManagedRemote(StartupFixture fixture)
    {
        var root = Path.GetDirectoryName(fixture.Values["Mcp:Device:KeyDirectory"])!;
        var core = Path.Combine(root, "native-core");
        var store = new McpManagedKeyStore(core);
        store.Setup(Path.Combine(root, "native-frontend"), StartupFixture.Issuer.TrimEnd('/'), enabled: true);
        foreach (var key in fixture.Values.Keys.Where(key => key.StartsWith("Mcp:Device:", StringComparison.Ordinal)).ToArray())
            fixture.Values.Remove(key);
        fixture.Values["Mcp:Managed:Enabled"] = "true";
        fixture.Values["Mcp:Managed:Directory"] = core;
        return store.ReadState();
    }

    private static string StartUri(string clientId, string redirect, string challengeMethod = "S256") =>
        QueryHelpers.AddQueryString(McpRemoteResource.AuthorizationPath, new Dictionary<string, string?>
        {
            ["client_id"] = clientId, ["response_type"] = "code", ["redirect_uri"] = redirect,
            ["scope"] = "library:read tracking:write offline_access", ["resource"] = RemoteResource,
            ["state"] = NativeState, ["code_challenge_method"] = challengeMethod,
            ["code_challenge"] = Base64UrlTextEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(Verifier)))
        });

    private static async Task<HttpResponseMessage> ConsentAsync(HttpClient http, McpManagedState generated,
        string? owner, string client, string pending, string action, string? proof = null)
    {
        var fields = new List<KeyValuePair<string, string>> { new("client_id", client), new("request_uri", pending), new("action", action) };
        if (proof is not null) fields.Add(new("confirmation", proof));
        using var request = new HttpRequestMessage(HttpMethod.Post, McpRemoteResource.ConsentPath)
        { Content = new FormUrlEncodedContent(fields) };
        request.Headers.Add(McpDeviceSettings.BffHeader, generated.ConsentKey);
        if (owner is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", owner);
        return await http.SendAsync(request);
    }

    private static Task<HttpResponseMessage> RedeemAsync(HttpClient http, string client, string code,
        string verifier, string redirect, string resource) => http.PostAsync(McpDeviceProtocol.TokenPath,
        Form(("client_id", client), ("grant_type", "authorization_code"), ("code", code),
            ("code_verifier", verifier), ("redirect_uri", redirect), ("resource", resource)));

    private sealed class RemoteMetadataFixture : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var client = McpRemoteClient.Find(request.RequestUri!.AbsoluteUri)!;
            Requests.Add(client.Id);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { client_id = client.Id, application_type = "native",
                    grant_types = new[] { "authorization_code", "refresh_token" }, response_types = new[] { "code" },
                    token_endpoint_auth_method = "none", redirect_uris = client.RedirectUris }),
                Headers = { CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(10) } }
            });
        }
    }
}
