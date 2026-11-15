using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using GameHub.Mcp;
using GameHub.Mcp.Authentication;
using Xunit;

namespace GameHub.Tests;

public sealed class McpLoginTests
{
    [Fact]
    public async Task Login_PendingSlowDownAndRestart_PreservesPrivateSelectedCredential()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new LoginFixture();
        var timer = new FastClock();
        var transport = new ProtocolHandler([
            Device(), Json(new { error = "authorization_pending" }, 400), Json(new { error = "slow_down" }, 400), Tokens()
        ]);
        var terminal = new StringWriter();
        using var http = new HttpClient(transport);
        Assert.Equal(0, await new McpLoginCommands(fixture.Settings, http, terminal, timer).RunAsync("login"));
        Assert.Equal(new[] { 5d, 5d, 10d }, timer.Delays);
        Assert.Contains("Open https://issuer.example/mcp/verify", terminal.ToString());
        Assert.Contains("Enter code: ABCD-EFGH", terminal.ToString());
        Assert.DoesNotContain("opaque-access", terminal.ToString());
        Assert.DoesNotContain("opaque-renewal", terminal.ToString());
        Assert.DoesNotContain("device-secret", terminal.ToString());
        Assert.All(transport.Requests, request => Assert.DoesNotContain("secret", request.Url));
        Assert.DoesNotContain("tracking:write", transport.Requests[0].Body);
        using (var restarted = await new McpCredentialStore(fixture.Settings).LockAsync())
            Assert.Equal("opaque-renewal", (await restarted.ReadAsync())!.RefreshToken);
        foreach (var path in Directory.GetDirectories(fixture.Path)) Assert.Equal((UnixFileMode)448, File.GetUnixFileMode(path));
        foreach (var path in Directory.GetFiles(fixture.Path, "*", SearchOption.AllDirectories)) Assert.Equal((UnixFileMode)384, File.GetUnixFileMode(path));
        var child = await fixture.CommandAsync("status");
        Assert.Equal(0, child.Code);
        Assert.Empty(child.Stdout);
        Assert.Contains("Authorization stored", child.Stderr);
        Assert.DoesNotContain("opaque-", child.Stderr);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(503)]
    [InlineData(302)]
    [InlineData(999)]
    public async Task Logout_RemoteFailureOrSuccess_RemovesOnlySelectedLocalCredential(int status)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new LoginFixture();
        var other = fixture.ForConnection("other");
        await fixture.SaveAsync(fixture.Settings);
        await fixture.SaveAsync(other);
        var response = status == 999 ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("invalid-json") }
            : new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("") };
        var handler = new ProtocolHandler([response]);
        using var http = new HttpClient(handler);
        var terminal = new StringWriter();
        Assert.Equal(status == 200 ? 0 : 2, await new McpLoginCommands(fixture.Settings, http, terminal).RunAsync("logout"));
        Assert.Contains(status == 200 ? "remote connection revoked" : "Remote revocation could not be confirmed", terminal.ToString());
        Assert.DoesNotContain("opaque-", terminal.ToString());
        using (var selected = await new McpCredentialStore(fixture.Settings).LockAsync()) Assert.Null(await selected.ReadAsync());
        using (var retained = await new McpCredentialStore(other).LockAsync()) Assert.NotNull(await retained.ReadAsync());
        Assert.Contains("token=opaque-renewal", Assert.Single(handler.Requests).Body);
        Assert.Equal("https://issuer.example/connect/revoke", handler.Requests[0].Url);
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("permissions")]
    [InlineData("symlink")]
    [InlineData("binding")]
    [InlineData("oversized")]
    [InlineData("duplicate")]
    public async Task Storage_UnsafeOrCorruptCredential_FailsClosedWithoutRemoteWork(string fault)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new LoginFixture();
        await fixture.SaveAsync(fixture.Settings);
        var path = Directory.GetFiles(fixture.Path, "credential.json", SearchOption.AllDirectories).Single();
        switch (fault)
        {
            case "corrupt": await File.WriteAllTextAsync(path, "{broken"); break;
            case "permissions": File.SetUnixFileMode(path, (UnixFileMode)420); break;
            case "symlink": File.Delete(path); File.CreateSymbolicLink(path, Path.Combine(fixture.Path, "missing")); break;
            case "binding": await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path)).Replace("primary", "foreign")); break;
            case "oversized": await File.WriteAllTextAsync(path, new string('x', 9000)); break;
            case "duplicate": await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path)).Replace("\"Version\":1", "\"Version\":1,\"Version\":1")); break;
        }
        using var http = new HttpClient(new ProtocolHandler([]));
        var output = new StringWriter();
        Assert.Equal(1, await new McpLoginCommands(fixture.Settings, http, output).RunAsync("logout"));
        Assert.DoesNotContain("opaque-", output.ToString());
        Assert.True(File.Exists(path) || new FileInfo(path).LinkTarget is not null);
    }

    [Fact]
    public async Task Storage_InterruptedWriteAndIndependentIssuer_KeepPreviousCredential()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new LoginFixture();
        await fixture.SaveAsync(fixture.Settings);
        var otherIssuer = new McpLoginSettings("https://other.example/", "https://other.example/api", "primary", fixture.Path);
        using (var other = await new McpCredentialStore(otherIssuer).LockAsync()) Assert.Null(await other.ReadAsync());
        using var selected = await new McpCredentialStore(fixture.Settings).LockAsync();
        var previous = (await selected.ReadAsync())!;
        // An orphan from a killed writer must never be mistaken for a committed credential.
        var directory = Directory.GetDirectories(fixture.Path).Single(path => File.Exists(Path.Combine(path, "credential.json")));
        await File.WriteAllTextAsync(Path.Combine(directory, "interrupted.tmp"), "partial");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selected.WriteAsync(previous, cancelled.Token));
        Assert.Equal(previous.RefreshToken, (await selected.ReadAsync())!.RefreshToken);
        Assert.Single(Directory.GetFiles(directory, "*.tmp"));
        // A real subprocess must fail to acquire the held inode, then succeed after disposal.
        var blocked = await fixture.CommandAsync("setup");
        Assert.Equal(1, blocked.Code);
        Assert.Empty(blocked.Stdout);
        selected.Dispose();
        Assert.Equal(0, (await fixture.CommandAsync("setup")).Code);
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("expired")]
    [InlineData("redirect")]
    [InlineData("wrong-verification")]
    [InlineData("scope-widening")]
    [InlineData("oversized")]
    public async Task Login_UntrustedOrTerminalResponse_DoesNotPersistSecrets(string fault)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new LoginFixture();
        var responses = fault switch
        {
            "denied" => new[] { Device(), Json(new { error = "access_denied", error_description = "sensitive-remote-message" }, 400) },
            "expired" => new[] { Device(), Json(new { error = "expired_token" }, 400) },
            "redirect" => new[] { new HttpResponseMessage(HttpStatusCode.Redirect) { Content = new StringContent("") } },
            "wrong-verification" => new[] { Device("https://foreign.example/mcp/verify") },
            "scope-widening" => new[] { Device(), Tokens("library:read tracking:write offline_access") },
            _ => new[] { new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 9000)) } }
        };
        using var http = new HttpClient(new ProtocolHandler(responses));
        var terminal = new StringWriter();
        Assert.Equal(1, await new McpLoginCommands(fixture.Settings, http, terminal, new FastClock()).RunAsync("login"));
        Assert.DoesNotContain("sensitive-remote-message", terminal.ToString());
        Assert.DoesNotContain("opaque-", terminal.ToString());
        using var selected = await new McpCredentialStore(fixture.Settings).LockAsync();
        Assert.Null(await selected.ReadAsync());
    }

    [Fact]
    public async Task Login_Cancellation_DoesNotPersistPendingCode()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new LoginFixture();
        using var cancelled = new CancellationTokenSource();
        var handler = new ProtocolHandler([Device()], () => cancelled.Cancel());
        using var http = new HttpClient(handler);
        var terminal = new StringWriter();
        Assert.Equal(1, await new McpLoginCommands(fixture.Settings, http, terminal).RunAsync("login", cancellationToken: cancelled.Token));
        Assert.Contains("cancelled", terminal.ToString());
        Assert.Empty(Directory.GetFiles(fixture.Path, "credential.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Commands_InvalidArgumentsAndNoninteractiveLogin_KeepStdoutEmpty()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new LoginFixture();
        foreach (var command in new[] { "login", "--token=synthetic-argument" })
        {
            var result = await fixture.CommandAsync(command);
            Assert.Equal(1, result.Code);
            Assert.Empty(result.Stdout);
            Assert.DoesNotContain("synthetic-argument", result.Stderr);
        }
    }

    [Theory]
    [InlineData("http://issuer.example/", "http://issuer.example/api", "primary")]
    [InlineData("https://issuer.example/", "https://other.example/api", "primary")]
    [InlineData("https://issuer.example/", "https://issuer.example/api", "../other")]
    [InlineData("https://issuer.example/?code=bad", "https://issuer.example/api", "primary")]
    public void Settings_AmbiguousTrustOrSelector_RejectsBeforeSideEffects(string issuer, string resource, string connection)
        => Assert.Throws<McpLoginException>(() => new McpLoginSettings(issuer, resource, connection, "/tmp/unused"));

    private static HttpResponseMessage Json(object value, int status = 200) => new((HttpStatusCode)status)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Device(string verification = "https://issuer.example/mcp/verify") => Json(new
    { device_code = "device-secret", user_code = "ABCD-EFGH", verification_uri = verification, expires_in = 600, interval = 5 });
    private static HttpResponseMessage Tokens(string scope = "library:read offline_access") => Json(new
    { access_token = "opaque-access", refresh_token = "opaque-renewal", scope, expires_in = 600, token_type = "Bearer" });

    private sealed class ProtocolHandler(IEnumerable<HttpResponseMessage> responses, Action? afterResponse = null) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new(responses);
        internal List<(string Url, string Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.AbsoluteUri, await request.Content!.ReadAsStringAsync(cancellationToken)));
            var response = responses.Dequeue();
            afterResponse?.Invoke();
            return response;
        }
    }

    private sealed class FastClock : TimeProvider
    {
        private long ticks;
        internal List<double> Delays { get; } = [];
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref ticks));
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Delays.Add(dueTime.TotalSeconds);
            Interlocked.Add(ref ticks, dueTime.Ticks);
            return new Timer(callback, state, TimeSpan.FromMilliseconds(1), Timeout.InfiniteTimeSpan);
        }
    }

    private sealed class LoginFixture : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gamehub-login-" + Guid.NewGuid().ToString("N"));
        internal McpLoginSettings Settings => ForConnection("primary");
        internal McpLoginSettings ForConnection(string name) => new("https://issuer.example/", "https://issuer.example/api", name, Path);
        internal async Task SaveAsync(McpLoginSettings settings)
        {
            using var selected = await new McpCredentialStore(settings).LockAsync();
            await selected.WriteAsync(new McpCredential
            {
                Issuer = settings.Issuer.AbsoluteUri, Resource = settings.Resource, Connection = settings.Connection,
                AccessToken = "opaque-access", RefreshToken = "opaque-renewal", Scope = "library:read offline_access",
                AccessExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
            });
        }
        internal async Task<(int Code, string Stdout, string Stderr)> CommandAsync(string command)
        {
            var start = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            // Project references share the test host's framework dependencies in this output.
            // Image smoke verification separately runs the actual published app dependencies.
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add("--runtimeconfig");
            start.ArgumentList.Add(System.IO.Path.Combine(AppContext.BaseDirectory, "GameHub.Tests.runtimeconfig.json"));
            start.ArgumentList.Add("--depsfile");
            start.ArgumentList.Add(System.IO.Path.Combine(AppContext.BaseDirectory, "GameHub.Tests.deps.json"));
            start.ArgumentList.Add(typeof(CoreApiClient).Assembly.Location);
            start.ArgumentList.Add(command);
            start.Environment["Mcp__Issuer"] = Settings.Issuer.AbsoluteUri;
            start.Environment["Mcp__Resource"] = Settings.Resource;
            start.Environment["Mcp__Connection"] = Settings.Connection;
            start.Environment["Mcp__CredentialDirectory"] = Settings.Directory;
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            return (process.ExitCode, await stdout, await stderr);
        }
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
