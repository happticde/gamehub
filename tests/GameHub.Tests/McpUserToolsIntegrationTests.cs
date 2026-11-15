using GameHub.Mcp;
using GameHub.Mcp.Authentication;
using GameHub.Mcp.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.TestHost;
using ModelContextProtocol;
using Xunit;

namespace GameHub.Tests;

public sealed partial class McpAuthorizationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UserClient_RealCoreTools_EnforceReadWriteConsentTenantIsolationAndRevocation(bool postgres, bool write)
    {
        if (!OperatingSystem.IsLinux() || !IntegrationHost.CanRun(postgres)) return;
        await using var host = await IntegrationHost.CreateAsync(postgres: postgres, write: write);
        var directory = Path.Combine(Path.GetTempPath(), "mcp-tools-integration-" + Guid.NewGuid().ToString("N"));
        var settings = new McpLoginSettings("https://core.example/", "https://core.example/api", "primary", directory);
        try
        {
            using (var selected = await new McpCredentialStore(settings).LockAsync())
                await selected.WriteAsync(new McpCredential
                {
                    Issuer = settings.Issuer.AbsoluteUri, Resource = settings.Resource, Connection = settings.Connection,
                    AccessToken = await host.IssueAsync(), RefreshToken = "unused-refresh-fixture",
                    Scope = write ? "library:read tracking:write offline_access" : "library:read offline_access",
                    AccessExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
                });
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mcp:AuthenticationMode"] = "User", ["Mcp:Issuer"] = settings.Issuer.AbsoluteUri,
                ["Mcp:Resource"] = settings.Resource, ["Mcp:Connection"] = settings.Connection,
                ["Mcp:CredentialDirectory"] = settings.Directory, ["Core:ApiKey"] = IntegrationHost.OperatorKey
            }).Build();
            using var client = host.CreateToolClient();
            var tools = new LibraryTools(new CoreApiClient(client, config, NullLogger<CoreApiClient>.Instance));
            foreach (var result in new[] { await tools.SearchLibrary(), await tools.GetGame(101),
                         await tools.GetRandomGame(), await tools.GetStatistics(), await tools.ListTags() })
            {
                Assert.DoesNotContain("Error:", result);
                Assert.DoesNotContain("Foreign", result);
            }
            Assert.StartsWith("Error:", await tools.GetGame(202));
            if (write)
            {
                Assert.DoesNotContain("Error:", await tools.SetPlayStatus(101, "Completed"));
                Assert.DoesNotContain("Error:", await tools.SetRating(101, 4));
                Assert.Equal(4, (await host.GameAsync(101)).UserRating);
                Assert.Equal("Completed", (await host.GameAsync(101)).PlayStatus.ToString());
                Assert.StartsWith("Error:", await tools.SetPlayStatus(202, "Completed"));
                Assert.StartsWith("Error:", await tools.SetRating(202, 5));
            }
            else
            {
                await Assert.ThrowsAsync<McpException>(() => tools.SetPlayStatus(101, "Completed"));
                await Assert.ThrowsAsync<McpException>(() => tools.SetRating(101, 4));
                Assert.Null((await host.GameAsync(101)).UserRating);
            }
            Assert.Null((await host.GameAsync(202)).UserRating);
            await host.InvalidateAsync("family");
            Assert.Contains("logout, then login", (await Assert.ThrowsAsync<McpException>(() => tools.GetStatistics())).Message);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed partial class IntegrationHost
    {
        internal HttpClient CreateToolClient() => app.GetTestClient();
    }
}

public sealed partial class McpDeviceProtocolTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UserClient_RealProtocol_RestartsRotatesAndRejectsConfirmedReplay(bool postgres)
    {
        if (!OperatingSystem.IsLinux() || !DeviceHost.CanRun(postgres)) return;
        await using var host = await DeviceHost.CreateAsync(fixture: true, postgres: postgres);
        var first = await host.ApprovedTokensAsync();
        var independent = await host.ApprovedTokensAsync();
        var directory = Path.Combine(Path.GetTempPath(), "mcp-rotation-integration-" + Guid.NewGuid().ToString("N"));
        var settings = new McpLoginSettings(DeviceHost.Issuer, DeviceHost.Resource, "primary", directory);
        try
        {
            using (var selected = await new McpCredentialStore(settings).LockAsync())
                await selected.WriteAsync(new McpCredential
                {
                    Issuer = settings.Issuer.AbsoluteUri, Resource = settings.Resource, Connection = settings.Connection,
                    AccessToken = first.GetProperty("access_token").GetString()!, RefreshToken = first.GetProperty("refresh_token").GetString()!,
                    Scope = first.GetProperty("scope").GetString()!, AccessExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1)
                });
            await host.RestartAsync();
            var providers = Enumerable.Range(0, 3).Select(_ => new McpTokenProvider(settings, host.Client));
            var results = await Task.WhenAll(providers.Select(provider => provider.GetAccessTokenAsync(false)));
            Assert.Single(results.Distinct());
            Assert.NotEqual(first.GetProperty("access_token").GetString(), results[0]);
            using var request = new HttpRequestMessage(HttpMethod.Get, "/test/access");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", results[0]);
            using var response = await host.Client.SendAsync(request);
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            Assert.True((await host.RefreshAsync(first)).TryGetProperty("error", out _));
            // The retained successor can no longer renew after confirmed reuse of its ancestor.
            using (var selected = await new McpCredentialStore(settings).LockAsync())
            {
                var current = (await selected.ReadAsync())!;
                await selected.WriteAsync(new McpCredential
                {
                    Issuer = current.Issuer, Resource = current.Resource, Connection = current.Connection,
                    AccessToken = current.AccessToken, RefreshToken = current.RefreshToken,
                    Scope = current.Scope, AccessExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1)
                });
            }
            await Assert.ThrowsAsync<McpLoginException>(() => new McpTokenProvider(settings, host.Client).GetAccessTokenAsync(false));
            Assert.Equal(System.Net.HttpStatusCode.OK, await host.AccessStatusAsync(independent));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
