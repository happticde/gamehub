using System.Net;
using System.Security.Claims;
using System.Text.Json;
using GameHub.Ingest.Controllers;
using GameHub.Ingest.Data;
using GameHub.Ingest.Services;
using GameHub.Shared;
using GameHub.Shared.Authentication;
using GameHub.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace GameHub.Tests;

/// <summary>Exercises real fetch, ingest, audit, and export paths under two authenticated tenants.</summary>
public sealed class PlatformRetrievalSecurityTests
{
    [Theory]
    [InlineData("steam", "success", 200, null, "Success", null)]
    [InlineData("gog", "success", 200, null, "Success", null)]
    [InlineData("steam", "unexpected", 500, "Internal retrieval error.", "Error", "Steam retrieval failed.")]
    [InlineData("gog", "unexpected", 500, "Internal retrieval error.", "Error", "GOG retrieval failed.")]
    [InlineData("steam", "json", 502, "Steam API returned unexpected data.", "Error", "Steam retrieval failed.")]
    [InlineData("gog", "json", 500, "Internal retrieval error.", "MalformedResponse", "GOG provider returned an unexpected response.")]
    [InlineData("steam", "network", 502, "Failed to fetch from Steam API.", null, null)]
    [InlineData("gog", "network", 502, "Failed to fetch from GOG API.", null, null)]
    [InlineData("steam", "forbidden", 502, "Failed to fetch from Steam API.", "TokenExpired", "API Key is invalid or expired.")]
    [InlineData("gog", "forbidden", 502, "Failed to fetch from GOG API.", "TokenExpired", "gog-al cookie expired or invalid.")]
    [InlineData("steam", "rate-limit", 502, "Failed to fetch from Steam API.", "RateLimited", "Provider rate limit exceeded.")]
    [InlineData("gog", "rate-limit", 502, "Failed to fetch from GOG API.", "RateLimited", "Provider rate limit exceeded.")]
    [InlineData("steam", "upstream", 502, "Failed to fetch from Steam API.", "UpstreamUnavailable", "Provider is temporarily unavailable.")]
    [InlineData("gog", "upstream", 502, "Failed to fetch from GOG API.", "UpstreamUnavailable", "Provider is temporarily unavailable.")]
    public async Task Retrieve_ProviderSuccessAndFailures_KeepLogsSecretFreeAndDataTenantScoped(
        string platform,
        string scenario,
        int expectedStatus,
        string? expectedMessage,
        string? expectedAuditStatus,
        string? expectedAuditMessage)
    {
        Guid[] tenants =
        [
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")
        ];
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var accessor = new HttpContextAccessor();
        await using var services = new ServiceCollection()
            .AddSingleton<IHttpContextAccessor>(accessor)
            .AddScoped<IIngestTenantContext, AuthenticatedIngestTenantContext>()
            .AddDbContext<IngestContext>(options => options.UseSqlite(connection))
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var recording = new RecordingLoggerProvider();
        using var loggers = LoggerFactory.Create(builder =>
            builder.SetMinimumLevel(LogLevel.Trace).AddProvider(recording));

        foreach (var tenant in tenants)
        {
            accessor.HttpContext = CreateHttpContext(tenant, tenants.Single(value => value != tenant));
            await using var seedScope = services.CreateAsyncScope();
            var seed = seedScope.ServiceProvider.GetRequiredService<IngestContext>();
            await seed.Database.EnsureCreatedAsync();
            seed.IngestAuditLogs.Add(new IngestAuditLog
            {
                Platform = "Existing",
                Status = "Existing",
                ErrorMessage = $"Existing history for {tenant}"
            });
            await seed.SaveChangesAsync();
        }

        foreach (var tenant in tenants)
        {
            accessor.HttpContext = CreateHttpContext(tenant, tenants.Single(value => value != tenant));
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IngestContext>();
            var credential = $"provider-secret-{tenant}";
            var identifier = $"untrusted-user-{tenant}\r\nCRITICAL forged entry";
            var title = $"untrusted-game-{tenant}\r\nCRITICAL forged entry";
            var handler = new ProviderHandler(request =>
            {
                Assert.Equal(platform == "steam" ? "api.steampowered.com" : "www.gog.com", request.RequestUri?.Host);
                if (platform == "steam")
                {
                    Assert.Equal(credential, Assert.Single(request.Headers.GetValues("x-webapi-key")));
                    Assert.DoesNotContain(credential, request.RequestUri!.Query, StringComparison.Ordinal);
                }
                return CreateProviderResponse(platform, scenario, credential, title);
            });
            using var http = new HttpClient(handler);
            var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
            ISteamFetcher steam = platform == "steam"
                ? new SteamFetcher(http, loggers.CreateLogger<SteamFetcher>(), scopeFactory)
                : Mock.Of<ISteamFetcher>();
            IGogFetcher gog = platform == "gog"
                ? new GogFetcher(loggers.CreateLogger<GogFetcher>(), scopeFactory, cookie =>
                {
                    Assert.Equal(credential, cookie);
                    return handler;
                })
                : Mock.Of<IGogFetcher>();
            var controller = new RetrieveController(
                db, steam, gog, Mock.Of<IPsnFetcher>(), Mock.Of<IEpicFetcher>(), null!,
                loggers.CreateLogger<RetrieveController>(), loggers);

            var action = platform == "steam"
                ? await controller.RetrieveSteam(new SteamRetrieveRequest { ApiKey = credential, SteamId = identifier })
                : await controller.RetrieveGog(new GogRetrieveRequest { Cookie = credential, User = identifier });
            var result = Assert.IsAssignableFrom<ObjectResult>(action);
            Assert.Equal(expectedStatus, result.StatusCode);
            var body = JsonSerializer.SerializeToElement(result.Value);
            if (expectedMessage is not null)
            {
                Assert.Equal(expectedMessage, body.GetProperty("Message").GetString());
                Assert.Single(body.EnumerateObject());
            }
            else
            {
                Assert.Equal(1, body.GetProperty("Added").GetInt32());
                Assert.Equal(0, body.GetProperty("Updated").GetInt32());
                Assert.Equal(0, body.GetProperty("Unchanged").GetInt32());
                Assert.Equal(0, body.GetProperty("Skipped").GetInt32());
                var export = new ExportController(db, loggers.CreateLogger<ExportController>());
                var exported = Assert.IsType<OkObjectResult>(await export.GetByPlatform(platform.ToUpperInvariant()));
                var page = Assert.IsType<PagedResult<RawGameDto>>(exported.Value);
                Assert.Equal(title, Assert.Single(page.Items).Name);
                Assert.Equal(1, page.TotalCount);
            }

            var audits = await db.IngestAuditLogs.Where(row => row.Status != "Existing").ToListAsync();
            if (expectedAuditStatus is null)
            {
                Assert.Empty(audits);
            }
            else
            {
                var audit = Assert.Single(audits);
                Assert.Equal(tenant, audit.TenantId);
                Assert.Equal(expectedAuditStatus, audit.Status);
                Assert.Equal(expectedAuditMessage, audit.ErrorMessage);
            }
            var history = Assert.IsType<OkObjectResult>(await controller.GetIngestHistory(null));
            var rows = Assert.IsType<List<PlatformRetrievalAuditLogResponse>>(history.Value);
            Assert.Equal(1 + audits.Count, rows.Count);
            Assert.Contains(rows, row => row.ErrorMessage == $"Existing history for {tenant}");
            Assert.DoesNotContain(rows, row => row.ErrorMessage?.Contains(credential, StringComparison.Ordinal) == true);
        }

        await using var verifyScope = services.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<IngestContext>();
        var allAudits = await verify.IngestAuditLogs.IgnoreQueryFilters().ToListAsync();
        Assert.Equal(expectedAuditStatus is null ? 2 : 4, allAudits.Count);
        Assert.All(allAudits, audit => Assert.Contains(audit.TenantId, tenants));
        Assert.NotEmpty(recording.Records);
        Assert.All(recording.Records, entry =>
        {
            Assert.Null(entry.Exception);
            AssertSafeLogValue(entry.Message);
            Assert.All(entry.Properties, property => AssertSafeLogValue(property.Value?.ToString() ?? ""));
        });
    }

    private static void AssertSafeLogValue(string value)
    {
        Assert.DoesNotContain("untrusted-", value, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-secret-", value, StringComparison.Ordinal);
        Assert.DoesNotContain("nested-secret", value, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", value, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", value, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", value, StringComparison.Ordinal);
    }

    private static DefaultHttpContext CreateHttpContext(Guid tenant, Guid forgedTenant)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(GameHubClaimTypes.TenantId, tenant.ToString("D"))], "test"))
        };
        context.Request.Headers[TenantDelegationAuthenticationHandler.TenantHeaderName] = forgedTenant.ToString("D");
        return context;
    }

    private static HttpResponseMessage CreateProviderResponse(string platform, string scenario, string credential, string title)
    {
        if (scenario is "network" or "unexpected")
        {
            var message = $"{credential}\r\nCRITICAL forged entry";
            Exception error = scenario == "network"
                ? new HttpRequestException(message, new Exception("nested-secret"))
                : new InvalidOperationException(message, new Exception("nested-secret"));
            error.Data["credential"] = credential;
            throw error;
        }
        var status = scenario switch
        {
            "forbidden" => HttpStatusCode.Forbidden,
            "rate-limit" => HttpStatusCode.TooManyRequests,
            "upstream" => HttpStatusCode.ServiceUnavailable,
            _ => HttpStatusCode.OK
        };
        string payload;
        if (scenario == "success")
        {
            payload = platform == "steam"
                ? JsonSerializer.Serialize(new { response = new { games = new[] { new { appid = 42, name = title } } } })
                : JsonSerializer.Serialize(new { pages = 1, _embedded = new { items = new[] { new { game = new { id = 42, title } } } } });
        }
        else
        {
            payload = "{not-json:" + credential + "}";
        }
        return new HttpResponseMessage(status) { Content = new StringContent(payload) };
    }

    private sealed class ProviderHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }
}
