using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using GameHub.Core.Data;
using GameHub.Core.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace GameHub.Tests;

public sealed class McpDeviceStartupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductionStartup_Enabled_ConsentIssuanceValidationAndRevocation(bool postgres)
    {
        if (postgres && string.IsNullOrWhiteSpace(Postgres)) return;
        await using var fixture = new StartupFixture(postgres);
        using var factory = fixture.Factory();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri(StartupFixture.Issuer), AllowAutoRedirect = false });
        var session = await SeedAsync(factory);
        using var pending = await client.PostAsync("/connect/device", Form(("client_id", McpDeviceProtocol.ClientId),
            ("scope", "library:read offline_access"), ("resource", StartupFixture.Resource)));
        var device = await pending.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
        Assert.Equal(StartupFixture.Issuer + "mcp/verify", device.GetProperty("verification_uri").GetString());
        var code = device.GetProperty("user_code").GetString()!;
        using var preview = await VerifyAsync(client, session.Token, code, "preview");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var display = await preview.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Startup owner", display.GetProperty("account").GetString());
        using var decision = await VerifyAsync(client, session.Token, code, "approve", display.GetProperty("confirmation").GetString());
        Assert.Equal(HttpStatusCode.NoContent, decision.StatusCode);
        using var tokenResponse = await client.PostAsync("/connect/token", Form(("client_id", McpDeviceProtocol.ClientId),
            ("grant_type", "urn:ietf:params:oauth:grant-type:device_code"), ("device_code", device.GetProperty("device_code").GetString()!),
            ("resource", StartupFixture.Resource)));
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
        var token = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        var access = token.GetProperty("access_token").GetString()!;
        using var library = await BearerGetAsync(client, "/api/library", access);
        Assert.Equal(HttpStatusCode.OK, library.StatusCode);
        using var account = await BearerGetAsync(client, "/api/auth/sessions", access);
        Assert.Equal(HttpStatusCode.Unauthorized, account.StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
            var grant = await db.McpGrants.SingleAsync();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await AccountSessionRevocation.RevokeFamilyAsync(db, grant.AccountSessionId, grant.UserAccountId,
                grant.TenantId, DateTime.UtcNow, default);
            await transaction.CommitAsync();
        }
        using var revoked = await BearerGetAsync(client, "/api/library", access);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.False(factory.Services.GetRequiredService<GameHub.Shared.Authentication.MultiUserMode>().IsEnabled);
    }

    [Fact]
    public void ProductionStartup_DefaultOff_DoesNotRequireKeysOrEnableProtocol()
    {
        using var fixture = new StartupFixture(false);
        fixture.Values["Mcp:Device:Enabled"] = "false";
        fixture.Values["Mcp:Device:SigningCertificate"] = "/missing/not-used.pfx";
        using var factory = fixture.Factory();
        using var client = factory.CreateClient();
        Assert.False(factory.Services.GetRequiredService<McpDeviceSettings>().Enabled);
        Assert.Null(factory.Services.GetService<McpRestResource>());
    }

    [Theory]
    [InlineData("Mcp:Device:Enabled", "maybe")]
    [InlineData("Mcp:Device:Issuer", "http://issuer.example/")]
    [InlineData("Mcp:Device:Issuer", "https://issuer.example/subpath/")]
    [InlineData("Mcp:Device:Resource", "https://issuer.example/mcp")]
    [InlineData("Mcp:Device:PublicOrigin", "https://foreign.example")]
    [InlineData("Mcp:Device:BffServiceKey", "short")]
    [InlineData("Mcp:Device:BffServiceKey", StartupFixture.OidcKey)]
    [InlineData("Authentication:ApiKey", " " + StartupFixture.McpKey + " ")]
    [InlineData("Mcp:Device:KnownProxies:0", "untrusted-host-name")]
    [InlineData("Authentication:Frontend:Enabled", "false")]
    [InlineData("Authentication:Frontend:SecureCookies", "false")]
    [InlineData("Authentication:Oidc:Enabled", "false")]
    [InlineData("Mcp:Device:SigningCertificate", "/missing/not-a-key.pfx")]
    public void ProductionStartup_InvalidTrust_AbortsInsteadOfPartiallyEnabling(string key, string value)
    {
        using var fixture = new StartupFixture(false);
        fixture.Values[key] = value;
        using var factory = fixture.Factory();
        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductionStartup_ForwardedHeaders_OnlyConfiguredProxyRestoresHttps(bool trusted)
    {
        using var fixture = new StartupFixture(false);
        fixture.Values["Mcp:Device:KnownProxies:0"] = trusted ? "127.0.0.1" : "192.0.2.90";
        using var factory = fixture.Factory();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("http://internal.example"), AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/connect/device")
        {
            Content = Form(("client_id", McpDeviceProtocol.ClientId), ("scope", "library:read"), ("resource", StartupFixture.Resource))
        };
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-Host", "issuer.example");
        // TestServer has no network peer; set it explicitly at the application boundary below.
        using var response = await client.SendAsync(request);
        Assert.Equal(trusted ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<(string Token, Guid Account)> SeedAsync(WebApplicationFactory<CoreContext> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
        var owner = new UserAccount { Id = Guid.NewGuid(), TenantId = TenantDefaults.DefaultTenantId,
            AccountType = UserAccountType.ExternalOidc, DisplayName = "Startup owner", CreatedAtUtc = DateTime.UtcNow };
        var family = Guid.NewGuid();
        var external = Guid.NewGuid();
        db.UserAccounts.Add(owner);
        db.AccountSessions.Add(new() { Id = family, UserAccountId = owner.Id, TenantId = owner.TenantId, CreatedAtUtc = DateTime.UtcNow });
        db.ExternalIdentities.Add(new() { Id = external, UserAccountId = owner.Id, TenantId = owner.TenantId,
            Issuer = "https://provider.example", Subject = owner.Id.ToString(), CreatedAtUtc = DateTime.UtcNow });
        db.OidcProviderSessions.Add(new() { Id = Guid.NewGuid(), UserAccountId = owner.Id, TenantId = owner.TenantId,
            ExternalIdentityId = external, AccountSessionId = family,
            SessionKeyHash = OidcProviderSessionKey.Hash("https://provider.example", family.ToString()), CreatedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return (scope.ServiceProvider.GetRequiredService<JwtTokenService>().CreateToken(owner, family).AccessToken, owner.Id);
    }

    private static async Task<HttpResponseMessage> VerifyAsync(HttpClient client, string token, string code, string action, string? confirmation = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/connect/verify");
        var fields = new List<KeyValuePair<string, string>> { new("user_code", code), new("action", action) };
        if (confirmation is not null) fields.Add(new("confirmation", confirmation));
        request.Content = new FormUrlEncodedContent(fields);
        request.Headers.Add(McpDeviceSettings.BffHeader, StartupFixture.McpKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> BearerGetAsync(HttpClient client, string path, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) => new(fields.Select(field => new KeyValuePair<string, string>(field.Key, field.Value)));
    private static string? Postgres => Environment.GetEnvironmentVariable("GAMEHUB_TEST_MCP_POSTGRES_CONNECTION");

    private sealed class StartupFixture : IDisposable, IAsyncDisposable
    {
        internal const string Issuer = "https://issuer.example/";
        internal const string Resource = "https://issuer.example/api";
        internal const string OidcKey = "startup-oidc-service-fixture-key-0123456789";
        internal const string McpKey = "startup-mcp-service-fixture-key-0123456789";
        private readonly string path = Path.Combine(Path.GetTempPath(), "mcp-startup-" + Guid.NewGuid().ToString("N"));
        private readonly bool postgres;
        internal Dictionary<string, string?> Values { get; }
        internal StartupFixture(bool postgres)
        {
            this.postgres = postgres;
            Directory.CreateDirectory(path);
            Directory.CreateDirectory(Path.Combine(path, "ring"));
            Values = new()
            {
                ["Database:Provider"] = postgres ? "Postgres" : "Sqlite",
                ["ConnectionStrings:Sqlite"] = "Data Source=" + Path.Combine(path, "core.db"),
                ["ConnectionStrings:Postgres"] = postgres ? new NpgsqlConnectionStringBuilder(Postgres) { Database = "mcp_startup_" + Guid.NewGuid().ToString("N") }.ConnectionString : null,
                ["Authentication:ApiKey"] = "startup-operator-fixture-key-0123456789",
                ["Authentication:Jwt:SigningKey"] = "startup-browser-signing-fixture-key-0123456789",
                ["Authentication:Oidc:Enabled"] = "true",
                ["Authentication:Oidc:Issuer"] = "https://provider.example",
                ["Authentication:Oidc:Audience"] = "gamehub-browser",
                ["Authentication:Oidc:AllowedAlgorithms"] = "RS256",
                ["Authentication:Oidc:BffServiceKey"] = OidcKey,
                ["Authentication:Frontend:Enabled"] = "true",
                ["Authentication:Frontend:SecureCookies"] = "true",
                ["Igdb:EnableBackgroundEnrichment"] = "false",
                ["Mcp:Device:Enabled"] = "true",
                ["Mcp:Device:Issuer"] = Issuer,
                ["Mcp:Device:Resource"] = Resource,
                ["Mcp:Device:PublicOrigin"] = Issuer.TrimEnd('/'),
                ["Mcp:Device:BffServiceKey"] = McpKey,
                ["Mcp:Device:KeyDirectory"] = Path.Combine(path, "ring")
            };
            foreach (var role in new[] { "Signing", "Encryption", "Protection" })
            {
                using var rsa = RSA.Create(2048);
                using var cert = new CertificateRequest("CN=" + role, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                    .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(3));
                var certificatePath = Path.Combine(path, role + ".pfx");
                File.WriteAllBytes(certificatePath, cert.Export(X509ContentType.Pfx));
                Values["Mcp:Device:" + role + "Certificate"] = certificatePath;
            }
        }

        internal WebApplicationFactory<CoreContext> Factory() => new WebApplicationFactory<CoreContext>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            foreach (var value in Values.Where(pair => pair.Value is not null)) builder.UseSetting(value.Key, value.Value);
            builder.ConfigureAppConfiguration((_, config) => { config.Sources.Clear(); config.AddInMemoryCollection(Values); });
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureServices(services => services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, PeerFilter>());
        });

        public void Dispose()
        {
            if (postgres)
            {
                var connection = new NpgsqlConnectionStringBuilder(Values["ConnectionStrings:Postgres"]);
                var database = connection.Database;
                connection.Database = "postgres";
                using var admin = new NpgsqlConnection(connection.ConnectionString);
                admin.Open();
                using var command = admin.CreateCommand();
                command.CommandText = $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)";
                command.ExecuteNonQuery();
            }
            Directory.Delete(path, true);
        }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class PeerFilter : IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, following) => { context.Connection.RemoteIpAddress = IPAddress.Loopback; await following(); });
            next(app);
        };
    }
}
