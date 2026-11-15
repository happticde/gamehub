using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using GameHub.Mcp;
using GameHub.Mcp.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace GameHub.Tests;

public sealed class McpRefreshProcessTests
{
    [Fact]
    public async Task Stdio_TwoProcessesShareExpiredCredential_OneHttpsRefreshAndCleanProtocol()
    {
        if (!OperatingSystem.IsLinux()) return;
        var directory = Path.Combine(Path.GetTempPath(), "mcp-process-refresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=GameHub process test root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var serverKey = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var publicServer = request.Create(root, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1), RandomNumberGenerator.GetBytes(16));
        using var server = publicServer.CopyWithPrivateKey(serverKey);
        var caPath = Path.Combine(directory, "test-ca.pem");
        await File.WriteAllTextAsync(caPath, root.ExportCertificatePem());
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(server)));
        await using var app = builder.Build();
        var renewals = 0;
        var reads = 0;
        app.MapPost("/connect/token", async (HttpContext context) =>
        {
            Assert.Null(context.Request.Headers.Authorization.FirstOrDefault());
            Assert.False(context.Request.Headers.ContainsKey("X-Api-Key"));
            var form = await context.Request.ReadFormAsync();
            Assert.Equal("process-refresh-before", form["refresh_token"].ToString());
            Assert.Equal("refresh_token", form["grant_type"].ToString());
            Assert.Equal(McpLoginSettings.ClientId, form["client_id"].ToString());
            Assert.Equal(context.Request.Scheme + "://" + context.Request.Host + "/api", form["resource"].ToString());
            Assert.Equal(1, Interlocked.Increment(ref renewals));
            await Task.Delay(200);
            return Results.Json(new { access_token = "process-access-after", refresh_token = "process-refresh-after",
                token_type = "Bearer", scope = "library:read offline_access", expires_in = 600 });
        });
        app.MapGet("/api/library/stats", (HttpContext context) =>
        {
            Assert.Equal("Bearer process-access-after", context.Request.Headers.Authorization.ToString());
            Assert.False(context.Request.Headers.ContainsKey("X-Api-Key"));
            Interlocked.Increment(ref reads);
            return Results.Json(new { count = 1 });
        });
        try
        {
            await app.StartAsync();
            var issuer = app.Urls.Single().TrimEnd('/') + "/";
            var settings = new McpLoginSettings(issuer, issuer + "api", "shared", Path.Combine(directory, "credentials"));
            using (var selected = await new McpCredentialStore(settings).LockAsync())
                await selected.WriteAsync(new McpCredential
                {
                    Issuer = issuer, Resource = settings.Resource, Connection = settings.Connection,
                    AccessToken = "process-access-before", RefreshToken = "process-refresh-before",
                    Scope = "library:read offline_access", AccessExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1)
                });
            await Task.WhenAll(CallToolInProcessAsync(settings, caPath), CallToolInProcessAsync(settings, caPath));
            // A later fresh process must reuse the replacement, rather than a cached predecessor.
            await CallToolInProcessAsync(settings, caPath);
            Assert.Equal(1, renewals);
            Assert.Equal(3, reads);
        }
        finally { await app.StopAsync(); Directory.Delete(directory, recursive: true); }
    }

    private static async Task CallToolInProcessAsync(McpLoginSettings settings, string caPath)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "GameHub.Tests.runtimeconfig.json"),
                     "--depsfile", Path.Combine(AppContext.BaseDirectory, "GameHub.Tests.deps.json"), typeof(CoreApiClient).Assembly.Location })
            start.ArgumentList.Add(argument);
        // Trust only this test's public CA in the child; production certificate checks stay enabled.
        start.Environment["SSL_CERT_FILE"] = caPath;
        start.Environment["Mcp__AuthenticationMode"] = "User";
        start.Environment["Mcp__Issuer"] = settings.Issuer.AbsoluteUri;
        start.Environment["Mcp__Resource"] = settings.Resource;
        start.Environment["Mcp__Connection"] = settings.Connection;
        start.Environment["Mcp__CredentialDirectory"] = settings.Directory;
        start.Environment["Core__ApiKey"] = "must-not-be-forwarded";
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"renewal-test\",\"version\":\"1\"}}}");
            await ReadResponseAsync(1);
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"get_statistics\",\"arguments\":{}}}");
            var result = await ReadResponseAsync(2);
            Assert.False(result.TryGetProperty("isError", out var error) && error.GetBoolean(), result.ToString());
            Assert.Contains("count", result.ToString());
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("process-access-", await stderr);
            Assert.DoesNotContain("process-refresh-", await stderr);
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }

        async Task<JsonElement> ReadResponseAsync(int id)
        {
            for (var count = 0; count < 100; count++)
            {
                var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
                Assert.NotNull(line);
                using var document = JsonDocument.Parse(line);
                Assert.Equal("2.0", document.RootElement.GetProperty("jsonrpc").GetString());
                if (document.RootElement.TryGetProperty("id", out var identifier) && identifier.GetInt32() == id)
                    return document.RootElement.GetProperty("result").Clone();
            }
            throw new InvalidOperationException("No matching process response.");
        }
    }
}
