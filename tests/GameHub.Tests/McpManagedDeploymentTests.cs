using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GameHub.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GameHub.Tests;

public sealed partial class McpDeviceStartupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task ManagedDeployment_PrivateHttpConsentAndRecreation_KeepOwnerAndResourceBoundaries(bool postgres)
    {
        if (postgres && string.IsNullOrWhiteSpace(Postgres)) return;
        await using var fixture = new StartupFixture(postgres);
        var root = Path.GetDirectoryName(fixture.Values["Mcp:Device:KeyDirectory"])!;
        var store = new McpManagedKeyStore(Path.Combine(root, "managed-core"));
        store.Setup(Path.Combine(root, "managed-frontend"), StartupFixture.Issuer.TrimEnd('/'), true);
        foreach (var key in fixture.Values.Keys.Where(key => key.StartsWith("Mcp:Device:")).ToArray()) fixture.Values.Remove(key);
        fixture.Values["Mcp:Managed:Enabled"] = "true";
        fixture.Values["Mcp:Managed:Directory"] = Path.Combine(root, "managed-core");
        var state = store.ReadState();
        string access;
        using (var factory = fixture.Factory())
        using (var client = factory.CreateClient(new() { BaseAddress = new Uri("http://core:8080"), AllowAutoRedirect = false }))
        {
            var deployment = factory.Services.GetRequiredService<McpManagedDeployment>();
            Assert.DoesNotContain(state.TransportKey, deployment.ToString());
            using var denied = await client.GetAsync(McpManagedDeployment.ReadinessPath);
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
            client.DefaultRequestHeaders.Add(McpManagedDeployment.TransportHeader, state.TransportKey);
            client.DefaultRequestHeaders.Add(McpManagedDeployment.GenerationHeader, "wrong");
            using var mismatch = await client.GetAsync(McpManagedDeployment.ReadinessPath);
            Assert.Equal(HttpStatusCode.NotFound, mismatch.StatusCode);
            client.DefaultRequestHeaders.Remove(McpManagedDeployment.GenerationHeader);
            client.DefaultRequestHeaders.Add(McpManagedDeployment.GenerationHeader, state.Generation);
            using var duplicateProof = new HttpRequestMessage(HttpMethod.Get, McpManagedDeployment.ReadinessPath);
            duplicateProof.Headers.Add(McpManagedDeployment.TransportHeader, new[] { state.TransportKey, state.TransportKey });
            using var duplicateDenied = await client.SendAsync(duplicateProof);
            Assert.Equal(HttpStatusCode.NotFound, duplicateDenied.StatusCode);
            client.DefaultRequestHeaders.Add("X-Forwarded-Host", "attacker.example");
            client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "http");
            using var readinessRequest = new HttpRequestMessage(HttpMethod.Get, McpManagedDeployment.ReadinessPath);
            readinessRequest.Headers.Add(McpDeviceSettings.BffHeader, state.ConsentKey);
            readinessRequest.Headers.Add("Origin", state.PublicOrigin);
            using var readiness = await client.SendAsync(readinessRequest);
            Assert.Equal("{\"code\":\"ready\"}", await readiness.Content.ReadAsStringAsync());
            using var wrongConsent = new HttpRequestMessage(HttpMethod.Get, McpManagedDeployment.ReadinessPath);
            wrongConsent.Headers.Add(McpDeviceSettings.BffHeader, "wrong-independent-consent-key-0123456789");
            wrongConsent.Headers.Add("Origin", state.PublicOrigin);
            using var wrongPair = await client.SendAsync(wrongConsent);
            Assert.Equal(HttpStatusCode.NotFound, wrongPair.StatusCode);
            var browser = (await SeedAsync(factory)).Token;
            using var pending = await client.PostAsync(McpDeviceProtocol.DevicePath, Form(("client_id", McpDeviceProtocol.ClientId),
                ("scope", "library:read offline_access"), ("resource", StartupFixture.Resource)));
            Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
            var device = await pending.Content.ReadFromJsonAsync<JsonElement>();
            var code = device.GetProperty("user_code").GetString()!;
            var deviceCode = device.GetProperty("device_code").GetString()!;
            using var noOwner = await client.PostAsync(McpDeviceProtocol.VerifyPath, Form(("user_code", code), ("action", "preview")));
            Assert.Equal(HttpStatusCode.Forbidden, noOwner.StatusCode);
            client.DefaultRequestHeaders.Add(McpDeviceSettings.BffHeader, state.ConsentKey);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", browser);
            using var preview = await client.PostAsync(McpDeviceProtocol.VerifyPath, Form(("user_code", code), ("action", "preview")));
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            var confirmation = (await preview.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("confirmation").GetString()!;
            using var approval = await client.PostAsync(McpDeviceProtocol.VerifyPath, Form(("user_code", code), ("action", "approve"), ("confirmation", confirmation)));
            Assert.Equal(HttpStatusCode.NoContent, approval.StatusCode);
            client.DefaultRequestHeaders.Remove(McpDeviceSettings.BffHeader);
            client.DefaultRequestHeaders.Authorization = null;
            using var redemption = await client.PostAsync(McpDeviceProtocol.TokenPath, Form(("client_id", McpDeviceProtocol.ClientId),
                ("grant_type", "urn:ietf:params:oauth:grant-type:device_code"), ("device_code", deviceCode), ("resource", StartupFixture.Resource)));
            Assert.Equal(HttpStatusCode.OK, redemption.StatusCode);
            access = (await redemption.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
        }
        // Same store/database, new application process; browser access and local integration tokens coexist.
        store.Setup(Path.Combine(root, "managed-frontend"), StartupFixture.Issuer.TrimEnd('/'), true);
        using var reopened = fixture.Factory();
        using var restarted = reopened.CreateClient(new() { BaseAddress = new Uri(StartupFixture.Issuer) });
        using var library = await BearerGetAsync(restarted, "/api/library", access);
        Assert.Equal(HttpStatusCode.OK, library.StatusCode);
        using var unrelated = await BearerGetAsync(restarted, "/api/auth/sessions", access);
        Assert.NotEqual(HttpStatusCode.OK, unrelated.StatusCode);
    }

    [Fact]
    public void ManagedDeployment_ManualConfigurationConflict_FailsBeforeOpeningStorage()
    {
        using var fixture = new StartupFixture(false);
        fixture.Values["Mcp:Managed:Enabled"] = "true";
        using var factory = fixture.Factory();
        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }
}
