using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using GameHub.Core.Data;
using GameHub.Core.Services;
using GameHub.Shared.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using Xunit;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace GameHub.Tests;

public sealed class McpDeviceProtocolTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeviceRequest_WithoutConsent_IsOwnerlessPendingAndPollingSlowsDown(bool postgres)
    {
        if (!DeviceHost.CanRun(postgres)) return;
        await using var host = await DeviceHost.CreateAsync(postgres: postgres);
        var device = await host.DeviceAsync();
        Assert.Equal(5, device.GetProperty("interval").GetInt32());
        Assert.InRange(device.GetProperty("expires_in").GetInt32(), 590, 600);
        Assert.StartsWith(DeviceHost.Issuer, device.GetProperty("verification_uri").GetString());
        Assert.Equal(Errors.AuthorizationPending, (await host.PollAsync(device)).GetProperty("error").GetString());
        Assert.Equal(Errors.SlowDown, (await host.PollAsync(device)).GetProperty("error").GetString());
        await host.WithDbAsync(async db =>
        {
            Assert.Empty(await db.McpGrants.ToListAsync());
            Assert.Empty(await db.Set<McpProtocolAuthorization>().ToListAsync());
            Assert.All(await db.Set<McpProtocolToken>().ToListAsync(), token => Assert.Null(token.Subject));
        });
        using var denied = await host.Client.PostAsync(McpDeviceProtocol.VerifyPath,
            new FormUrlEncodedContent([new("user_code", device.GetProperty("user_code").GetString()!)]));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("no-store", denied.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://other.example/api")]
    [InlineData("https://issuer.example/mcp")]
    public async Task DeviceRequest_MissingOrSubstitutedResource_IsRejected(string? resource)
    {
        await using var host = await DeviceHost.CreateAsync();
        var fields = DeviceHost.Fields();
        fields.RemoveAll(field => field.Key == Parameters.Resource);
        if (resource is not null) fields.Add(new(Parameters.Resource, resource));
        Assert.Equal(Errors.InvalidTarget, (await host.PostAsync(McpDeviceProtocol.DevicePath, fields)).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("tracking:write")]
    [InlineData("library:read admin")]
    [InlineData("openid library:read")]
    public async Task DeviceRequest_UnsupportedScopes_AreRejected(string scopes)
    {
        await using var host = await DeviceHost.CreateAsync();
        var fields = DeviceHost.Fields();
        fields.RemoveAll(field => field.Key == Parameters.Scope);
        fields.Add(new(Parameters.Scope, scopes));
        Assert.Equal(Errors.InvalidScope, (await host.PostAsync(McpDeviceProtocol.DevicePath, fields)).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("tenant_id", "selected-owner")]
    [InlineData("account_id", "selected-owner")]
    [InlineData("client_secret", "not-a-public-client-secret")]
    [InlineData("resource", DeviceHost.Resource)]
    public async Task DeviceRequest_OwnerSelectorsOrDuplicateParameters_AreRejected(string key, string value)
    {
        await using var host = await DeviceHost.CreateAsync();
        var fields = DeviceHost.Fields();
        fields.Add(new(key, value));
        Assert.Equal(Errors.InvalidRequest, (await host.PostAsync(McpDeviceProtocol.DevicePath, fields)).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("denied", Errors.AccessDenied)]
    [InlineData("expired", Errors.ExpiredToken)]
    [InlineData("revoked", Errors.InvalidGrant)]
    public async Task Poll_TerminalRequest_NeverIssuesCredentials(string state, string error)
    {
        await using var host = await DeviceHost.CreateAsync();
        var device = await host.DeviceAsync();
        await host.WithDbAsync(async db =>
        {
            var token = await db.Set<McpProtocolToken>().SingleAsync(row => row.Type == TokenTypeIdentifiers.Private.DeviceCode);
            if (state == "expired") token.ExpirationDate = DateTime.UtcNow.AddMinutes(-1);
            else token.Status = state == "denied" ? Statuses.Rejected : Statuses.Revoked;
            await db.SaveChangesAsync();
        });
        Assert.Equal(error, (await host.PollAsync(device)).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Poll_ApprovedFixture_ConcurrentRedemptionAndReplayAreOneTime(bool postgres)
    {
        if (!DeviceHost.CanRun(postgres)) return;
        await using var host = await DeviceHost.CreateAsync(fixture: true, postgres: postgres);
        var device = await host.DeviceAsync();
        await host.ApproveAsync(device);
        var results = await Task.WhenAll(host.PollAsync(device), host.PollAsync(device));
        Assert.Equal(1, results.Count(result => result.TryGetProperty("access_token", out _)));
        await host.ResetPollingAsync();
        Assert.Equal(Errors.InvalidGrant, (await host.PollAsync(device)).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Token_FixtureApproval_PreservesResourceScopesAndRejectsSubstitution(bool postgres)
    {
        if (!DeviceHost.CanRun(postgres)) return;
        await using var host = await DeviceHost.CreateAsync(fixture: true, postgres: postgres);
        var device = await host.DeviceAsync();
        await host.ApproveAsync(device);
        var fields = DeviceHost.TokenFields(device.GetProperty("device_code").GetString()!);
        fields.Add(new(Parameters.Scope, "library:read tracking:write offline_access"));
        Assert.Equal(Errors.InvalidRequest, (await host.PostAsync(McpDeviceProtocol.TokenPath, fields)).GetProperty("error").GetString());
        await host.ResetPollingAsync();
        var issued = await host.PollAsync(device);
        Assert.True(issued.TryGetProperty("access_token", out _));
        var refresh = issued.GetProperty("refresh_token").GetString()!;
        var wrong = DeviceHost.TokenFields(refresh, refresh: true);
        wrong.RemoveAll(field => field.Key == Parameters.Resource);
        wrong.Add(new(Parameters.Resource, "https://issuer.example/mcp"));
        Assert.Equal(Errors.InvalidTarget, (await host.PostAsync(McpDeviceProtocol.TokenPath, wrong)).GetProperty("error").GetString());
        var wider = DeviceHost.TokenFields(refresh, refresh: true);
        wider.Add(new(Parameters.Scope, "library:read tracking:write offline_access"));
        Assert.Equal(Errors.InvalidGrant, (await host.PostAsync(McpDeviceProtocol.TokenPath, wider)).GetProperty("error").GetString());
        var rotated = await host.PostAsync(McpDeviceProtocol.TokenPath, DeviceHost.TokenFields(refresh, refresh: true));
        Assert.Equal("library:read offline_access", rotated.GetProperty("scope").GetString());
        Assert.True(refresh != rotated.GetProperty("refresh_token").GetString());
        Assert.Equal(Errors.InvalidGrant, (await host.PostAsync(McpDeviceProtocol.TokenPath,
            DeviceHost.TokenFields(refresh, refresh: true))).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Token_ApprovedFixtureWithClosedProductionGate_StillCannotIssue()
    {
        await using var host = await DeviceHost.CreateAsync(fixture: true, closedToken: true);
        var device = await host.DeviceAsync();
        await host.ApproveAsync(device);
        Assert.Equal(Errors.AccessDenied, (await host.PollAsync(device)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task DeviceRequest_Restart_PreservesEncryptedPendingCodeAndPollingBudget()
    {
        await using var host = await DeviceHost.CreateAsync();
        var device = await host.DeviceAsync();
        Assert.Equal(Errors.AuthorizationPending, (await host.PollAsync(device)).GetProperty("error").GetString());
        await host.RestartAsync();
        Assert.Equal(Errors.SlowDown, (await host.PollAsync(device)).GetProperty("error").GetString());
        await host.ResetPollingAsync();
        Assert.Equal(Errors.AuthorizationPending, (await host.PollAsync(device)).GetProperty("error").GetString());
        var ring = string.Join("", Directory.GetFiles(host.KeysPath, "*.xml").Select(File.ReadAllText));
        Assert.True(ring.Contains("encryptedSecret", StringComparison.Ordinal));
        Assert.False(ring.Contains("<masterKey", StringComparison.Ordinal));
        await host.WithDbAsync(async db =>
        {
            foreach (var token in await db.Set<McpProtocolToken>().ToListAsync())
            {
                Assert.False(token.Payload!.Contains(device.GetProperty("device_code").GetString()!, StringComparison.Ordinal));
                Assert.False(token.Payload.Contains("library:read", StringComparison.Ordinal));
                Assert.True(device.GetProperty("device_code").GetString() != token.ReferenceId);
                Assert.False(string.Join("", host.Logs).Contains(device.GetProperty("device_code").GetString()!, StringComparison.Ordinal));
                Assert.False(string.Join("", host.Logs).Contains(device.GetProperty("user_code").GetString()!, StringComparison.Ordinal));
            }
        });
    }

    [Theory]
    [InlineData("body")]
    [InlineData("chunked")]
    [InlineData("query")]
    [InlineData("origin")]
    [InlineData("host")]
    [InlineData("http")]
    [InlineData("json")]
    public async Task Protocol_UntrustedTransportOrUnboundedInput_IsRejected(string corruption)
    {
        await using var host = await DeviceHost.CreateAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post,
            corruption == "query" ? "/connect/device?user_code=must-not-log" : "/connect/device");
        request.Content = new FormUrlEncodedContent(DeviceHost.Fields());
        if (corruption is "body" or "chunked") request.Content = corruption == "body"
            ? new StringContent(new string('a', 5000), System.Text.Encoding.UTF8, "application/x-www-form-urlencoded")
            : new StreamContent(new NonSeekableBody(new byte[5000]));
        if (corruption == "chunked") request.Content.Headers.ContentType = new("application/x-www-form-urlencoded");
        if (corruption == "origin") request.Headers.Add("Origin", DeviceHost.Issuer);
        if (corruption == "host") request.Headers.Host = "evil.example";
        if (corruption == "http") request.RequestUri = new Uri("http://issuer.example/connect/device");
        if (corruption == "json") request.Content = JsonContent.Create(new { client_id = McpDeviceProtocol.ClientId });
        using var response = await host.Client.SendAsync(request);
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.RequestEntityTooLarge });
        Assert.DoesNotContain("must-not-log", string.Join("", host.Logs));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Admission_FullPendingStoreAndGlobalBudgets_BoundAnonymousWork(bool postgres)
    {
        if (!DeviceHost.CanRun(postgres)) return;
        await using var host = await DeviceHost.CreateAsync(postgres: postgres);
        await host.WithDbAsync(async db =>
        {
            for (var i = 0; i < McpDeviceProtocol.MaximumPending; i++) db.Set<McpProtocolToken>().Add(new()
            {
                Id = Guid.NewGuid().ToString(), Type = TokenTypeIdentifiers.Private.DeviceCode,
                Status = Statuses.Inactive, CreationDate = DateTime.UtcNow, ExpirationDate = DateTime.UtcNow.AddMinutes(5)
            });
            await db.SaveChangesAsync();
        });
        Assert.Equal(Errors.TemporarilyUnavailable, (await host.PostAsync(McpDeviceProtocol.DevicePath,
            DeviceHost.Fields())).GetProperty("error").GetString());
        using var budget = new McpProtocolBudget();
        var leases = Enumerable.Range(0, 16).Select(_ => budget.AcquireConcurrency()).ToArray();
        using var excess = budget.AcquireConcurrency();
        Assert.False(excess.IsAcquired);
        foreach (var lease in leases) lease.Dispose();
        for (var i = 0; i < 30; i++) Assert.True(budget.Admit(McpDeviceProtocol.VerifyPath));
        Assert.False(budget.Admit(McpDeviceProtocol.VerifyPath));
    }

    [Fact]
    public async Task Poll_MissingOriginalKeysOrUnavailableStorage_FailsClosed()
    {
        await using var host = await DeviceHost.CreateAsync();
        var device = await host.DeviceAsync();
        foreach (var file in Directory.GetFiles(host.KeysPath, "*.xml")) File.Delete(file);
        await host.RestartAsync();
        Assert.Equal(Errors.InvalidGrant, (await host.PollAsync(device)).GetProperty("error").GetString());
        await host.WithDbAsync(async db => await db.Database.ExecuteSqlRawAsync("DROP TABLE \"OpenIddictTokens\""));
        Assert.Equal(Errors.TemporarilyUnavailable, (await host.PollAsync(device)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Registration_ChangedClientTrust_IsRejectedOnRestart()
    {
        await using var host = await DeviceHost.CreateAsync();
        await host.WithDbAsync(async db =>
        {
            await db.Set<McpProtocolApplication>().ExecuteUpdateAsync(update => update
                .SetProperty(row => row.ClientType, ClientTypes.Confidential));
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.RestartAsync());
    }

    [Fact]
    public void ProtocolKeys_SharedCertificateOrMissingDirectory_IsRejected()
    {
        var path = Path.Combine(Path.GetTempPath(), "gamehub-keys-" + Guid.NewGuid().ToString("N"));
        using var rsa = RSA.Create(2048);
        using var certificate = new CertificateRequest("CN=fixture", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        Assert.Throws<ArgumentException>(() => new McpProtocolKeys(new DirectoryInfo(path), certificate, certificate, certificate));
        Directory.CreateDirectory(path);
        try
        {
            Assert.Throws<ArgumentException>(() => new McpProtocolKeys(new DirectoryInfo(path), certificate, certificate, certificate));
            using var differentCertificate = new CertificateRequest("CN=other", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            using var thirdCertificate = new CertificateRequest("CN=third", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            Assert.Throws<ArgumentException>(() => new McpProtocolKeys(new DirectoryInfo(path), certificate, differentCertificate, thirdCertificate));
        }
        finally { Directory.Delete(path); }
    }

    private sealed class NonSeekableBody(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class DeviceHost : IAsyncDisposable
    {
        internal const string Issuer = "https://issuer.example/";
        internal const string Resource = "https://issuer.example/api";
        private const string Protocol = OpenIddictServerAspNetCoreDefaults.AuthenticationScheme;
        private readonly string directory = Path.Combine(Path.GetTempPath(), "gamehub-device-" + Guid.NewGuid().ToString("N"));
        private readonly X509Certificate2 signing = Certificate("signing");
        private readonly X509Certificate2 encryption = Certificate("encryption");
        private readonly X509Certificate2 protection = Certificate("protection");
        private readonly Guid account = Guid.NewGuid();
        private readonly Guid browser = Guid.NewGuid();
        private readonly Guid provider = Guid.NewGuid();
        private WebApplication app = null!;
        private bool fixture;
        private bool closedToken;
        private string connection = "";
        private bool postgres;
        internal string KeysPath => Path.Combine(directory, "keys");
        internal List<string> Logs { get; } = [];
        internal HttpClient Client { get; private set; } = null!;

        internal static bool CanRun(bool postgres) => !postgres || !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("GAMEHUB_TEST_MCP_POSTGRES_CONNECTION"));

        internal static async Task<DeviceHost> CreateAsync(bool fixture = false, bool closedToken = false, bool postgres = false)
        {
            var host = new DeviceHost { fixture = fixture, closedToken = closedToken, postgres = postgres };
            Directory.CreateDirectory(host.KeysPath);
            host.connection = postgres ? new NpgsqlConnectionStringBuilder(
                Environment.GetEnvironmentVariable("GAMEHUB_TEST_MCP_POSTGRES_CONNECTION"))
                { Database = "mcp_device_" + Guid.NewGuid().ToString("N") }.ConnectionString
                : "Data Source=" + Path.Combine(host.directory, "protocol.db");
            await host.StartAsync();
            return host;
        }

        private async Task StartAsync()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production", Args = [] });
            builder.Configuration.Sources.Clear();
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Trace).AddProvider(new CaptureLogs(Logs));
            builder.WebHost.UseTestServer();
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddSingleton<TenantExecutionContext>();
            builder.Services.AddScoped<ITenantContext, AuthenticatedTenantContext>();
            builder.Services.AddDbContext<CoreContext>(options =>
            {
                if (postgres) options.UseNpgsql(connection); else options.UseSqlite(connection);
                options.ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
            });
            builder.Services.AddAuthentication();
            builder.Services.AddMcpDeviceProtocol(new(new Uri(Issuer), new Uri(Resource)),
                new McpProtocolKeys(new DirectoryInfo(KeysPath), signing, encryption, protection));
            app = builder.Build();
            if (fixture)
                app.UseWhen(context => context.Request.Path != McpDeviceProtocol.VerifyPath,
                    branch => branch.UseMcpDeviceProtocol());
            else app.UseMcpDeviceProtocol();
            app.UseAuthentication();
            if (!fixture || closedToken) app.MapClosedMcpConsent();
            if (fixture)
            {
                // Synthetic test consent only. Production has no such approval path or policy.
                app.Use(async (http, next) =>
                {
                    if (http.Request.Path == McpDeviceProtocol.VerifyPath)
                        await (await ApproveFixtureAsync(http)).ExecuteAsync(http);
                    else await next(http);
                });
                if (!closedToken) app.MapPost(McpDeviceProtocol.TokenPath, async (HttpContext http) =>
                {
                    var result = await http.AuthenticateAsync(Protocol);
                    return Results.SignIn(result.Principal!, authenticationScheme: Protocol);
                });
            }
            await WithDbAsync(async db => await db.Database.MigrateAsync());
            await using (var scope = app.Services.CreateAsyncScope())
                await scope.ServiceProvider.RegisterMcpDeviceClientAsync();
            await app.StartAsync();
            Client = app.GetTestClient();
            Client.BaseAddress = new Uri(Issuer);
        }

        private async Task<IResult> ApproveFixtureAsync(HttpContext http)
        {
            var result = await http.AuthenticateAsync(Protocol);
            if (!result.Succeeded) return Results.Unauthorized();
            var db = http.RequestServices.GetRequiredService<CoreContext>();
            var now = DateTime.UtcNow.AddSeconds(-1);
            if (!await db.UserAccounts.AnyAsync(row => row.Id == account))
            {
                db.UserAccounts.Add(new() { Id = account, TenantId = TenantDefaults.DefaultTenantId,
                    Email = "fixture@example.test", NormalizedEmail = "FIXTURE@EXAMPLE.TEST", PasswordHash = "synthetic", CreatedAtUtc = now });
                db.AccountSessions.Add(new() { Id = browser, TenantId = TenantDefaults.DefaultTenantId, UserAccountId = account, CreatedAtUtc = now });
                var identity = Guid.NewGuid();
                db.ExternalIdentities.Add(new() { Id = identity, TenantId = TenantDefaults.DefaultTenantId, UserAccountId = account,
                    Issuer = "https://provider.example", Subject = "synthetic", CreatedAtUtc = now });
                db.OidcProviderSessions.Add(new() { Id = provider, TenantId = TenantDefaults.DefaultTenantId, UserAccountId = account,
                    ExternalIdentityId = identity, AccountSessionId = browser, SessionKeyHash = new string('A', 64), CreatedAtUtc = now });
                await db.SaveChangesAsync();
            }
            var family = Guid.NewGuid();
            db.AccountSessions.Add(new() { Id = family, TenantId = TenantDefaults.DefaultTenantId, UserAccountId = account, CreatedAtUtc = now });
            var authorization = new McpProtocolAuthorization
            {
                Id = Guid.NewGuid().ToString(), Application = await db.Set<McpProtocolApplication>().SingleAsync(),
                Subject = account.ToString(), TenantId = TenantDefaults.DefaultTenantId, UserAccountId = account,
                AccountSessionId = family, Status = Statuses.Valid, Type = AuthorizationTypes.Permanent,
                Scopes = JsonSerializer.Serialize(result.Principal!.GetScopes()), CreationDate = now
            };
            db.Add(authorization);
            await db.SaveChangesAsync();
            db.McpGrants.Add(new() { Id = Guid.NewGuid(), TenantId = TenantDefaults.DefaultTenantId, UserAccountId = account,
                AccountSessionId = family, ProtocolAuthorizationId = authorization.Id, OidcProviderSessionId = provider,
                ClientId = McpDeviceProtocol.ClientId, Resource = Resource, Scopes = McpPermissions.LibraryRead,
                CreatedAtUtc = now, ExpiresAtUtc = now.AddDays(30) });
            await db.SaveChangesAsync();
            var principal = new ClaimsPrincipal(new ClaimsIdentity("synthetic-consent"));
            // The supported verification update retains the existing storage authorization ID.
            // Consent must bind the pending entry explicitly, inside #71's ownership transaction.
            var tokens = http.RequestServices.GetRequiredService<IOpenIddictTokenManager>();
            var pending = await tokens.FindByIdAsync(result.Principal!.GetClaim(Claims.Private.DeviceCodeId)!)
                ?? throw new InvalidOperationException("Fixture device entry missing.");
            var descriptor = new OpenIddictTokenDescriptor();
            await tokens.PopulateAsync(descriptor, pending);
            descriptor.AuthorizationId = authorization.Id;
            await tokens.UpdateAsync(pending, descriptor);
            principal.SetClaim(Claims.Subject, account.ToString());
            principal.SetClaim(GameHubClaimTypes.TenantId, TenantDefaults.DefaultTenantId.ToString());
            principal.SetClaim(GameHubClaimTypes.AccountSessionId, family.ToString());
            principal.SetClaim(GameHubClaimTypes.SessionVersion, "0");
            principal.SetScopes(result.Principal.GetScopes()).SetResources(result.Principal.GetResources()).SetAuthorizationId(authorization.Id);
            return Results.SignIn(principal, authenticationScheme: Protocol);
        }

        internal async Task ApproveAsync(JsonElement device)
        {
            using var response = await Client.PostAsync(McpDeviceProtocol.VerifyPath,
                new FormUrlEncodedContent([new("user_code", device.GetProperty("user_code").GetString()!)]));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        internal static List<KeyValuePair<string, string>> Fields() =>
            [new(Parameters.ClientId, McpDeviceProtocol.ClientId), new(Parameters.Scope, "library:read offline_access"), new(Parameters.Resource, Resource)];
        internal static List<KeyValuePair<string, string>> TokenFields(string code, bool refresh = false) =>
            [new(Parameters.ClientId, McpDeviceProtocol.ClientId), new(Parameters.GrantType, refresh ? GrantTypes.RefreshToken : GrantTypes.DeviceCode),
                new(refresh ? Parameters.RefreshToken : Parameters.DeviceCode, code), new(Parameters.Resource, Resource)];
        internal async Task<JsonElement> DeviceAsync()
        {
            var device = await PostAsync(McpDeviceProtocol.DevicePath, Fields());
            Assert.False(device.TryGetProperty("error", out var error), error.ValueKind == JsonValueKind.String ? error.GetString() : null);
            return device;
        }
        internal Task<JsonElement> PollAsync(JsonElement device) => PostAsync(McpDeviceProtocol.TokenPath,
            TokenFields(device.GetProperty("device_code").GetString()!));
        internal async Task<JsonElement> PostAsync(string path, IEnumerable<KeyValuePair<string, string>> fields)
        {
            using var response = await Client.PostAsync(path, new FormUrlEncodedContent(fields));
            var value = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            if (response.StatusCode != HttpStatusCode.OK)
            {
                Assert.False(value.TryGetProperty("access_token", out _));
                Assert.False(value.TryGetProperty("refresh_token", out _));
                Assert.False(value.TryGetProperty("error_description", out _));
            }
            foreach (var field in new[] { "device_code", "user_code", "access_token", "refresh_token" })
                if (value.TryGetProperty(field, out var secret))
                    Assert.False(string.Join("", Logs).Contains(secret.GetString()!, StringComparison.Ordinal),
                        "Protocol secret reached diagnostic logs.");
            return value;
        }
        internal async Task WithDbAsync(Func<CoreContext, Task> action)
        {
            await using var scope = app.Services.CreateAsyncScope();
            await action(scope.ServiceProvider.GetRequiredService<CoreContext>());
        }
        internal Task ResetPollingAsync() => WithDbAsync(async db => await db.Set<McpProtocolToken>()
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.Properties, (string?)null)));
        internal async Task RestartAsync()
        {
            Client.Dispose(); await app.DisposeAsync(); await StartAsync();
        }
        public async ValueTask DisposeAsync()
        {
            Client?.Dispose();
            if (app is not null)
            {
                if (postgres) await WithDbAsync(async db => await db.Database.EnsureDeletedAsync());
                await app.DisposeAsync();
            }
            signing.Dispose(); encryption.Dispose(); protection.Dispose();
            Directory.Delete(directory, recursive: true);
        }
        private static X509Certificate2 Certificate(string name)
        {
            using var rsa = RSA.Create(2048);
            return new CertificateRequest("CN=" + name, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(90));
        }
    }

    private sealed class CaptureLogs(List<string> entries) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Capture(entries);
        public void Dispose() { }
        private sealed class Capture(List<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (entries) entries.Add(formatter(state, exception));
            }
        }
    }
}
