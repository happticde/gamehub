using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using GameHub.Core.Data;
using GameHub.Core.Mcp;
using GameHub.Core.Services;
using GameHub.Shared.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenIddict.Abstractions;
using Xunit;

namespace GameHub.Tests;

/// <summary>Exercises the SDK transport with the managed gateway and real tenant-filtered library operations.</summary>
public sealed class PrivateMcpTransportTests
{
    private const string Origin = "https://games.example";
    private const string TransportKey = "transport-fixture-0123456789-0123456789";
    private const string Generation = "generation-fixture";
    private const string FixtureScheme = "PrivateMcpTest";
    private static readonly Guid OtherTenant = Guid.Parse("c21570b1-2c52-48f7-a9f0-84c5f79c5743");

    [Fact]
    public async Task Transport_WithoutRemoteAuthorization_RejectsEveryMethodEvenWithGatewayProof()
    {
        await using var database = new FixtureDatabase();
        await using var app = await StartAsync(database);
        using var http = app.GetTestClient();
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Get, HttpMethod.Delete })
        {
            using var direct = new HttpRequestMessage(method, "/mcp");
            direct.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "owner-one-fixture");
            using var missingProof = await http.SendAsync(direct);
            Assert.Equal(HttpStatusCode.NotFound, missingProof.StatusCode);

            using var gateway = new HttpRequestMessage(method, "/mcp");
            AddGatewayProof(gateway.Headers);
            gateway.Headers.Add("X-Api-Key", "operator-fixture");
            gateway.Headers.Add("Cookie", "gamehub_access_token=browser-fixture");
            gateway.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "rest-token-fixture");
            using var denied = await http.SendAsync(gateway);
            Assert.Equal(method == HttpMethod.Post ? HttpStatusCode.Unauthorized : HttpStatusCode.MethodNotAllowed,
                denied.StatusCode);
            if (method == HttpMethod.Post)
                Assert.Equal($"Bearer resource_metadata=\"{Origin}/.well-known/oauth-protected-resource/mcp\", scope=\"library:read\", error=\"invalid_token\"",
                    Assert.Single(denied.Headers.WwwAuthenticate).ToString());
            Assert.Equal("no-store", denied.Headers.CacheControl?.ToString());
        }

        foreach (var (path, origin, expected) in new[]
        {
            ("/mcp/", Origin, HttpStatusCode.NotFound),
            ("/mcp/sse", Origin, HttpStatusCode.NotFound),
            ("/mcp?ignored=true", Origin, HttpStatusCode.BadRequest),
            ("/mcp", "https://other.example", HttpStatusCode.Forbidden)
        })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path);
            AddGatewayProof(request.Headers);
            request.Headers.Add("Origin", origin);
            using var response = await http.SendAsync(request);
            Assert.Equal(expected, response.StatusCode);
        }
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized, "")]
    [InlineData("Basic unsupported", HttpStatusCode.Unauthorized, "")]
    [InlineData("Bearer invalid", HttpStatusCode.Unauthorized, ", error=\"invalid_token\"")]
    [InlineData("Bearer missing-read-scope", HttpStatusCode.Forbidden, ", error=\"insufficient_scope\"")]
    public async Task Transport_BearerChallenge_UsesCanonicalMetadataAndMinimumScope(string? authorization,
        HttpStatusCode expected, string error)
    {
        await using var database = new FixtureDatabase();
        await using var app = await StartAsync(database, authorizeFixture: true);
        using var http = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        AddGatewayProof(request.Headers);
        request.Headers.Host = "internal.example:8080";
        request.Headers.Add("X-Forwarded-Host", "untrusted.example");
        if (authorization is not null) request.Headers.Add("Authorization", authorization);
        using var response = await http.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal($"Bearer resource_metadata=\"{Origin}/.well-known/oauth-protected-resource/mcp\", scope=\"library:read\"{error}",
            Assert.Single(response.Headers.WwwAuthenticate).ToString());
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transport_CurrentAndLegacyClients_IsolateRequestsAndReconnectAfterRestart(bool postgres)
    {
        if (postgres && PostgresConnection is null) return;
        await using var database = new FixtureDatabase(postgres: postgres);
        await database.SeedAsync();
        await using (var app = await StartAsync(database, authorizeFixture: true))
        {
            using var firstHttp = Connect(app, "owner-one-fixture");
            using var secondHttp = Connect(app, "owner-two-fixture");
            await using var first = await CreateClientAsync(firstHttp, "2025-11-25");
            await using var second = await CreateClientAsync(secondHttp, "2026-07-28");
            Assert.Equal("2025-11-25", first.NegotiatedProtocolVersion);
            Assert.Equal("2026-07-28", second.NegotiatedProtocolVersion);

            var listed = await first.ListToolsAsync();
            Assert.Equal(new[] { "get_game", "get_random_game", "get_statistics", "list_tags", "search_library" },
                listed.Select(tool => tool.Name).Order(StringComparer.Ordinal));
            var reads = await Task.WhenAll(
                ReadTextAsync(first, "search_library"), ReadTextAsync(second, "search_library"),
                ReadTextAsync(first, "get_game", new Dictionary<string, object?> { ["gameId"] = 101 }),
                ReadTextAsync(second, "get_random_game"), ReadTextAsync(first, "get_statistics"),
                ReadTextAsync(second, "list_tags"));
            Assert.Contains("First game", reads[0]);
            Assert.DoesNotContain("Second game", reads[0]);
            Assert.Contains("Second game", reads[1]);
            Assert.DoesNotContain("First game", reads[1]);
            Assert.Contains("First game", reads[2]);
            Assert.Contains("202", reads[3]);
            Assert.Contains("\"totalGames\":1", reads[4]);
            Assert.Contains("Second tag", reads[5]);
            Assert.DoesNotContain("First tag", reads[5]);

            using var initialize = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Content = new StringContent("""
                    {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"fixture","version":"1"}}}
                    """, Encoding.UTF8, "application/json")
            };
            initialize.Headers.Accept.ParseAdd("application/json");
            initialize.Headers.Accept.ParseAdd("text/event-stream");
            using var initialized = await firstHttp.SendAsync(initialize);
            Assert.Equal(HttpStatusCode.OK, initialized.StatusCode);
            Assert.False(initialized.Headers.Contains("Mcp-Session-Id"));

            using var invalid = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Content = new StringContent("{", Encoding.UTF8, "application/json")
            };
            invalid.Headers.Accept.ParseAdd("application/json");
            invalid.Headers.Accept.ParseAdd("text/event-stream");
            using var malformed = await firstHttp.SendAsync(invalid);
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);

            foreach (var method in new[] { HttpMethod.Get, HttpMethod.Delete })
            {
                using var request = new HttpRequestMessage(method, "/mcp");
                using var response = await firstHttp.SendAsync(request);
                Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
            }
        }

        await using var restarted = await StartAsync(database, authorizeFixture: true);
        using var restartedHttp = Connect(restarted, "owner-two-fixture");
        await using var reconnected = await CreateClientAsync(restartedHttp, "2026-07-28");
        Assert.Contains("Second game", await ReadTextAsync(reconnected, "search_library"));
    }

    [Fact]
    public async Task Transport_RequestCancellation_CancelsDatabaseWorkAndLeavesNextRequestUsable()
    {
        var query = new BlockNextQuery();
        await using var database = new FixtureDatabase(query);
        await database.SeedAsync();
        await using var app = await StartAsync(database, authorizeFixture: true);
        using var http = Connect(app, "owner-one-fixture");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search_library","arguments":{}}}""",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add("Mcp-Protocol-Version", "2025-11-25");
        using var stop = new CancellationTokenSource();
        query.Arm();
        var call = http.SendAsync(request, stop.Token);
        await query.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await call);
        await query.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await using var next = await CreateClientAsync(http, "2026-07-28");
        Assert.Contains("First game", await ReadTextAsync(next, "search_library"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tracking_SeparateUpdates_KeepTheOtherFieldAndStayWithinTheTenant(bool postgres)
    {
        if (postgres && PostgresConnection is null) return;
        await using var database = new FixtureDatabase(postgres: postgres);
        await database.SeedAsync();
        await using var owned = database.Open(TenantDefaults.DefaultTenantId);
        var library = new LibraryOperations(owned, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        Assert.Null(await library.UpdatePlayStatusAsync(202, PlayStatus.Completed, default));
        Assert.Null(await library.UpdateRatingAsync(202, 5, default));
        Assert.Equal(PlayStatus.Completed, (await library.UpdatePlayStatusAsync(101, PlayStatus.Completed, default))?.PlayStatus);
        var rated = await library.UpdateRatingAsync(101, 5, default);
        Assert.Equal((PlayStatus.Completed, (int?)5), (rated!.PlayStatus, rated.UserRating));
        var cleared = await library.UpdateRatingAsync(101, null, default);
        Assert.Equal((PlayStatus.Completed, (int?)null), (cleared!.PlayStatus, cleared.UserRating));
    }

    private static async Task<WebApplication> StartAsync(FixtureDatabase database, bool authorizeFixture = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<TenantExecutionContext>();
        builder.Services.AddScoped<ITenantContext, AuthenticatedTenantContext>();
        builder.Services.AddSingleton(database.Options);
        builder.Services.AddScoped<CoreContext>();
        builder.Services.AddScoped(services => new LibraryOperations(services.GetRequiredService<CoreContext>(),
            services.GetRequiredService<ILogger<LibraryOperations>>()));
        builder.Services.AddControllers().AddJsonOptions(options =>
            options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
        builder.Services.AddSingleton(new McpManagedDeployment("unused", Generation, Origin, TransportKey));
        builder.Services.AddSingleton(McpRemoteResource.Create(new McpRestResource(new Uri(Origin + "/"), new Uri(Origin + "/api"))));
        builder.Services.AddPrivateMcpTransport();
        if (authorizeFixture)
        {
            builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, FixtureAuthentication>(FixtureScheme, null);
            builder.Services.Configure<AuthenticationSchemeOptions>(PrivateMcpTransport.Scheme,
                options => options.ForwardAuthenticate = FixtureScheme);
        }
        var app = builder.Build();
        app.UseMiddleware<McpManagedGatewayMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapPrivateMcpTransport();
        await app.StartAsync();
        return app;
    }

    private static HttpClient Connect(WebApplication app, string token)
    {
        var http = app.GetTestClient();
        AddGatewayProof(http.DefaultRequestHeaders);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    private static void AddGatewayProof(HttpRequestHeaders headers)
    {
        headers.Add(McpManagedDeployment.TransportHeader, TransportKey);
        headers.Add(McpManagedDeployment.GenerationHeader, Generation);
    }

    private static Task<McpClient> CreateClientAsync(HttpClient http, string revision) =>
        McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp
        }, http), new McpClientOptions { ProtocolVersion = revision });

    private static async Task<string> ReadTextAsync(McpClient client, string name,
        IReadOnlyDictionary<string, object?>? arguments = null)
    {
        var result = await client.CallToolAsync(name, arguments);
        Assert.NotEqual(true, result.IsError);
        return Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
    }

    private sealed class FixtureAuthentication(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var tenant = Request.Headers.Authorization.ToString() switch
            {
                "Bearer owner-one-fixture" or "Bearer missing-read-scope" => TenantDefaults.DefaultTenantId,
                "Bearer owner-two-fixture" => OtherTenant,
                _ => Guid.Empty
            };
            if (tenant == Guid.Empty) return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(GameHubClaimTypes.TenantId, tenant.ToString())], PrivateMcpTransport.Scheme));
            principal.SetScopes(Request.Headers.Authorization == "Bearer missing-read-scope"
                ? McpPermissions.TrackingWrite : McpPermissions.LibraryRead);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }

    private sealed class FixtureDatabase : IAsyncDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"gamehub-remote-{Guid.NewGuid():N}.db");
        private readonly bool _postgres;
        internal DbContextOptions<CoreContext> Options { get; }

        internal FixtureDatabase(DbCommandInterceptor? interceptor = null, bool postgres = false)
        {
            _postgres = postgres;
            var options = new DbContextOptionsBuilder<CoreContext>();
            if (postgres)
                options.UseNpgsql(new Npgsql.NpgsqlConnectionStringBuilder(PostgresConnection)
                {
                    Database = $"mcp_remote_{Guid.NewGuid():N}", Pooling = false
                }.ConnectionString);
            else
                options.UseSqlite($"Data Source={_path};Pooling=False");
            if (interceptor is not null) options.AddInterceptors(interceptor);
            Options = options.Options;
        }

        internal CoreContext Open(Guid tenant) => new(Options, new FixtureTenant(tenant));

        internal async Task SeedAsync()
        {
            await using var first = Open(TenantDefaults.DefaultTenantId);
            await first.Database.EnsureCreatedAsync();
            first.Tenants.Add(new Tenant { Id = OtherTenant, Name = "Second owner" });
            first.UnifiedGames.Add(new UnifiedGame { Id = 101, Title = "First game", Slug = "game" });
            first.Tags.Add(new Tag { Name = "First tag" });
            await first.SaveChangesAsync();
            await using var second = Open(OtherTenant);
            second.UnifiedGames.Add(new UnifiedGame { Id = 202, Title = "Second game", Slug = "game", TenantId = OtherTenant });
            second.Tags.Add(new Tag { Name = "Second tag", TenantId = OtherTenant });
            await second.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (_postgres)
            {
                await using var database = Open(TenantDefaults.DefaultTenantId);
                await database.Database.EnsureDeletedAsync();
            }
            else File.Delete(_path);
        }
    }

    private static string? PostgresConnection => Environment.GetEnvironmentVariable("GAMEHUB_TEST_POSTGRES_CONNECTION");

    private sealed record FixtureTenant(Guid TenantId) : ITenantContext;

    private sealed class BlockNextQuery : DbCommandInterceptor
    {
        private int _armed;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Arm() => Interlocked.Exchange(ref _armed, 1);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _armed, 0) != 1) return result;
            Entered.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return result;
            }
            catch (OperationCanceledException)
            {
                Cancelled.SetResult();
                throw;
            }
        }
    }
}
