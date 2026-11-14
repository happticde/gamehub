using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using GameHub.Core.Controllers;
using GameHub.Core.Data;
using GameHub.Core.Services;
using GameHub.Shared.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using Xunit;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace GameHub.Tests;

/// <summary>Exercises real reference tokens, entry validation and MVC tenant/operation boundaries.</summary>
public sealed class McpAuthorizationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Library_ReadOnlyToken_AllFiveReadsAreTenantScopedAndBothWritesDenied(bool postgres)
    {
        if (!IntegrationHost.CanRun(postgres)) return;
        await using var host = await IntegrationHost.CreateAsync(postgres: postgres);
        var token = await host.IssueAsync();
        foreach (var path in new[] { "/api/library", "/api/library/101", "/api/library/random",
                     "/api/library/tags", "/api/library/stats" })
        {
            var (status, body) = await host.SendAsync(HttpMethod.Get, path, token);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.DoesNotContain("Foreign", body, StringComparison.Ordinal);
            if (path == "/api/library/random") Assert.Contains("101", body);
            if (path == "/api/library/tags") Assert.Contains("Owned tag", body);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Get, "/api/library/202", token)).Status);
        foreach (var payload in new[] { "{\"playStatus\":1}", "{\"playStatus\":1,\"userRating\":4}" })
            Assert.Equal(HttpStatusCode.Forbidden,
                (await host.SendAsync(HttpMethod.Patch, "/api/library/101/personal", token, payload)).Status);
        Assert.Equal(0, (int)(await host.GameAsync(101)).PlayStatus);
        Assert.Null((await host.GameAsync(101)).UserRating);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Library_WriteConsent_ChangesOnlyOwnedTrackingAndRejectsUnsupportedFields(bool postgres)
    {
        if (!IntegrationHost.CanRun(postgres)) return;
        await using var host = await IntegrationHost.CreateAsync(write: true, postgres: postgres);
        var token = await host.IssueAsync();
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Patch,
            "/api/library/101/personal", token, "{\"playStatus\":1,\"userRating\":4}")).Status);
        Assert.Equal(4, (await host.GameAsync(101)).UserRating);
        foreach (var field in new[] { "\"isIgnored\":true", "\"isIgnored\":null", "\"tenantId\":null",
                     "\"title\":\"changed\"", "\"IsIgnored\":false" })
            Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Patch,
                "/api/library/101/personal", token, "{\"playStatus\":2," + field + "}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Patch,
            "/api/library/202/personal", token, "{\"playStatus\":2,\"userRating\":5}")).Status);
        Assert.False((await host.GameAsync(101)).IsIgnored);
        Assert.Null((await host.GameAsync(202)).UserRating);
    }

    [Theory]
    [InlineData("/api/library/sync", "POST")]
    [InlineData("/api/library/sync/status", "GET")]
    [InlineData("/api/library/developers", "GET")]
    [InlineData("/api/library/101/physical-copies", "GET")]
    [InlineData("/api/library/merge", "POST")]
    [InlineData("/api/library/101/tags", "PUT")]
    [InlineData("/api/activity", "GET")]
    [InlineData("/api/auth/sessions", "GET")]
    [InlineData("/api/platform-credentials", "GET")]
    [InlineData("/api/backup", "GET")]
    [InlineData("/operator", "GET")]
    [InlineData("/web", "GET")]
    public async Task IntegrationToken_UnapprovedRoutes_DenyDirectCalls(string path, string method)
    {
        await using var host = await IntegrationHost.CreateAsync(write: true);
        var response = await host.SendAsync(new HttpMethod(method), path, await host.IssueAsync(), "{}");
        Assert.Contains(response.Status, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
    }

    public static IEnumerable<object[]> LiveInvalidationCases() =>
        from postgres in new[] { false, true }
        from change in new[] { "token", "authorization", "family", "epoch", "provider", "pending-deletion",
            "expired-grant", "deleted-grant", "database-outage" }
        select new object[] { change, postgres };

    [Theory]
    [MemberData(nameof(LiveInvalidationCases))]
    public async Task IntegrationToken_LiveInvalidation_DeniesNextRequest(string change, bool postgres)
    {
        if (!IntegrationHost.CanRun(postgres)) return;
        await using var host = await IntegrationHost.CreateAsync(expired: change == "expired-grant", postgres: postgres);
        var token = await host.IssueAsync();
        if (change != "expired-grant")
            Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, "/api/library", token)).Status);
        await host.InvalidateAsync(change);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendAsync(HttpMethod.Get, "/api/library", token)).Status);
    }

    [Theory]
    [InlineData("missing-resource")]
    [InlineData("wrong-resource")]
    [InlineData("multiple-resources")]
    [InlineData("no-read-scope")]
    [InlineData("widened-scope")]
    [InlineData("foreign-tenant")]
    [InlineData("foreign-account")]
    [InlineData("foreign-family")]
    [InlineData("stale-epoch")]
    public async Task IntegrationToken_InvalidBinding_IsRejected(string corruption)
    {
        await using var host = await IntegrationHost.CreateAsync();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await host.SendAsync(HttpMethod.Get, "/api/library", await host.IssueAsync(corruption))).Status);
    }

    [Fact]
    public async Task Schemes_BrowserAndOperatorCompatibility_RemainSeparateWithNoFallback()
    {
        await using var host = await IntegrationHost.CreateAsync();
        var browser = host.BrowserToken();
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, "/api/library", browser)).Status);
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, "/web", browser)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendAsync(HttpMethod.Get, "/integration", browser)).Status);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/library");
        request.Headers.Add("X-Api-Key", IntegrationHost.OperatorKey);
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var operatorIntegration = new HttpRequestMessage(HttpMethod.Get, "/integration");
        operatorIntegration.Headers.Add("X-Api-Key", IntegrationHost.OperatorKey);
        using var deniedOperator = await host.Client.SendAsync(operatorIntegration);
        Assert.Equal(HttpStatusCode.Unauthorized, deniedOperator.StatusCode);
        using var mixed = new HttpRequestMessage(HttpMethod.Get, "/api/library");
        mixed.Headers.Add("X-Api-Key", IntegrationHost.OperatorKey);
        mixed.Headers.Authorization = new("Bearer", new string('a', 43));
        using var rejected = await host.Client.SendAsync(mixed);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        var integration = await host.IssueAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendAsync(HttpMethod.Get, "/web", integration)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await host.SendAsync(HttpMethod.Get, "/api/library?access_token=" + integration, null)).Status);
        using var refresh = await host.Client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = integration });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task IntegrationTrust_NotRegistered_RejectsOpaqueTokens()
    {
        await using var host = await IntegrationHost.CreateAsync(validation: false);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await host.SendAsync(HttpMethod.Get, "/api/library", new string('a', 43))).Status);
    }

    [Fact]
    public async Task IntegrationToken_WrongIssuer_IsRejectedWithOtherwiseValidStorageAndKeys()
    {
        await using var host = await IntegrationHost.CreateAsync(wrongIssuer: true);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await host.SendAsync(HttpMethod.Get, "/api/library", await host.IssueAsync())).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revocation_BrowserLogoutPreservesIndependentConnection_GlobalLogoutClosesIt(bool postgres)
    {
        if (!IntegrationHost.CanRun(postgres)) return;
        await using var host = await IntegrationHost.CreateAsync(postgres: postgres);
        var token = await host.IssueAsync();
        await host.InvalidateAsync("browser-family");
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, "/api/library", token)).Status);
        await host.InvalidateAsync("global");
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendAsync(HttpMethod.Get, "/api/library", token)).Status);
    }

    private sealed class IntegrationHost : IAsyncDisposable
    {
        public const string OperatorKey = "test-operator-key-for-mcp-authorization-boundary";
        private const string Resource = "https://core.example/api";
        private const string Issuer = "https://issuer.example/";
        private const string ClientId = "gamehub-mcp-stdio";
        private const string Protocol = OpenIddictServerAspNetCoreDefaults.AuthenticationScheme;
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly SymmetricSecurityKey browserKey = new(RandomNumberGenerator.GetBytes(32));
        private WebApplication app = null!;
        private ClaimsPrincipal issuance = null!;
        private readonly Guid accountId = Guid.NewGuid();
        private readonly Guid familyId = Guid.NewGuid();
        private readonly Guid browserId = Guid.NewGuid();
        private readonly Guid providerId = Guid.NewGuid();
        private readonly string authorizationId = Guid.NewGuid().ToString();
        private string scopes = McpPermissions.LibraryRead;
        private string? postgresConnection;
        public HttpClient Client { get; private set; } = null!;

        public static bool CanRun(bool postgres) => !postgres || !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("GAMEHUB_TEST_MCP_POSTGRES_CONNECTION"));

        public static async Task<IntegrationHost> CreateAsync(bool write = false, bool expired = false,
            bool validation = true, bool postgres = false, bool wrongIssuer = false)
        {
            var host = new IntegrationHost { scopes = write ? McpPermissions.ReadAndWrite : McpPermissions.LibraryRead };
            if (postgres)
                host.postgresConnection = new NpgsqlConnectionStringBuilder(
                    Environment.GetEnvironmentVariable("GAMEHUB_TEST_MCP_POSTGRES_CONNECTION"))
                    { Database = "mcp_authorization_" + Guid.NewGuid().ToString("N") }.ConnectionString;
            await host.StartAsync(expired, validation, wrongIssuer);
            return host;
        }

        private async Task StartAsync(bool expired, bool validation, bool wrongIssuer)
        {
            await connection.OpenAsync();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production", Args = [] });
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Authentication:ApiKey"] = OperatorKey });
            builder.Logging.ClearProviders();
            builder.WebHost.UseTestServer();
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddSingleton<TenantExecutionContext>();
            builder.Services.AddScoped<ITenantContext, AuthenticatedTenantContext>();
            builder.Services.AddDbContext<CoreContext>(options =>
            {
                if (postgresConnection is null) options.UseSqlite(connection);
                else options.UseNpgsql(postgresConnection);
                options.ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
            });
            builder.Services.AddApiKeyAuthentication(builder.Configuration, builder.Environment);
            builder.Services.AddAuthentication().AddPolicyScheme("GameHub", null, options =>
                options.ForwardDefaultSelector = context => context.Request.Headers.Authorization.Count > 0
                    ? JwtBearerDefaults.AuthenticationScheme : ApiKeyAuthenticationHandler.SchemeName)
                .AddJwtBearer(options =>
                {
                    options.MapInboundClaims = false;
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuerSigningKey = true, IssuerSigningKey = browserKey,
                        ValidateIssuer = true, ValidIssuer = "browser", ValidateAudience = true, ValidAudience = "web",
                        ValidateLifetime = true, ValidAlgorithms = [SecurityAlgorithms.HmacSha256]
                    };
                });
            builder.Services.AddMcpLibraryAuthorization(jwtEnabled: true, multiUserEnabled: false);
            builder.Services.AddSingleton(new JwtSettings(true, "browser", "web",
                Convert.ToBase64String(browserKey.Key), 10, 30));
            builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
            builder.Services.AddSingleton<JwtTokenService>();
            builder.Services.AddScoped<RefreshTokenService>();
            builder.Services.AddSingleton<AuthAttemptLimiter>();
            builder.Services.AddScoped<AuthAttemptLimitFilter>();
            builder.Services.AddScoped(services => new AuthController(null!, null!,
                services.GetRequiredService<JwtTokenService>(), services.GetRequiredService<RefreshTokenService>(),
                null!, null!, null!, null!, null!, null!, null!, null!, services.GetRequiredService<ITenantContext>(),
                services.GetRequiredService<IConfiguration>(), NullLogger<AuthController>.Instance));
            if (validation)
                builder.Services.AddMcpRestTokenValidation(new(new Uri(Issuer), new Uri(Resource)), options =>
                {
                    if (wrongIssuer) options.SetConfiguration(new OpenIddictConfiguration { Issuer = new Uri(Issuer) });
                    else options.UseLocalServer();
                    options.UseDataProtection();
                });
            // Test-only issuance. No such endpoint, client, key or approval bypass exists in Core.
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
            builder.Services.AddOpenIddict().AddServer(options =>
            {
                options.SetIssuer(new Uri(wrongIssuer ? "https://other-issuer.example/" : Issuer))
                    .SetTokenEndpointUris("test/token");
                options.AllowCustomFlow("test");
                options.RegisterScopes(McpPermissions.LibraryRead, McpPermissions.TrackingWrite, "other");
                options.RegisterResources(new Uri(Resource), new Uri("https://core.example/mcp"));
                options.AddEphemeralEncryptionKey().AddEphemeralSigningKey();
                options.UseReferenceAccessTokens();
                options.UseDataProtection();
                options.UseAspNetCore().EnableTokenEndpointPassthrough();
            });
            builder.Services.AddScoped(services => new LibraryController(null!, null!, null!,
                services.GetRequiredService<CoreContext>(), NullLogger<LibraryController>.Instance,
                new SyncSchedulerStatus(), services.GetRequiredService<ITenantContext>(), null!, null!));
            builder.Services.AddControllers().AddApplicationPart(typeof(LibraryController).Assembly).AddControllersAsServices();
            app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapControllers();
            app.MapGet("/web", () => Results.Ok()).RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute
                { AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme });
            app.MapGet("/integration", () => Results.Ok()).RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute
                { AuthenticationSchemes = McpAuthorization.IntegrationScheme });
            app.MapGet("/operator", () => Results.Ok()).RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute
                { AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName });
            app.MapPost("/test/token", () => Results.SignIn(issuance, authenticationScheme: Protocol));
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
                await db.Database.MigrateAsync();
                var now = DateTime.UtcNow.AddMinutes(-2);
                db.UserAccounts.Add(new UserAccount { Id = accountId, TenantId = TenantDefaults.DefaultTenantId,
                    Email = "owner@example.test", NormalizedEmail = "OWNER@EXAMPLE.TEST", PasswordHash = "synthetic", CreatedAtUtc = now });
                db.AccountSessions.AddRange(new AccountSession { Id = familyId, UserAccountId = accountId,
                    TenantId = TenantDefaults.DefaultTenantId, CreatedAtUtc = now }, new AccountSession { Id = browserId,
                    UserAccountId = accountId, TenantId = TenantDefaults.DefaultTenantId, CreatedAtUtc = now });
                var identityId = Guid.NewGuid();
                db.ExternalIdentities.Add(new ExternalIdentity { Id = identityId, UserAccountId = accountId,
                    TenantId = TenantDefaults.DefaultTenantId, Issuer = "https://provider.example", Subject = "synthetic", CreatedAtUtc = now });
                db.OidcProviderSessions.Add(new OidcProviderSession { Id = providerId, ExternalIdentityId = identityId,
                    AccountSessionId = browserId, UserAccountId = accountId, TenantId = TenantDefaults.DefaultTenantId,
                    SessionKeyHash = new string('A', 64), CreatedAtUtc = now });
                var applicationId = "test-application";
                db.Set<McpProtocolApplication>().Add(new McpProtocolApplication { Id = applicationId,
                    ClientId = ClientId, ClientType = ClientTypes.Public, ConsentType = ConsentTypes.Explicit,
                    Permissions = JsonSerializer.Serialize(new[] { Permissions.Endpoints.Token,
                        Permissions.Prefixes.GrantType + "test", Permissions.Prefixes.Scope + McpPermissions.LibraryRead,
                        Permissions.Prefixes.Scope + McpPermissions.TrackingWrite }) });
                db.Set<McpProtocolAuthorization>().Add(new McpProtocolAuthorization { Id = authorizationId,
                    Application = await db.Set<McpProtocolApplication>().FindAsync(applicationId),
                    Subject = accountId.ToString(), Status = Statuses.Valid, Type = AuthorizationTypes.Permanent,
                    Scopes = JsonSerializer.Serialize(scopes.Split(' ')), CreationDate = now,
                    UserAccountId = accountId, TenantId = TenantDefaults.DefaultTenantId, AccountSessionId = familyId });
                await db.SaveChangesAsync();
                db.McpGrants.Add(new McpGrant { Id = Guid.NewGuid(), UserAccountId = accountId,
                    TenantId = TenantDefaults.DefaultTenantId, AccountSessionId = familyId,
                    ProtocolAuthorizationId = authorizationId, OidcProviderSessionId = providerId,
                    ClientId = ClientId, Resource = Resource, Scopes = scopes,
                    CreatedAtUtc = now, ExpiresAtUtc = expired ? now.AddMinutes(1) : now.AddDays(1) });
                var foreignTenant = Guid.NewGuid();
                db.Tenants.Add(new Tenant { Id = foreignTenant, Name = "Foreign", CreatedAtUtc = now });
                await db.SaveChangesAsync();
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "UnifiedGames" ("Id", "TenantId", "Title", "Slug", "PlayStatus", "IsIgnored", "IsPlayStatusManual")
                    VALUES (101, {TenantDefaults.DefaultTenantId}, 'Owned', 'owned', 0, {false}, {false}),
                           (202, {foreignTenant}, 'Foreign', 'foreign', 0, {false}, {false});
                    INSERT INTO "Tags" ("Id", "TenantId", "Name", "IsCustomDefinition")
                    VALUES (101, {TenantDefaults.DefaultTenantId}, 'Owned tag', {false}),
                           (202, {foreignTenant}, 'Foreign tag', {false});
                    """);
            }
            await app.StartAsync();
            Client = app.GetTestClient();
            Client.BaseAddress = new Uri(Issuer);
        }

        public async Task<string> IssueAsync(string? corruption = null)
        {
            var identity = new ClaimsIdentity("test-approval");
            identity.SetClaim(Claims.Subject, accountId.ToString());
            identity.SetClaim(GameHubClaimTypes.TenantId, TenantDefaults.DefaultTenantId.ToString());
            identity.SetClaim(GameHubClaimTypes.AccountSessionId, familyId.ToString());
            identity.SetClaim(GameHubClaimTypes.SessionVersion, "0");
            issuance = new ClaimsPrincipal(identity);
            issuance.SetAuthorizationId(authorizationId).SetScopes(scopes.Split(' ')).SetResources(Resource);
            switch (corruption)
            {
                case "missing-resource": issuance.SetResources([]); break;
                case "wrong-resource": issuance.SetResources("https://core.example/mcp"); break;
                case "multiple-resources": issuance.SetResources(Resource, "https://core.example/mcp"); break;
                case "no-read-scope": issuance.SetScopes(McpPermissions.TrackingWrite); break;
                case "widened-scope": issuance.SetScopes(McpPermissions.LibraryRead, McpPermissions.TrackingWrite); break;
                case "foreign-tenant": identity.SetClaim(GameHubClaimTypes.TenantId, Guid.NewGuid().ToString()); break;
                case "foreign-account": identity.SetClaim(Claims.Subject, Guid.NewGuid().ToString()); break;
                case "foreign-family": identity.SetClaim(GameHubClaimTypes.AccountSessionId, Guid.NewGuid().ToString()); break;
                case "stale-epoch": identity.SetClaim(GameHubClaimTypes.SessionVersion, "1"); break;
            }
            issuance.SetDestinations(_ => [Destinations.AccessToken]);
            using var response = await Client.PostAsync("/test/token", new FormUrlEncodedContent(
                [new("client_id", ClientId), new("grant_type", "test")]));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            return body.GetProperty("access_token").GetString()!;
        }

        public string BrowserToken() => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("browser", "web",
            [new Claim(ClaimTypes.NameIdentifier, accountId.ToString()), new Claim(GameHubClaimTypes.TenantId, TenantDefaults.DefaultTenantId.ToString())],
            expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new SigningCredentials(browserKey, SecurityAlgorithms.HmacSha256)));

        public async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpMethod method, string path, string? token, string? json = null)
        {
            using var request = new HttpRequestMessage(method, path);
            if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (json is not null) request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using var response = await Client.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        public async Task<UnifiedGame> GameAsync(int id)
        {
            await using var scope = app.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<CoreContext>().UnifiedGames.IgnoreQueryFilters().SingleAsync(game => game.Id == id);
        }

        public async Task InvalidateAsync(string change)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
            switch (change)
            {
                case "token": await db.Set<McpProtocolToken>().ExecuteUpdateAsync(s => s.SetProperty(row => row.Status, Statuses.Revoked)); break;
                case "authorization": await db.Set<McpProtocolAuthorization>().ExecuteUpdateAsync(s => s.SetProperty(row => row.Status, Statuses.Revoked)); break;
                case "family":
                case "browser-family":
                    await using (var transaction = await db.Database.BeginTransactionAsync())
                    {
                        await AccountSessionRevocation.RevokeFamilyAsync(db, change == "family" ? familyId : browserId,
                            accountId, TenantDefaults.DefaultTenantId, DateTime.UtcNow);
                        await transaction.CommitAsync();
                    }
                    break;
                case "global":
                    await using (var transaction = await db.Database.BeginTransactionAsync())
                    {
                        await db.UserAccounts.ExecuteUpdateAsync(s => s.SetProperty(row => row.SessionVersion, 1L));
                        await AccountSessionRevocation.RevokeAllFamiliesAsync(db, accountId, TenantDefaults.DefaultTenantId, DateTime.UtcNow);
                        await transaction.CommitAsync();
                    }
                    break;
                case "epoch": await db.UserAccounts.ExecuteUpdateAsync(s => s.SetProperty(row => row.SessionVersion, 1L)); break;
                case "provider": await db.OidcProviderSessions.ExecuteUpdateAsync(s => s.SetProperty(row => row.RevokedAtUtc, DateTime.UtcNow).SetProperty(row => row.ReissueBlockedAtUtc, DateTime.UtcNow)); break;
                case "pending-deletion": await db.Tenants.ExecuteUpdateAsync(s => s.SetProperty(row => row.DeletionRequestedAtUtc, DateTime.UtcNow)); break;
                case "deleted-grant": await db.McpGrants.ExecuteDeleteAsync(); break;
                case "database-outage": await db.Database.ExecuteSqlRawAsync("DROP TABLE \"OpenIddictTokens\""); break;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Client?.Dispose();
            if (postgresConnection is not null && app is not null)
            {
                await using var scope = app.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<CoreContext>().Database.EnsureDeletedAsync();
            }
            if (app is not null) await app.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
