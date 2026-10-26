using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameHub.Ingest.Controllers;
using GameHub.Ingest.Data;
using GameHub.Ingest.Models;
using GameHub.Ingest.Services;
using GameHub.Shared;
using GameHub.Shared.Authentication;
using GameHub.Tests.Helpers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace GameHub.Tests;

/// <summary>Runs MVC authorization with the real service-key and API-key handlers, not a fake principal.</summary>
public sealed class PlatformCredentialAuthorizationTests
{
    [Theory]
    [InlineData("no-headers")]
    [InlineData("tenant-only")]
    [InlineData("wrong-service-key")]
    [InlineData("missing-tenant")]
    [InlineData("invalid-tenant")]
    [InlineData("empty-tenant")]
    [InlineData("duplicate-service-key")]
    [InlineData("duplicate-tenant")]
    [InlineData("api-key-only")]
    [InlineData("api-key-with-spoofed-tenant")]
    public async Task Upsert_InvalidDelegation_RejectsEveryPreparationPathWithoutChanges(string authentication)
    {
        using var fixture = new CredentialHost();
        await fixture.SeedAsync();
        var before = await fixture.SnapshotAsync();

        foreach (var kind in new[] { "steam", "epic-code", "epic-code-with-refresh", "epic-refresh" })
        {
            using var request = fixture.UpsertRequest(kind, CredentialHost.TenantB);
            SetAuthentication(request, authentication);
            using var response = await fixture.Client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Empty(fixture.Epic.AuthorizationCodes);
            Assert.Equal(before, await fixture.SnapshotAsync());
        }
        fixture.AssertNoSecretOutput();
    }

    [Theory]
    [InlineData("steam")]
    [InlineData("gog")]
    [InlineData("psn")]
    [InlineData("epic-code")]
    [InlineData("epic-code-with-refresh")]
    [InlineData("epic-refresh")]
    public async Task Upsert_ValidDelegation_UsesClaimTenantAndPreservesOtherTenant(string kind)
    {
        using var fixture = new CredentialHost();
        await fixture.SeedAsync();
        var otherBefore = await fixture.SnapshotAsync(CredentialHost.TenantB);
        using var request = fixture.UpsertRequest(kind, CredentialHost.TenantB);
        SetAuthentication(request, "valid");

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("submitted-", body, StringComparison.Ordinal);
        Assert.DoesNotContain("exchanged-", body, StringComparison.Ordinal);
        Assert.DoesNotContain("tenantId", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(otherBefore, await fixture.SnapshotAsync(CredentialHost.TenantB));
        Assert.Equal(kind.StartsWith("epic-code", StringComparison.Ordinal) ? 1 : 0, fixture.Epic.AuthorizationCodes.Count);

        await using var db = fixture.CreateContext(CredentialHost.TenantA);
        var stored = await db.PlatformCredentials.SingleAsync(row => row.Platform == PlatformFor(kind));
        var plaintext = fixture.Protector.Unprotect(stored);
        try
        {
            var decoded = System.Text.Encoding.UTF8.GetString(plaintext);
            Assert.Contains(kind.StartsWith("epic-code", StringComparison.Ordinal) ? "exchanged-refresh" : "submitted-secret", decoded, StringComparison.Ordinal);
            Assert.DoesNotContain("submitted-code", decoded, StringComparison.Ordinal);
            Assert.Equal(CredentialHost.TenantA, stored.TenantId);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
        }
        fixture.AssertNoSecretOutput();
    }

    [Theory]
    [InlineData("rejected", false)]
    [InlineData("rejected", true)]
    [InlineData("invalid-result", false)]
    [InlineData("invalid-result", true)]
    public async Task Upsert_EpicExchangeFailure_DoesNotReplaceEitherTenantOrExposeSecrets(string failure, bool existing)
    {
        using var fixture = new CredentialHost();
        if (existing)
            await fixture.SeedAsync();
        else
        {
            await using var db = fixture.CreateContext(CredentialHost.TenantA);
            await db.Database.EnsureCreatedAsync();
        }
        var before = await fixture.SnapshotAsync();
        fixture.Epic.Failure = failure;
        using var request = fixture.UpsertRequest("epic-code-with-refresh", CredentialHost.TenantB);
        SetAuthentication(request, "valid");

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("submitted-", body, StringComparison.Ordinal);
        Assert.DoesNotContain("exchanged-", body, StringComparison.Ordinal);
        Assert.Single(fixture.Epic.AuthorizationCodes);
        Assert.Equal(before, await fixture.SnapshotAsync());
        fixture.AssertNoSecretOutput();
    }

    private static Platform PlatformFor(string kind) => kind switch
    {
        "steam" => Platform.Steam,
        "gog" => Platform.Gog,
        "psn" => Platform.Psn,
        _ => Platform.Epic
    };

    private static JsonElement Payload(string kind) => kind switch
    {
        "steam" => JsonSerializer.SerializeToElement(new { apiKey = "submitted-secret", steamId = "123" }),
        "gog" => JsonSerializer.SerializeToElement(new { cookie = "submitted-secret", user = "player" }),
        "psn" => JsonSerializer.SerializeToElement(new { cookies = new { session = "submitted-secret" } }),
        "epic-code" => JsonSerializer.SerializeToElement(new { authorizationCode = "submitted-code" }),
        "epic-code-with-refresh" => JsonSerializer.SerializeToElement(new { authorizationCode = "submitted-code", refreshToken = "submitted-secret" }),
        _ => JsonSerializer.SerializeToElement(new { refreshToken = "submitted-secret" })
    };

    private static void SetAuthentication(HttpRequestMessage request, string mode)
    {
        if (mode is "valid" or "missing-tenant" or "invalid-tenant" or "empty-tenant" or "duplicate-tenant")
            request.Headers.Add(TenantDelegationAuthenticationHandler.ServiceKeyHeaderName, CredentialHost.ServiceKey);
        else if (mode == "wrong-service-key")
            request.Headers.Add(TenantDelegationAuthenticationHandler.ServiceKeyHeaderName, "incorrect-service-key");
        else if (mode == "duplicate-service-key")
            request.Headers.Add(TenantDelegationAuthenticationHandler.ServiceKeyHeaderName, new[] { CredentialHost.ServiceKey, "incorrect-service-key" });

        if (mode is not "no-headers" and not "missing-tenant" and not "api-key-only")
        {
            var tenant = mode switch
            {
                "invalid-tenant" => "not-a-guid",
                "empty-tenant" => Guid.Empty.ToString(),
                "valid" => CredentialHost.TenantA.ToString(),
                _ => CredentialHost.TenantB.ToString()
            };
            request.Headers.Add(TenantDelegationAuthenticationHandler.TenantHeaderName, mode == "duplicate-tenant"
                ? new[] { tenant, CredentialHost.TenantA.ToString() }
                : new[] { tenant });
        }
        if (mode is "api-key-only" or "api-key-with-spoofed-tenant")
            request.Headers.Add(ApiKeyAuthenticationHandler.HeaderName, CredentialHost.ApiKey);
    }

    private sealed class CredentialHost : IDisposable
    {
        public static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        public static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        public const string ServiceKey = "internal-service-key-for-http-regressions";
        public const string ApiKey = "compatibility-api-key-for-http-regressions";
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly DbContextOptions<IngestContext> _options;
        private readonly IHost _host;
        private readonly RecordingLoggerProvider _logs = new();
        public HttpClient Client { get; }
        public EpicExchange Epic { get; } = new();
        public PlatformCredentialProtector Protector { get; }

        public CredentialHost()
        {
            _connection.Open();
            _options = new DbContextOptionsBuilder<IngestContext>().UseSqlite(_connection).Options;
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:ApiKey"] = ApiKey,
                ["Authentication:ServiceKey"] = ServiceKey,
                ["Credentials:ActiveKeyVersion"] = "1",
                ["Credentials:Keys:1"] = Convert.ToBase64String(Enumerable.Repeat((byte)0xA5, 32).ToArray())
            }).Build();
            Protector = new PlatformCredentialProtector(PlatformCredentialKeyRing.FromConfiguration(configuration));
            _host = new HostBuilder()
                .UseEnvironment(Environments.Production)
                .ConfigureAppConfiguration((_, builder) => builder.AddConfiguration(configuration))
                .ConfigureWebHost(web => web.UseTestServer()
                .ConfigureServices((context, services) =>
                {
                    services.AddLogging(builder => builder.ClearProviders().AddProvider(_logs));
                    services.AddApiKeyAuthentication(context.Configuration, context.HostingEnvironment);
                    services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TenantDelegationAuthenticationHandler>(
                        TenantDelegationAuthenticationHandler.SchemeName, null);
                    services.AddHttpContextAccessor();
                    services.AddScoped<IIngestTenantContext, AuthenticatedIngestTenantContext>();
                    services.AddDbContext<IngestContext>(options => options.UseSqlite(_connection));
                    services.AddSingleton(Protector);
                    services.AddSingleton<TimeProvider>(TimeProvider.System);
                    services.AddSingleton<IEpicAuthenticationClient>(Epic);
                    services.AddScoped<PlatformCredentialService>();
                    services.AddControllers().AddApplicationPart(typeof(PlatformCredentialsController).Assembly);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                })).Build();
            _host.Start();
            Client = _host.GetTestClient();
        }

        public IngestContext CreateContext(Guid tenant) => new(_options, new FixedTenant(tenant));

        public async Task SeedAsync()
        {
            foreach (var tenant in new[] { TenantA, TenantB })
            {
                await using var db = CreateContext(tenant);
                await db.Database.EnsureCreatedAsync();
                var service = new PlatformCredentialService(db, new FixedTenant(tenant), Protector, TimeProvider.System);
                foreach (var kind in new[] { "steam", "gog", "psn", "epic-refresh" })
                    await service.UpsertAsync(PlatformFor(kind), 1, Payload(kind));
            }
        }

        public HttpRequestMessage UpsertRequest(string kind, Guid bodyTenant) => new(HttpMethod.Put,
            $"/api/internal/platform-credentials/{PlatformFor(kind)}")
        {
            Content = JsonContent.Create(new { schemaVersion = 1, credentials = Payload(kind), tenantId = bodyTenant })
        };

        public async Task<string> SnapshotAsync(Guid? tenant = null)
        {
            await using var db = CreateContext(tenant ?? TenantA);
            var query = tenant.HasValue ? db.PlatformCredentials.AsNoTracking() : db.PlatformCredentials.IgnoreQueryFilters().AsNoTracking();
            return JsonSerializer.Serialize(await query.OrderBy(row => row.Id).ToListAsync());
        }

        public void AssertNoSecretOutput() => Assert.All(_logs.Records, entry =>
        {
            Assert.Null(entry.Exception);
            Assert.All(new[] { entry.Message }.Concat(entry.Properties.Select(property => property.Value?.ToString() ?? "")), value =>
            {
                Assert.DoesNotContain("submitted-", value, StringComparison.Ordinal);
                Assert.DoesNotContain("exchanged-", value, StringComparison.Ordinal);
            });
        });

        public void Dispose()
        {
            Client.Dispose();
            _host.Dispose();
            _connection.Dispose();
        }
    }

    private sealed record FixedTenant(Guid TenantId) : IIngestTenantContext;

    private sealed class EpicExchange : IEpicAuthenticationClient
    {
        public List<string> AuthorizationCodes { get; } = [];
        public string? Failure { get; set; }

        public Task<EpicPlatformCredential> ExchangeAuthorizationCodeAsync(string authorizationCode, CancellationToken cancellationToken = default)
        {
            AuthorizationCodes.Add(authorizationCode);
            if (Failure == "rejected")
                throw new PlatformCredentialValidationException("Epic authorization failed.", new Exception("submitted-code exchanged-refresh"));
            return Task.FromResult(new EpicPlatformCredential(Failure == "invalid-result" ? "" : "exchanged-refresh"));
        }
    }
}
