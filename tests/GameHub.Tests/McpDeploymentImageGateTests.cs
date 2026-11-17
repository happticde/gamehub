using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using GameHub.Core.Data;
using GameHub.Core.Models;
using GameHub.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GameHub.Tests;

public sealed partial class McpDeviceStartupTests
{
    // Explicit private fixture IPC only. No test endpoint or identity is compiled into product images.
    [Fact]
    public async Task DeploymentImages_SeedDisposableDatabase()
    {
        var directory = Environment.GetEnvironmentVariable("GAMEHUB_MANAGED_SEED_DIRECTORY");
        if (directory is null) return;
        await using var fixture = new StartupFixture(false);
        fixture.Values["Mcp:Device:Issuer"] = "https://games.example/";
        fixture.Values["Mcp:Device:Resource"] = "https://games.example/api";
        fixture.Values["Mcp:Device:PublicOrigin"] = "https://games.example";
        string browser;
        using (var factory = fixture.Factory())
        {
            using var client = factory.CreateClient();
            browser = (await SeedAsync(factory)).Token;
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
            db.UnifiedGames.Add(new UnifiedGame { Id = 701, Title = "Deployment fixture", Slug = "deployment-fixture" });
            await db.SaveChangesAsync();
        }
        Directory.CreateDirectory(directory);
        using (var source = new SqliteConnection(fixture.Values["ConnectionStrings:Sqlite"]))
        using (var destination = new SqliteConnection("Data Source=" + Path.Combine(directory, "core.db")))
        {
            source.Open(); destination.Open(); source.BackupDatabase(destination);
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "browser.json"), JsonSerializer.Serialize(new { browser }));
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(Path.Combine(directory, "browser.json"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public async Task DeploymentImages_CaddyAndNativeFrontend_ConsentPrivateRoutesAndRecreation()
    {
        var directory = Environment.GetEnvironmentVariable("GAMEHUB_MANAGED_GATE_DIRECTORY");
        if (directory is null) return;
        using var root = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(directory, "root.crt"));
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false,
            ServerCertificateCustomValidationCallback = (_, certificate, presented, errors) =>
            {
                if (certificate is null || errors.HasFlag(System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch)) return false;
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(root);
                if (presented is not null) foreach (var element in presented.ChainElements)
                    chain.ChainPolicy.ExtraStore.Add(element.Certificate);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(certificate);
            } };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://games.example"), Timeout = TimeSpan.FromSeconds(20) };
        using var browserFile = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "browser.json")));
        var browser = browserFile.RootElement.GetProperty("browser").GetString()!;
        using var browserRequest = new HttpRequestMessage(HttpMethod.Get, "/library");
        browserRequest.Headers.Add("Cookie", "gamehub_access_token=" + browser);
        using var libraryPage = await client.SendAsync(browserRequest);
        Assert.Equal(HttpStatusCode.OK, libraryPage.StatusCode);
        foreach (var path in new[] { "/connect/verify", "/connect/verify/", "/connect/verify/subpath",
                     "/connect/authorize/consent", "/connect/authorize/consent/", "/connect/register",
                     "/internal/mcp/readiness", "/mcp" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        var receipt = Path.Combine(directory, "connection.json");
        string access;
        if (File.Exists(receipt))
        {
            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(receipt));
            access = saved.RootElement.GetProperty("access").GetString()!;
        }
        else
        {
            using var pending = await client.PostAsync("/connect/device", Form(("client_id", McpDeviceProtocol.ClientId),
                ("scope", "library:read offline_access"), ("resource", "https://games.example/api")));
            Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
            var device = await pending.Content.ReadFromJsonAsync<JsonElement>();
            var code = device.GetProperty("user_code").GetString()!;
            async Task<HttpResponseMessage> Consent(object value)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/mcp/consent") { Content = JsonContent.Create(value) };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                request.Headers.Add("Cookie", "gamehub_access_token=" + browser);
                request.Headers.Add("Origin", "https://games.example");
                return await client.SendAsync(request);
            }
            using var preview = await Consent(new { action = "preview", userCode = code });
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            var display = await preview.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("https://games.example", display.GetProperty("instance").GetString());
            using var approval = await Consent(new { action = "approve", userCode = code,
                confirmation = display.GetProperty("confirmation").GetString() });
            Assert.Equal(HttpStatusCode.NoContent, approval.StatusCode);
            using var token = await client.PostAsync("/connect/token", Form(("client_id", McpDeviceProtocol.ClientId),
                ("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
                ("device_code", device.GetProperty("device_code").GetString()!), ("resource", "https://games.example/api")));
            Assert.Equal(HttpStatusCode.OK, token.StatusCode);
            access = (await token.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
            await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new { access }));
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(receipt, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/library/701");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        using var library = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, library.StatusCode);
        using var forbidden = new HttpRequestMessage(HttpMethod.Patch, "/api/library/701/personal") { Content = JsonContent.Create(new { userRating = 5 }) };
        forbidden.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        using var denied = await client.SendAsync(forbidden);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }
}
