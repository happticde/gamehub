using GameHub.Mcp;
using GameHub.Mcp.Tools;
using GameHub.Mcp.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

// ============================================================
// GameHub MCP Server
//
// Exposes the GameHub unified game library as a Model Context
// Protocol (MCP) server over stdio. AI clients (Claude Desktop,
// VS Code Copilot, Cursor, etc.) can launch this as a subprocess
// and call library tools via JSON-RPC over stdin/stdout.
//
// IMPORTANT: All application logs go to STDERR so they don't
// corrupt the JSON-RPC stream on STDOUT.
// ============================================================

// Commands have a separate path: no host, logging pipeline or stdio server is started.
if (args.Length > 0)
{
    if (args.Length > 2 || args[0] is not ("setup" or "login" or "logout" or "status")
        || (args.Length == 2 && (args[0] != "login" || args[1] != "--tracking-write")))
    {
        await Console.Error.WriteLineAsync("Use setup, login [--tracking-write], status or logout. Codes and tokens are never arguments.");
        Environment.ExitCode = 1;
        return;
    }
    using var cancelled = new CancellationTokenSource();
    if (args[0] == "login" && Console.IsInputRedirected)
    {
        await Console.Error.WriteLineAsync("Run login in an interactive terminal (docker run --rm -it). Normal stdio startup never starts login.");
        Environment.ExitCode = 1;
        return;
    }
    Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancelled.Cancel(); };
    try
    {
        var settings = new McpLoginSettings(Environment.GetEnvironmentVariable("Mcp__Issuer"),
            Environment.GetEnvironmentVariable("Mcp__Resource"), Environment.GetEnvironmentVariable("Mcp__Connection"),
            Environment.GetEnvironmentVariable("Mcp__CredentialDirectory") ?? "/var/lib/gamehub-mcp");
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        Environment.ExitCode = await new McpLoginCommands(settings, http, Console.Error)
            .RunAsync(args[0], args.Length == 2, cancelled.Token);
    }
    catch (McpLoginException exception)
    { await Console.Error.WriteLineAsync(exception.Message); Environment.ExitCode = 1; }
    return;
}

var builder = Host.CreateApplicationBuilder(args);

// --- Logging: redirect everything to stderr so stdout stays clean for MCP ---
builder.Logging.AddConsole(opts =>
{
    opts.LogToStandardErrorThreshold = LogLevel.Trace;
});

// --- HTTP client for the Core REST API ---
builder.Services.AddHttpClient<CoreApiClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        // Keep normal TLS validation and never forward the API key to a redirect destination.
        AllowAutoRedirect = false
    });

// --- MCP server: stdio transport + auto-discover tools in this assembly ---
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly(typeof(LibraryTools).Assembly);

var app = builder.Build();
await app.RunAsync();
