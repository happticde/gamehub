using GameHub.Mcp;
using GameHub.Mcp.Tools;
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
        // Allow HTTP (not just HTTPS) for local dev
        ServerCertificateCustomValidationCallback =
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    });

// --- MCP server: stdio transport + auto-discover tools in this assembly ---
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly(typeof(LibraryTools).Assembly);

var app = builder.Build();
await app.RunAsync();
