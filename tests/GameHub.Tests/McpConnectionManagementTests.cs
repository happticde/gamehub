using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GameHub.Core.Data;
using GameHub.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace GameHub.Tests;

public sealed partial class McpDeviceStartupTests
{
    private const string ConnectionsPath = "/api/auth/mcp-connections";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Connections_ProductionBrowserRoutes_IsolateOwnersAndRevokeOnlySelectedGrant(bool postgres)
    {
        if (postgres && string.IsNullOrWhiteSpace(Postgres)) return;
        await using var fixture = new StartupFixture(postgres);
        using var factory = fixture.Factory();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri(StartupFixture.Issuer), AllowAutoRedirect = false });
        var owner = await SeedAsync(factory);
        var selected = await ConnectAsync(client, owner.Token);
        var independent = await ConnectAsync(client, owner.Token);
        using var browserSessions = await BearerGetAsync(client, "/api/auth/sessions", owner.Token);
        Assert.Single((await browserSessions.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
        var foreign = await SeedAsync(factory, Guid.NewGuid());
        var foreignGrant = await ConnectAsync(client, foreign.Token);

        using var list = await BearerGetAsync(client, ConnectionsPath, owner.Token);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.True(list.Headers.CacheControl?.NoStore);
        var rows = await list.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, rows.GetArrayLength());
        foreach (var row in rows.EnumerateArray())
        {
            Assert.Equal(new[] { "clientId", "createdAtUtc", "expiresAtUtc", "id", "resource", "revokedAtUtc", "scopes" },
                row.EnumerateObject().Select(property => property.Name).Order().ToArray());
            Assert.Equal(StartupFixture.Resource, row.GetProperty("resource").GetString());
            Assert.EndsWith("Z", row.GetProperty("createdAtUtc").GetString());
        }
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
        var selectedId = await db.McpGrants.Where(grant => grant.UserAccountId == owner.Account)
            .OrderBy(grant => grant.CreatedAtUtc).Select(grant => grant.Id).FirstAsync();
        var foreignId = await db.McpGrants.Where(grant => grant.UserAccountId == foreign.Account).Select(grant => grant.Id).SingleAsync();
        foreach (var id in new[] { foreignId, Guid.NewGuid() })
        {
            using var denied = await DeleteConnectionAsync(client, owner.Token, id);
            Assert.Equal(HttpStatusCode.NoContent, denied.StatusCode);
            Assert.Empty(await denied.Content.ReadAsStringAsync());
        }
        using var revoke = await DeleteConnectionAsync(client, owner.Token, selectedId);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        using var repeat = await DeleteConnectionAsync(client, owner.Token, selectedId);
        Assert.Equal(HttpStatusCode.NoContent, repeat.StatusCode);
        using var rejected = await BearerGetAsync(client, "/api/library", selected.GetProperty("access_token").GetString()!);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        using var renewal = await RenewConnectionAsync(client, selected);
        Assert.Equal(HttpStatusCode.BadRequest, renewal.StatusCode);
        foreach (var token in new[] { independent, foreignGrant })
        {
            using var access = await BearerGetAsync(client, "/api/library", token.GetProperty("access_token").GetString()!);
            Assert.Equal(HttpStatusCode.OK, access.StatusCode);
        }
        using var browser = await BearerGetAsync(client, ConnectionsPath, owner.Token);
        Assert.Single((await browser.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
        using var anonymous = await client.GetAsync(ConnectionsPath);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var operatorRequest = new HttpRequestMessage(HttpMethod.Get, ConnectionsPath);
        operatorRequest.Headers.Add("X-API-Key", fixture.Values["Authentication:ApiKey"]);
        using var operatorResponse = await client.SendAsync(operatorRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, operatorResponse.StatusCode);
        using var integration = await BearerGetAsync(client, ConnectionsPath, independent.GetProperty("access_token").GetString()!);
        Assert.Equal(HttpStatusCode.Unauthorized, integration.StatusCode);
        using var integrationRevoke = await DeleteConnectionAsync(client, independent.GetProperty("access_token").GetString()!, foreignId);
        Assert.Equal(HttpStatusCode.Unauthorized, integrationRevoke.StatusCode);
        var grantState = await db.McpGrants.AsNoTracking().SingleAsync(grant => grant.Id == selectedId);
        Assert.NotNull(grantState.RevokedAtUtc);
        Assert.NotNull((await db.AccountSessions.AsNoTracking().SingleAsync(row => row.Id == grantState.AccountSessionId)).RevokedAtUtc);
        Assert.Equal(Statuses.Revoked, (await db.Set<McpProtocolAuthorization>().AsNoTracking().SingleAsync(row => row.Id == grantState.ProtocolAuthorizationId)).Status);
        Assert.All(await db.Set<McpProtocolToken>().AsNoTracking().Where(row => row.Authorization != null
            && row.Authorization.Id == grantState.ProtocolAuthorizationId).ToArrayAsync(), row => Assert.Equal(Statuses.Revoked, row.Status));
    }

    private static async Task<JsonElement> ConnectAsync(HttpClient client, string browser)
    {
        using var pending = await client.PostAsync(McpDeviceProtocol.DevicePath, Form(("client_id", McpDeviceProtocol.ClientId),
            ("scope", "library:read offline_access"), ("resource", StartupFixture.Resource)));
        Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
        var device = await pending.Content.ReadFromJsonAsync<JsonElement>();
        var code = device.GetProperty("user_code").GetString()!;
        using var preview = await VerifyAsync(client, browser, code, "preview");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var display = await preview.Content.ReadFromJsonAsync<JsonElement>();
        using var decision = await VerifyAsync(client, browser, code, "approve", display.GetProperty("confirmation").GetString());
        Assert.Equal(HttpStatusCode.NoContent, decision.StatusCode);
        using var issued = await client.PostAsync(McpDeviceProtocol.TokenPath, Form(("client_id", McpDeviceProtocol.ClientId),
            ("grant_type", GrantTypes.DeviceCode), ("device_code", device.GetProperty("device_code").GetString()!), ("resource", StartupFixture.Resource)));
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        return await issued.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> RenewConnectionAsync(HttpClient client, JsonElement token) =>
        client.PostAsync(McpDeviceProtocol.TokenPath, Form(("client_id", McpDeviceProtocol.ClientId),
            ("grant_type", GrantTypes.RefreshToken), ("refresh_token", token.GetProperty("refresh_token").GetString()!), ("resource", StartupFixture.Resource)));

    private static async Task<HttpResponseMessage> DeleteConnectionAsync(HttpClient client, string browser, Guid id)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, ConnectionsPath + "/" + id);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", browser);
        return await client.SendAsync(request);
    }
}
