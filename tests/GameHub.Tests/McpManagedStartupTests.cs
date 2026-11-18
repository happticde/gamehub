using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GameHub.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace GameHub.Tests;

public sealed partial class McpDeviceStartupTests
{
    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task ManagedStartup_RemoteDiscoveryAndTransport_RequireManagedDeploymentAndGateway()
    {
        await using var fixture = new StartupFixture(postgres: false);
        using (var legacy = fixture.Factory())
        using (var legacyClient = legacy.CreateClient(new() { BaseAddress = new Uri(StartupFixture.Issuer) }))
        {
            using var unavailable = await legacyClient.PostAsync("/mcp", null);
            Assert.Equal(HttpStatusCode.NotFound, unavailable.StatusCode);
            using var metadata = await legacyClient.GetAsync("/.well-known/oauth-protected-resource/mcp");
            Assert.Equal(HttpStatusCode.NotFound, metadata.StatusCode);
        }

        var root = Path.GetDirectoryName(fixture.Values["Mcp:Device:KeyDirectory"])!;
        var core = Path.Combine(root, "remote-core");
        var store = new McpManagedKeyStore(core);
        store.Setup(Path.Combine(root, "remote-frontend"), StartupFixture.Issuer.TrimEnd('/'), enabled: true);
        var generated = store.ReadState();
        foreach (var key in fixture.Values.Keys.Where(key => key.StartsWith("Mcp:Device:", StringComparison.Ordinal)).ToArray())
            fixture.Values.Remove(key);
        fixture.Values["Mcp:Managed:Enabled"] = "true";
        fixture.Values["Mcp:Managed:Directory"] = core;
        using var managed = fixture.Factory();
        using var client = managed.CreateClient(new() { BaseAddress = new Uri(StartupFixture.Issuer) });
        var browser = (await SeedAsync(managed)).Token;

        using var direct = await client.PostAsync("/mcp", null);
        Assert.Equal(HttpStatusCode.NotFound, direct.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        request.Headers.Add(McpManagedDeployment.TransportHeader, generated.TransportKey);
        request.Headers.Add(McpManagedDeployment.GenerationHeader, generated.Generation);
        request.Headers.Add("X-Api-Key", fixture.Values["Authentication:ApiKey"]);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", browser);
        using var rejected = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);

        const string resourcePath = "/.well-known/oauth-protected-resource/mcp";
        const string serverPath = "/.well-known/oauth-authorization-server";
        var issuer = new Uri(StartupFixture.Issuer);
        using var anonymous = await client.GetAsync(resourcePath);
        Assert.Equal(HttpStatusCode.NotFound, anonymous.StatusCode);
        client.DefaultRequestHeaders.Add(McpManagedDeployment.TransportHeader, generated.TransportKey);
        client.DefaultRequestHeaders.Add(McpManagedDeployment.GenerationHeader, generated.Generation);
        client.DefaultRequestHeaders.Host = "core:8080";
        client.DefaultRequestHeaders.Add("X-Forwarded-Host", "untrusted.example");
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "http");

        using var resourceResponse = await client.GetAsync(resourcePath);
        Assert.Equal(HttpStatusCode.OK, resourceResponse.StatusCode);
        Assert.Equal("application/json", resourceResponse.Content.Headers.ContentType?.MediaType);
        Assert.True(resourceResponse.Headers.CacheControl?.NoStore);
        var resource = await resourceResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(4, resource.EnumerateObject().Count());
        Assert.Equal(new Uri(issuer, "mcp").AbsoluteUri, resource.GetProperty("resource").GetString());
        Assert.Equal(new[] { issuer.AbsoluteUri }, ReadArray(resource, "authorization_servers"));
        Assert.Equal(new[] { "library:read" }, ReadArray(resource, "scopes_supported"));
        Assert.Equal(new[] { "header" }, ReadArray(resource, "bearer_methods_supported"));

        using var serverResponse = await client.GetAsync(serverPath);
        Assert.Equal(HttpStatusCode.OK, serverResponse.StatusCode);
        var server = await serverResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(11, server.EnumerateObject().Count());
        Assert.Equal(issuer.AbsoluteUri, server.GetProperty("issuer").GetString());
        Assert.Equal(new Uri(issuer, "connect/authorize").AbsoluteUri, server.GetProperty("authorization_endpoint").GetString());
        Assert.Equal(new Uri(issuer, "connect/token").AbsoluteUri, server.GetProperty("token_endpoint").GetString());
        Assert.Equal(new[] { "library:read", "tracking:write", "offline_access" }, ReadArray(server, "scopes_supported"));
        Assert.Equal(new[] { "code" }, ReadArray(server, "response_types_supported"));
        Assert.Equal(new[] { "query" }, ReadArray(server, "response_modes_supported"));
        Assert.Equal(new[] { "authorization_code", "refresh_token" }, ReadArray(server, "grant_types_supported"));
        Assert.Equal(new[] { "S256" }, ReadArray(server, "code_challenge_methods_supported"));
        Assert.Equal(new[] { "none" }, ReadArray(server, "token_endpoint_auth_methods_supported"));
        Assert.True(server.GetProperty("authorization_response_iss_parameter_supported").GetBoolean());
        Assert.True(server.GetProperty("client_id_metadata_document_supported").GetBoolean());

        foreach (var path in new[] { resourcePath + "/", serverPath.ToUpperInvariant(), "/.well-known/oauth-protected-resource",
            "/.well-known/openid-configuration", "/.well-known/jwks", "/connect/register" })
        {
            using var unknown = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }
        using var query = await client.GetAsync(resourcePath + "?resource=untrusted");
        Assert.Equal(HttpStatusCode.BadRequest, query.StatusCode);
        using var mutation = await client.PostAsync(serverPath, null);
        Assert.Equal(HttpStatusCode.NotFound, mutation.StatusCode);
    }

    private static string?[] ReadArray(JsonElement metadata, string property) =>
        metadata.GetProperty(property).EnumerateArray().Select(value => value.GetString()).ToArray();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task ManagedKeys_AdoptionRenewalAndRestart_PreserveActualGrantAndConsent(bool postgres)
    {
        if (postgres && string.IsNullOrWhiteSpace(Postgres)) return;
        await using var fixture = new StartupFixture(postgres);
        string browser, code, deviceCode, confirmation;
        using (var factory = fixture.Factory())
        using (var client = factory.CreateClient(new() { BaseAddress = new Uri(StartupFixture.Issuer) }))
        {
            browser = (await SeedAsync(factory)).Token;
            using var pending = await client.PostAsync(McpDeviceProtocol.DevicePath, Form(("client_id", McpDeviceProtocol.ClientId),
                ("scope", "library:read offline_access"), ("resource", StartupFixture.Resource)));
            Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
            var device = await pending.Content.ReadFromJsonAsync<JsonElement>();
            code = device.GetProperty("user_code").GetString()!;
            deviceCode = device.GetProperty("device_code").GetString()!;
            using var preview = await VerifyAsync(client, browser, code, "preview");
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            confirmation = (await preview.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("confirmation").GetString()!;
        }
        var ring = fixture.Values["Mcp:Device:KeyDirectory"]!;
        File.SetUnixFileMode(ring, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var file in Directory.EnumerateFiles(ring)) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var paths = new[] { "Signing", "Encryption", "Protection" }.Select(role => fixture.Values["Mcp:Device:" + role + "Certificate"]!).ToArray();
        foreach (var path in paths) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var core = Path.Combine(Path.GetDirectoryName(ring)!, "managed-core");
        var store = new McpManagedKeyStore(core);
        store.Setup(Path.Combine(Path.GetDirectoryName(ring)!, "managed-frontend"), StartupFixture.Issuer.TrimEnd('/'), true,
            new(StartupFixture.Issuer.TrimEnd('/'), paths[0], paths[1], paths[2], ring, StartupFixture.McpKey, "", []));
        foreach (var key in new[] { "KeyDirectory", "SigningCertificate", "EncryptionCertificate", "ProtectionCertificate" })
            fixture.Values.Remove("Mcp:Device:" + key);
        fixture.Values["Mcp:Device:ManagedKeyDirectory"] = core;
        string refresh, access;
        using (var factory = fixture.Factory())
        using (var client = factory.CreateClient(new() { BaseAddress = new Uri(StartupFixture.Issuer) }))
        {
            using var decision = await VerifyAsync(client, browser, code, "approve", confirmation);
            Assert.Equal(HttpStatusCode.NoContent, decision.StatusCode);
            using var redemption = await client.PostAsync(McpDeviceProtocol.TokenPath, Form(("client_id", McpDeviceProtocol.ClientId),
                ("grant_type", "urn:ietf:params:oauth:grant-type:device_code"), ("device_code", deviceCode), ("resource", StartupFixture.Resource)));
            Assert.Equal(HttpStatusCode.OK, redemption.StatusCode);
            var tokens = await redemption.Content.ReadFromJsonAsync<JsonElement>();
            refresh = tokens.GetProperty("refresh_token").GetString()!;
            access = tokens.GetProperty("access_token").GetString()!;
            var maintenance = factory.Services.GetServices<IHostedService>().OfType<McpKeyMaintenance>().Single();
            Assert.True(maintenance.RunOnce());
            store.Renew(force: true);
            Assert.True(maintenance.RunOnce());
            using var stillAuthorized = await BearerGetAsync(client, "/api/library", access);
            Assert.Equal(HttpStatusCode.OK, stillAuthorized.StatusCode);
            using var liveRenewal = await client.PostAsync(McpDeviceProtocol.TokenPath, Form(("client_id", McpDeviceProtocol.ClientId),
                ("grant_type", "refresh_token"), ("refresh_token", refresh), ("resource", StartupFixture.Resource)));
            Assert.Equal(HttpStatusCode.OK, liveRenewal.StatusCode);
            var renewed = await liveRenewal.Content.ReadFromJsonAsync<JsonElement>();
            refresh = renewed.GetProperty("refresh_token").GetString()!;
            access = renewed.GetProperty("access_token").GetString()!;
            using var newGeneration = await BearerGetAsync(client, "/api/library", access);
            Assert.Equal(HttpStatusCode.OK, newGeneration.StatusCode);
        }
        store.Renew(force: true);
        using var reopened = fixture.Factory();
        using var restarted = reopened.CreateClient(new() { BaseAddress = new Uri(StartupFixture.Issuer) });
        using var library = await BearerGetAsync(restarted, "/api/library", access);
        Assert.Equal(HttpStatusCode.OK, library.StatusCode);
        using var renewal = await restarted.PostAsync(McpDeviceProtocol.TokenPath, Form(("client_id", McpDeviceProtocol.ClientId),
            ("grant_type", "refresh_token"), ("refresh_token", refresh), ("resource", StartupFixture.Resource)));
        Assert.Equal(HttpStatusCode.OK, renewal.StatusCode);
    }
}
