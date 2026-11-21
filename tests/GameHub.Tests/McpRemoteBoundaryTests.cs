using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GameHub.Core.Data;
using GameHub.Core.Mcp;
using GameHub.Core.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenIddict.Abstractions;
using Xunit;

namespace GameHub.Tests;

public sealed partial class McpDeviceStartupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task RemoteTransport_ManagedCredentials_EnforcesResourceTenantAndLivePermissions(bool postgres)
    {
        if (postgres && string.IsNullOrWhiteSpace(Postgres)) return;
        await using var fixture = new StartupFixture(postgres);
        var generated = ConfigureManagedRemote(fixture);
        using var parent = fixture.Factory();
        using var factory = parent.WithWebHostBuilder(web => web.ConfigureServices(services =>
            services.AddSingleton(_ => new McpClientMetadata(new HttpClient(new RemoteMetadataFixture()), TimeProvider.System))));
        using var gateway = Gateway();
        var reader = await SeedAsync(factory, gameTitle: "First library game");
        var writerTenant = Guid.NewGuid();
        var writer = await SeedAsync(factory, writerTenant, "Second library game");
        var read = await ConnectRemoteAsync(gateway, generated, reader.Token, CodexId, "http://127.0.0.1:43127/callback");
        var write = await ConnectRemoteAsync(gateway, generated, writer.Token, HermesId, "http://localhost:27892/callback",
            allowTrackingWrite: true);
        var local = await ConnectLocalAsync(gateway, generated, reader.Token);

        foreach (var credential in new[] { reader.Token, "provider.jwt.fixture", local })
        {
            using var denied = await RemoteRequestAsync(gateway, credential, "tools/list");
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            Assert.Contains("invalid_token", Assert.Single(denied.Headers.WwwAuthenticate).ToString());
        }
        using var withoutGateway = factory.CreateClient();
        using var privateRoute = await RemoteRequestAsync(withoutGateway, read.Access, "tools/list");
        Assert.Equal(HttpStatusCode.NotFound, privateRoute.StatusCode);

        using var localLibrary = await BearerGetAsync(gateway, "/api/library", local);
        Assert.Equal(HttpStatusCode.OK, localLibrary.StatusCode);
        using var foreignSurface = await BearerGetAsync(gateway, "/api/library", write.Access);
        Assert.Equal(HttpStatusCode.Unauthorized, foreignSurface.StatusCode);
        foreach (var (token, expected) in new[] { (read.Access, HttpStatusCode.Unauthorized), (local, HttpStatusCode.Forbidden) })
        {
            using var patch = new HttpRequestMessage(HttpMethod.Patch, "/api/library/1/personal")
            { Content = JsonContent.Create(new { userRating = 3 }) };
            patch.Headers.Authorization = new("Bearer", token);
            using var blocked = await gateway.SendAsync(patch);
            Assert.Equal(expected, blocked.StatusCode);
        }

        using var readHttp = Gateway(read.Access);
        using var writeHttp = Gateway(write.Access);
        await using var readClient = await CreateRemoteClientAsync(readHttp);
        await using var writeClient = await CreateRemoteClientAsync(writeHttp);
        Assert.DoesNotContain(await readClient.ListToolsAsync(), tool => tool.Name == "set_rating");
        var ratingTool = Assert.Single(await writeClient.ListToolsAsync(), tool => tool.Name == "set_rating");
        Assert.False(ratingTool.JsonSchema.GetProperty("properties").TryGetProperty("tracking", out _));
        var reads = await Task.WhenAll(readClient.CallToolAsync("search_library").AsTask(),
            writeClient.CallToolAsync("search_library").AsTask());
        Assert.Contains("First library game", RemoteText(reads[0]));
        Assert.DoesNotContain("Second library game", RemoteText(reads[0]));
        Assert.Contains("Second library game", RemoteText(reads[1]));
        Assert.DoesNotContain("First library game", RemoteText(reads[1]));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
        var ownGame = await db.UnifiedGames.SingleAsync();
        var otherGame = await db.UnifiedGames.IgnoreQueryFilters().SingleAsync(game => game.TenantId == writerTenant);
        var gameRating = new Dictionary<string, object?> { ["gameId"] = otherGame.Id, ["rating"] = 4 };
        await Assert.ThrowsAsync<McpProtocolException>(() => readClient.CallToolAsync("set_rating", gameRating).AsTask());
        Assert.True((await writeClient.CallToolAsync("set_rating", new Dictionary<string, object?>
            { ["gameId"] = ownGame.Id, ["rating"] = 2 })).IsError);
        Assert.Contains("\"userRating\":4", RemoteText(await writeClient.CallToolAsync("set_rating", gameRating)));
        Assert.Contains("Completed", RemoteText(await writeClient.CallToolAsync("set_play_status", new Dictionary<string, object?>
            { ["gameId"] = otherGame.Id, ["playStatus"] = "Completed" })));

        // Caller-supplied session and tenant values have no authority over a stateless request.
        using var substituted = await RemoteRequestAsync(gateway, read.Access, "tools/call",
            new { name = "search_library", arguments = new { tenantId = writerTenant } }, writerTenant);
        var substituteBody = await substituted.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Second library game", substituteBody);
        using var afterSubstitution = await RemoteRequestAsync(gateway, read.Access, "tools/call",
            new { name = "search_library", arguments = new { } });
        Assert.Contains("First library game", await afterSubstitution.Content.ReadAsStringAsync());

        var grant = await db.McpGrants.AsNoTracking().SingleAsync(row => row.UserAccountId == writer.Account);
        using var revoke = new HttpRequestMessage(HttpMethod.Delete, $"/api/auth/mcp-connections/{grant.Id}");
        revoke.Headers.Authorization = new("Bearer", writer.Token);
        var tracking = RemoteRequestAsync(gateway, write.Access, "tools/call",
            new { name = "set_rating", arguments = new { gameId = otherGame.Id, rating = 5 } });
        var renew = gateway.PostAsync(McpDeviceProtocol.TokenPath, Form(("client_id", HermesId),
            ("grant_type", "refresh_token"), ("resource", RemoteResource), ("refresh_token", write.Refresh)));
        using var logout = await gateway.SendAsync(revoke);
        using var racingWrite = await tracking;
        using var renewal = await renew;
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        var writeResponse = await racingWrite.Content.ReadAsStringAsync();
        var persistedRating = await db.UnifiedGames.IgnoreQueryFilters().Where(game => game.Id == otherGame.Id)
            .Select(game => game.UserRating).SingleAsync();
        Assert.Equal(writeResponse.Contains("Personal tracking updated successfully.", StringComparison.Ordinal) ? 5 : 4,
            persistedRating);
        if (renewal.IsSuccessStatusCode)
        {
            var rotated = await renewal.Content.ReadFromJsonAsync<JsonElement>();
            using var denied = await RemoteRequestAsync(gateway, rotated.GetProperty("access_token").GetString()!, "tools/list");
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        else Assert.Equal(HttpStatusCode.BadRequest, renewal.StatusCode);
        using var revoked = await RemoteRequestAsync(gateway, write.Access, "tools/list");
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        using var afterLogout = await RemoteRequestAsync(gateway, write.Access, "tools/call",
            new { name = "set_rating", arguments = new { gameId = otherGame.Id, rating = 1 } });
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
        using var browserStillLive = await BearerGetAsync(gateway, "/api/library", writer.Token);
        Assert.Equal(HttpStatusCode.OK, browserStillLive.StatusCode);
        Assert.Contains("First library game", RemoteText(await readClient.CallToolAsync("search_library")));

        var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
        var tokenEntry = Assert.IsType<McpProtocolToken>(await tokens.FindByReferenceIdAsync(read.Access));
        await db.Set<McpProtocolToken>().Where(row => row.Id == tokenEntry.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.ExpirationDate, DateTime.UtcNow.AddMinutes(-1)));
        using var expired = await RemoteRequestAsync(gateway, read.Access, "tools/list");
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);

        var providerGrant = await ConnectRemoteAsync(gateway, generated, writer.Token, HermesId, "http://localhost:27892/callback");
        var providerLogout = scope.ServiceProvider.GetRequiredService<OidcBackChannelLogoutService>();
        await providerLogout.ProcessAsync(new("https://provider.example", writer.Account.ToString("D"),
            Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow, null));
        using var providerRevoked = await RemoteRequestAsync(gateway, providerGrant.Access, "tools/list");
        Assert.Equal(HttpStatusCode.Unauthorized, providerRevoked.StatusCode);

        var active = await ConnectRemoteAsync(gateway, generated, reader.Token, CodexId, "http://127.0.0.1:43127/callback");
        await db.Tenants.Where(row => row.Id == TenantDefaults.DefaultTenantId)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.DeletionRequestedAtUtc, DateTime.UtcNow));
        using var deleting = await RemoteRequestAsync(gateway, active.Access, "tools/list");
        Assert.Equal(HttpStatusCode.Unauthorized, deleting.StatusCode);

        HttpClient Gateway(string? token = null)
        {
            var client = factory.CreateClient(new() { BaseAddress = new Uri("http://core.internal"), AllowAutoRedirect = false });
            client.DefaultRequestHeaders.Add(McpManagedDeployment.TransportHeader, generated.TransportKey);
            client.DefaultRequestHeaders.Add(McpManagedDeployment.GenerationHeader, generated.Generation);
            if (token is not null) client.DefaultRequestHeaders.Authorization = new("Bearer", token);
            return client;
        }
    }

    private static async Task<(string Access, string Refresh)> ConnectRemoteAsync(HttpClient gateway,
        McpManagedState generated, string browser, string clientId, string callback, bool allowTrackingWrite = false)
    {
        using var initial = await gateway.GetAsync(StartUri(clientId, callback));
        Assert.Equal(HttpStatusCode.Redirect, initial.StatusCode);
        var pending = QueryHelpers.ParseQuery(initial.Headers.Location!.Query)["request_uri"].ToString();
        using var preview = await ConsentAsync(gateway, generated, browser, clientId, pending, "preview");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var display = await preview.Content.ReadFromJsonAsync<JsonElement>();
        using var approve = await ConsentAsync(gateway, generated, browser, clientId, pending, "approve",
            display.GetProperty("confirmation").GetString(), allowTrackingWrite);
        Assert.Equal(HttpStatusCode.Redirect, approve.StatusCode);
        var code = QueryHelpers.ParseQuery(approve.Headers.Location!.Query)["code"].ToString();
        using var exchange = await RedeemAsync(gateway, clientId, code, Verifier, callback, RemoteResource);
        Assert.Equal(HttpStatusCode.OK, exchange.StatusCode);
        var result = await exchange.Content.ReadFromJsonAsync<JsonElement>();
        return (result.GetProperty("access_token").GetString()!, result.GetProperty("refresh_token").GetString()!);
    }

    private static async Task<string> ConnectLocalAsync(HttpClient gateway, McpManagedState generated, string browser)
    {
        using var pending = await gateway.PostAsync(McpDeviceProtocol.DevicePath, Form(("client_id", McpDeviceProtocol.ClientId),
            ("scope", "library:read"), ("resource", StartupFixture.Resource)));
        Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
        var device = await pending.Content.ReadFromJsonAsync<JsonElement>();
        var code = device.GetProperty("user_code").GetString()!;
        using var preview = await VerifyAsync(gateway, browser, code, "preview", bffKey: generated.ConsentKey);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var display = await preview.Content.ReadFromJsonAsync<JsonElement>();
        using var approve = await VerifyAsync(gateway, browser, code, "approve", display.GetProperty("confirmation").GetString(), generated.ConsentKey);
        Assert.Equal(HttpStatusCode.NoContent, approve.StatusCode);
        using var exchange = await gateway.PostAsync(McpDeviceProtocol.TokenPath, Form(("client_id", McpDeviceProtocol.ClientId),
            ("grant_type", "urn:ietf:params:oauth:grant-type:device_code"), ("resource", StartupFixture.Resource),
            ("device_code", device.GetProperty("device_code").GetString()!)));
        Assert.Equal(HttpStatusCode.OK, exchange.StatusCode);
        return (await exchange.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
    }

    private static async Task<HttpResponseMessage> RemoteRequestAsync(HttpClient gateway, string access, string method,
        object? parameters = null, Guid? claimedTenant = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        { Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method, @params = parameters ?? new { } }) };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add("Mcp-Protocol-Version", "2025-11-25");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        if (claimedTenant is { } tenant)
        {
            request.Headers.Add("X-GameHub-Tenant-Id", tenant.ToString());
            request.Headers.Add("Mcp-Session-Id", "caller-chosen-session");
        }
        return await gateway.SendAsync(request);
    }

    private static Task<McpClient> CreateRemoteClientAsync(HttpClient http) =>
        McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, McpRemoteResource.Path),
            TransportMode = HttpTransportMode.StreamableHttp
        }, http), new McpClientOptions { ProtocolVersion = "2025-11-25" });

    private static string RemoteText(CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        return Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
    }
}
