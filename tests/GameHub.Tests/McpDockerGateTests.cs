using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using GameHub.Core.Data;
using GameHub.Core.Models;
using GameHub.Core.Services;
using GameHub.Shared.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace GameHub.Tests;

public sealed partial class McpDeviceStartupTests
{
    /// <summary>
    /// Opt-in fixture controlled by smoke-mcp-auth.py. Only the test assembly has this control
    /// channel; no fixture route, identity, certificate or bypass enters a production image.
    /// </summary>
    [Fact]
    public async Task DockerGate_ProductionCore_TwoOwnersAndPersistentProtocol()
    {
        var channel = Environment.GetEnvironmentVariable("GAMEHUB_MCP_DOCKER_GATE");
        if (string.IsNullOrEmpty(channel)) return;
        var postgres = Environment.GetEnvironmentVariable("GAMEHUB_MCP_DOCKER_POSTGRES") == "true";
        Assert.True(!postgres || !string.IsNullOrWhiteSpace(Postgres), "PostgreSQL gate requires its test connection.");
        await using var fixture = new StartupFixture(postgres);
        fixture.Values["MultiUser:Enabled"] = "true";
        fixture.Values["Authentication:Frontend:MultiUserEnabled"] = "true";
        fixture.Values["Authentication:Oidc:ProvisioningMode"] = "PersonalTenantJit";
        fixture.Values["Services:IngestServiceKey"] = "docker-gate-ingest-fixture-key-0123456789";

        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=GameHub gate CA", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var serverKey = RSA.Create(2048);
        var serverRequest = new CertificateRequest("CN=127.0.0.1", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        serverRequest.CertificateExtensions.Add(san.Build());
        serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var publicServer = serverRequest.Create(root, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1), RandomNumberGenerator.GetBytes(16));
        using var server = publicServer.CopyWithPrivateKey(serverKey);
        await File.WriteAllTextAsync(Path.Combine(channel, "ca.pem"), root.ExportCertificatePem());

        // A real TLS listener transports the image's requests into the actual Core entry point.
        // Consent stays on the private test channel, analogous to the separate trusted BFF.
        HttpClient upstream = null!;
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(server)));
        await using var edge = builder.Build();
        edge.Run(async context =>
        {
            var path = context.Request.Path.Value!;
            if (path is not ("/connect/device" or "/connect/token" or "/connect/revoke") && !path.StartsWith("/api/library", StringComparison.Ordinal))
            { context.Response.StatusCode = 404; return; }
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), path + context.Request.QueryString);
            // HttpClient streams JSON updates with chunked encoding and no Content-Length.
            if (HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPatch(context.Request.Method))
                request.Content = new StreamContent(context.Request.Body);
            foreach (var header in context.Request.Headers)
            {
                if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)
                    || header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                    || header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
                if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                    request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
            using var response = await upstream.SendAsync(request, context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Headers.Concat(response.Content.Headers))
                if (!header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                    context.Response.Headers[header.Key] = header.Value.ToArray();
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        });
        await edge.StartAsync();
        var issuer = edge.Urls.Single().TrimEnd('/') + "/";
        fixture.Values["Mcp:Device:Issuer"] = issuer;
        fixture.Values["Mcp:Device:Resource"] = issuer + "api";
        fixture.Values["Mcp:Device:PublicOrigin"] = issuer.TrimEnd('/');
        WebApplicationFactory<CoreContext> Open() => fixture.Factory().WithWebHostBuilder(web => web.ConfigureServices(services =>
            services.AddScoped<IMultiUserIngestReadinessProbe>(_ => new TestMultiUserIngestReadinessProbe(true))));
        var factory = Open();
        HttpClient Connect() => factory.CreateClient(new() { BaseAddress = new Uri(issuer), AllowAutoRedirect = false });
        upstream = Connect();
        try
        {
            Assert.True(factory.Services.GetRequiredService<MultiUserMode>().IsEnabled);
            var owners = new[] { await SeedAsync(factory), await SeedAsync(factory, Guid.NewGuid()) };
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
                for (var index = 0; index < owners.Length; index++)
                {
                    var tenant = await db.UserAccounts.Where(row => row.Id == owners[index].Account).Select(row => row.TenantId).SingleAsync();
                    // Seed through a tenant-scoped context; the production ownership guard stays on.
                    await using var owned = new CoreContext(scope.ServiceProvider.GetRequiredService<DbContextOptions<CoreContext>>(), new GateTenant(tenant));
                    owned.UnifiedGames.Add(new UnifiedGame { Id = index + 101, TenantId = tenant,
                        Title = "Gate owner " + index, Slug = "gate-game" });
                    await owned.SaveChangesAsync();
                }
            }
            await ReplyAsync("ready", new { issuer });
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
            for (var sequence = 1; ; sequence++)
            {
                var input = Path.Combine(channel, sequence + ".request");
                while (!File.Exists(input)) await Task.Delay(50, deadline.Token);
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(input, deadline.Token));
                File.Delete(input);
                var command = document.RootElement;
                var action = command.GetProperty("action").GetString();
                if (action == "stop") { await ReplyAsync(sequence.ToString(), new { ok = true }); break; }
                if (action == "restart")
                {
                    upstream.Dispose(); factory.Dispose(); factory = Open(); upstream = Connect();
                }
                else if (action == "approve")
                {
                    var owner = owners[command.GetProperty("owner").GetInt32()];
                    var code = command.GetProperty("code").GetString()!;
                    using var preview = await VerifyAsync(upstream, owner.Token, code, "preview");
                    Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
                    using var display = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
                    using var request = new HttpRequestMessage(HttpMethod.Post, "/connect/verify")
                    {
                        Content = Form(("user_code", code), ("action", "approve"),
                            ("confirmation", display.RootElement.GetProperty("confirmation").GetString()!),
                            ("allow_tracking_write", command.GetProperty("write").GetBoolean() ? "true" : "false"))
                    };
                    request.Headers.Add(McpDeviceSettings.BffHeader, StartupFixture.McpKey);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", owner.Token);
                    using var approved = await upstream.SendAsync(request);
                    Assert.Equal(HttpStatusCode.NoContent, approved.StatusCode);
                }
                else if (action == "revoke")
                {
                    var owner = owners[command.GetProperty("owner").GetInt32()];
                    using var list = await BearerGetAsync(upstream, "/api/auth/mcp-connections", owner.Token);
                    Assert.Equal(HttpStatusCode.OK, list.StatusCode);
                    using var rows = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
                    var id = rows.RootElement[0].GetProperty("id").GetString();
                    using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/auth/mcp-connections/" + id);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", owner.Token);
                    using var revoked = await upstream.SendAsync(request);
                    Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
                }
                else if (action == "assert-data")
                {
                    using var scope = factory.Services.CreateScope();
                    var games = await scope.ServiceProvider.GetRequiredService<CoreContext>().UnifiedGames.IgnoreQueryFilters().OrderBy(row => row.Id).ToArrayAsync();
                    Assert.Null(games[0].UserRating);
                    Assert.Equal(4, games[1].UserRating);
                    Assert.Equal(PlayStatus.Completed, games[1].PlayStatus);
                }
                else if (action == "assert-revoked")
                {
                    using var scope = factory.Services.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
                    var grants = await db.McpGrants.ToArrayAsync();
                    Assert.Equal(2, grants.Length);
                    Assert.All(grants, grant => Assert.NotNull(grant.RevokedAtUtc));
                    var families = grants.Select(grant => grant.AccountSessionId).ToArray();
                    Assert.All(await db.AccountSessions.Where(row => families.Contains(row.Id)).ToArrayAsync(),
                        family => Assert.NotNull(family.RevokedAtUtc));
                }
                else throw new InvalidOperationException("Unknown Docker gate command.");
                await ReplyAsync(sequence.ToString(), new { ok = true });
            }
        }
        finally { upstream.Dispose(); factory.Dispose(); await edge.StopAsync(); }

        async Task ReplyAsync(string name, object body)
        {
            var target = Path.Combine(channel, name + ".response");
            await File.WriteAllTextAsync(target + ".tmp", JsonSerializer.Serialize(body));
            File.Move(target + ".tmp", target);
        }
    }

    private sealed record GateTenant(Guid TenantId) : ITenantContext;
}
