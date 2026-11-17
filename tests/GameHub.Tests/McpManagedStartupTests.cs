using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameHub.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace GameHub.Tests;

public sealed partial class McpDeviceStartupTests
{
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
