using System.Net;
using System.Text.Json;
using GameHub.Core.Data;
using GameHub.Core.Services;
using GameHub.Mcp.Authentication;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using Xunit;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace GameHub.Tests;

public sealed partial class McpDeviceProtocolTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientLogin_RealProtocolAndConsent_PersistsAcrossRestartAndLogsOut(bool postgres)
    {
        if (!OperatingSystem.IsLinux() || !DeviceHost.CanRun(postgres)) return;
        await using var host = await DeviceHost.CreateAsync(postgres: postgres, productionConsent: true);
        var browser = await host.SeedConsentBrowserAsync();
        Assert.NotNull(browser);
        var directory = Path.Combine(Path.GetTempPath(), "gamehub-client-integration-" + Guid.NewGuid().ToString("N"));
        var settings = new McpLoginSettings(DeviceHost.Issuer, DeviceHost.Resource, "integration", directory);
        try
        {
            using var http = new HttpClient(new ConsentBridge(host, browser));
            var terminal = new StringWriter();
            Assert.Equal(0, await new McpLoginCommands(settings, http, terminal).RunAsync("login"));
            McpCredential stored;
            using (var selected = await new McpCredentialStore(settings).LockAsync()) stored = (await selected.ReadAsync())!;
            Assert.DoesNotContain(stored.AccessToken, terminal.ToString());
            Assert.DoesNotContain(stored.RefreshToken, terminal.ToString());
            await host.RestartAsync();
            Assert.Equal(0, await new McpLoginCommands(settings, http, terminal).RunAsync("status"));
            var issued = JsonSerializer.SerializeToElement(new { access_token = stored.AccessToken, refresh_token = stored.RefreshToken });
            Assert.Equal(HttpStatusCode.OK, await host.AccessStatusAsync(issued));
            Assert.Equal(0, await new McpLoginCommands(settings, http, terminal).RunAsync("logout"));
            Assert.Equal(HttpStatusCode.Unauthorized, await host.AccessStatusAsync(issued));
            Assert.Equal(Errors.InvalidGrant, (await host.RefreshAsync(issued)).GetProperty("error").GetString());
            using var cleared = await new McpCredentialStore(settings).LockAsync();
            Assert.Null(await cleared.ReadAsync());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private sealed class ConsentBridge(DeviceHost host, string browser) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri)
            { Content = new ByteArrayContent(await request.Content!.ReadAsByteArrayAsync(cancellationToken)) };
            foreach (var header in request.Content.Headers) forwarded.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            var response = await host.Client.SendAsync(forwarded, cancellationToken);
            if (request.RequestUri!.AbsolutePath == McpDeviceProtocol.DevicePath)
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
                var device = json.RootElement.Clone();
                var preview = await host.PreviewAsync(device, browser);
                using var decision = await host.BrowserDecisionAsync(device, browser, preview);
                Assert.Equal(HttpStatusCode.NoContent, decision.StatusCode);
            }
            return response;
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ClientLogout_ValidCredential_ClosesOnlyItsConnection(bool postgres, bool access)
    {
        if (!DeviceHost.CanRun(postgres)) return;
        await using var host = await DeviceHost.CreateAsync(fixture: true, postgres: postgres);
        var selected = await host.ApprovedTokensAsync();
        var sameOwner = await host.ApprovedTokensAsync();
        host.SwitchConsentOwner();
        var otherOwner = await host.ApprovedTokensAsync();
        using var response = await host.ClientLogoutAsync(selected.GetProperty(access ? "access_token" : "refresh_token").GetString()!);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, await host.AccessStatusAsync(selected));
        Assert.Equal(Errors.InvalidGrant, (await host.RefreshAsync(selected)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.OK, await host.AccessStatusAsync(sameOwner));
        Assert.Equal(HttpStatusCode.OK, await host.AccessStatusAsync(otherOwner));
        await host.WithDbAsync(async db =>
        {
            var revoked = await db.McpGrants.SingleAsync(grant => grant.RevokedAtUtc != null);
            Assert.NotNull((await db.AccountSessions.FindAsync(revoked.AccountSessionId))!.RevokedAtUtc);
            Assert.Equal(Statuses.Revoked, (await db.Set<McpProtocolAuthorization>().FindAsync(revoked.ProtocolAuthorizationId))!.Status);
            Assert.All(await db.Set<McpProtocolToken>().Where(token => token.Authorization != null
                && token.Authorization.Id == revoked.ProtocolAuthorizationId).ToArrayAsync(), token => Assert.Equal(Statuses.Revoked, token.Status));
            Assert.Equal(2, await db.McpGrants.CountAsync(grant => grant.RevokedAtUtc == null));
            Assert.All(await db.OidcProviderSessions.ToArrayAsync(), provider => Assert.Null(provider.RevokedAtUtc));
        });
        using var replay = await host.ClientLogoutAsync(selected.GetProperty("refresh_token").GetString()!);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientLogout_UnknownMalformedOrForeignClient_DoesNotMutateOwners(bool postgres)
    {
        if (!DeviceHost.CanRun(postgres)) return;
        await using var host = await DeviceHost.CreateAsync(fixture: true, postgres: postgres);
        var selected = await host.ApprovedTokensAsync();
        using var unknown = await host.ClientLogoutAsync("unknown-credential");
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        using var foreign = await host.ClientLogoutAsync(selected.GetProperty("refresh_token").GetString()!, "other-client");
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        using var selector = await host.Client.PostAsync(McpDeviceProtocol.RevocationPath,
            new FormUrlEncodedContent([new("client_id", McpDeviceProtocol.ClientId), new("token", "unknown"), new("tenant_id", Guid.NewGuid().ToString())]));
        Assert.Equal(HttpStatusCode.BadRequest, selector.StatusCode);
        using var duplicate = await host.Client.PostAsync(McpDeviceProtocol.RevocationPath,
            new FormUrlEncodedContent([new("client_id", McpDeviceProtocol.ClientId), new("token", "one"), new("token", "two")]));
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await host.AccessStatusAsync(selected));
        await host.WithDbAsync(async db => Assert.Null((await db.McpGrants.SingleAsync()).RevokedAtUtc));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientLogout_FailedCommit_RollsBackAllRevocation(bool postgres)
    {
        if (!DeviceHost.CanRun(postgres)) return;
        await using var host = await DeviceHost.CreateAsync(fixture: true, postgres: postgres);
        var selected = await host.ApprovedTokensAsync();
        host.FailTokenCommit = true;
        using var failure = await host.ClientLogoutAsync(selected.GetProperty("refresh_token").GetString()!);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
        host.FailTokenCommit = false;
        Assert.Equal(HttpStatusCode.OK, await host.AccessStatusAsync(selected));
        await host.WithDbAsync(async db => Assert.Null((await db.McpGrants.SingleAsync()).RevokedAtUtc));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ClientLogout_ForeignStoredBinding_PreservesBothOwners(bool postgres, bool application)
    {
        if (!DeviceHost.CanRun(postgres)) return;
        await using var host = await DeviceHost.CreateAsync(fixture: true, postgres: postgres);
        var selected = await host.ApprovedTokensAsync();
        var selectedTokenId = "";
        await host.WithDbAsync(async db => selectedTokenId = (await db.Set<McpProtocolToken>()
            .SingleAsync(token => token.Type == TokenTypeIdentifiers.RefreshToken)).Id);
        host.SwitchConsentOwner();
        var otherOwner = await host.ApprovedTokensAsync();
        await host.WithDbAsync(async db =>
        {
            var token = await db.Set<McpProtocolToken>().SingleAsync(row => row.Id == selectedTokenId);
            if (application)
            {
                token.Application = new McpProtocolApplication
                { Id = Guid.NewGuid().ToString(), ClientId = "foreign-client", ClientType = ClientTypes.Public };
                db.Add(token.Application);
            }
            else
            {
                var foreign = await db.Set<McpProtocolToken>().Include(row => row.Authorization)
                    .SingleAsync(row => row.Id != selectedTokenId && row.Type == TokenTypeIdentifiers.RefreshToken);
                token.Authorization = foreign.Authorization;
            }
            await db.SaveChangesAsync();
        });
        using var response = await host.ClientLogoutAsync(selected.GetProperty("refresh_token").GetString()!);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await host.AccessStatusAsync(selected));
        Assert.Equal(HttpStatusCode.OK, await host.AccessStatusAsync(otherOwner));
        await host.WithDbAsync(async db => Assert.Equal(2, await db.McpGrants.CountAsync(grant => grant.RevokedAtUtc == null)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientLogout_ConcurrentRenewal_LeavesNoSurvivingCredential(bool postgres)
    {
        if (!DeviceHost.CanRun(postgres)) return;
        await using var host = await DeviceHost.CreateAsync(fixture: true, postgres: postgres);
        var selected = await host.ApprovedTokensAsync();
        // Access proof remains usable while refresh rotates, avoiding reliance on a consumed proof.
        var logout = host.ClientLogoutAsync(selected.GetProperty("access_token").GetString()!);
        var refresh = host.RefreshAsync(selected);
        using var response = await logout;
        var successor = await refresh;
        if (response.StatusCode != HttpStatusCode.OK)
        {
            using var retry = await host.ClientLogoutAsync(selected.GetProperty("access_token").GetString()!);
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, await host.AccessStatusAsync(selected));
        if (successor.TryGetProperty("access_token", out _))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await host.AccessStatusAsync(successor));
            Assert.Equal(Errors.InvalidGrant, (await host.RefreshAsync(successor)).GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task ClientRegistration_PreviousExactPermissions_UpgradesOnlyRevocation()
    {
        await using var host = await DeviceHost.CreateAsync(fixture: true);
        await host.WithDbAsync(async db =>
        {
            var client = await db.Set<McpProtocolApplication>().SingleAsync();
            client.Permissions = JsonSerializer.Serialize(JsonSerializer.Deserialize<string[]>(client.Permissions!)!
                .Where(permission => permission != Permissions.Endpoints.Revocation));
            await db.SaveChangesAsync();
        });
        await host.RestartAsync();
        await host.WithDbAsync(async db => Assert.Contains(Permissions.Endpoints.Revocation,
            JsonSerializer.Deserialize<string[]>((await db.Set<McpProtocolApplication>().SingleAsync()).Permissions!)!));
        var selected = await host.ApprovedTokensAsync();
        using var revoked = await host.ClientLogoutAsync(selected.GetProperty("refresh_token").GetString()!);
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await host.AccessStatusAsync(selected));
    }

    [Theory]
    [InlineData("origin")]
    [InlineData("query")]
    [InlineData("host")]
    [InlineData("method")]
    [InlineData("oversized")]
    public async Task ClientLogout_UnsafeTransport_IsRejectedBeforeMutation(string defect)
    {
        await using var host = await DeviceHost.CreateAsync(fixture: true);
        var selected = await host.ApprovedTokensAsync();
        using var request = new HttpRequestMessage(defect == "method" ? HttpMethod.Get : HttpMethod.Post,
            McpDeviceProtocol.RevocationPath + (defect == "query" ? "?token=unknown" : ""))
        {
            Content = new FormUrlEncodedContent([new("client_id", McpDeviceProtocol.ClientId),
                new("token", defect == "oversized" ? new string('x', 5000) : selected.GetProperty("refresh_token").GetString()!)])
        };
        if (defect == "origin") request.Headers.Add("Origin", "https://foreign.example");
        if (defect == "host") request.Headers.Host = "foreign.example";
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(defect == "oversized" ? HttpStatusCode.RequestEntityTooLarge : HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await host.AccessStatusAsync(selected));
    }

    private sealed partial class DeviceHost
    {
        internal Task<HttpResponseMessage> ClientLogoutAsync(string token, string client = McpDeviceProtocol.ClientId) =>
            Client.PostAsync(McpDeviceProtocol.RevocationPath,
                new FormUrlEncodedContent([new("client_id", client), new("token", token), new("token_type_hint", "refresh_token")]));
    }
}
