using System.Security.Cryptography;
using System.Text;
using GameHub.Core.Mcp;

namespace GameHub.Core.Services;

/// <summary>Opt-in managed gateway trust, independent of browser identity and protocol permissions.</summary>
public sealed record McpManagedDeployment(string Directory, string Generation, string Origin, string TransportKey)
{
    /// <summary>Generated admission credential, never an account or tool authorization.</summary>
    public const string TransportHeader = "X-GameHub-Mcp-Transport-Key";
    /// <summary>Pairs the independently mounted Frontend and Core stores.</summary>
    public const string GenerationHeader = "X-GameHub-Mcp-Generation";
    /// <summary>Private, service-authenticated readiness route; never publicly forwarded.</summary>
    public const string ReadinessPath = "/internal/mcp/readiness";

    /// <summary>Loads dedicated private files and derives exact protocol identifiers before startup.</summary>
    public static McpManagedDeployment? Load(ConfigurationManager configuration)
    {
        var enabled = configuration["Mcp:Managed:Enabled"];
        if (enabled is not null && !bool.TryParse(enabled, out _)) throw new McpKeyStoreException("invalid_enablement");
        if (!bool.TryParse(enabled, out var active) || !active) return null;
        if (configuration.GetSection("Mcp:Device").GetChildren().Any())
            throw new McpKeyStoreException("configuration_conflict");
        var directory = configuration["Mcp:Managed:Directory"] ?? "/var/lib/gamehub-mcp";
        var state = new McpManagedKeyStore(directory).ReadState();
        if (new[] { "Authentication:ApiKey", "Authentication:Jwt:SigningKey", "Authentication:Oidc:BffServiceKey",
                "Services:IngestServiceKey" }.Select(key => configuration[key]?.Trim())
            .Any(key => key == state.TransportKey || key == state.ConsentKey))
            throw new McpKeyStoreException("configuration_conflict");
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mcp:Device:Enabled"] = "true",
            ["Mcp:Device:Issuer"] = state.PublicOrigin + "/",
            ["Mcp:Device:Resource"] = state.PublicOrigin + "/api",
            ["Mcp:Device:PublicOrigin"] = state.PublicOrigin,
            ["Mcp:Device:BffServiceKey"] = state.ConsentKey,
            ["Mcp:Device:ManagedKeyDirectory"] = directory
        });
        return new(directory, state.Generation, state.PublicOrigin, state.TransportKey);
    }

    /// <summary>Rejects missing, duplicate and mismatched pairing proof with constant-time key comparison.</summary>
    public bool IsTrusted(HttpContext context) => context.Request.Headers[TransportHeader] is { Count: 1 } keys
        && keys[0] is { Length: >= 32 and <= 512 } key
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(TransportKey), Encoding.UTF8.GetBytes(key))
        && context.Request.Headers[GenerationHeader] is { Count: 1 } generations && generations[0] == Generation;

    /// <summary>Prevents generated credentials from entering automatic record diagnostics.</summary>
    public override string ToString() => "MCP managed deployment";
}

/// <summary>Restores HTTPS authority only after generated gateway proof, before protocol authentication.</summary>
public sealed class McpManagedGatewayMiddleware(RequestDelegate next)
{
    /// <summary>Admits protocol traffic independently from browser routes and keeps private checks secret-free.</summary>
    public async Task InvokeAsync(HttpContext context, McpManagedDeployment deployment)
    {
        var path = context.Request.Path.Value ?? "";
        var remote = context.Request.Path.StartsWithSegments(McpRemoteResource.Path, StringComparison.OrdinalIgnoreCase);
        var discovery = path.StartsWith(McpRemoteDiscovery.MetadataPrefix, StringComparison.OrdinalIgnoreCase);
        var protocol = path.StartsWith("/connect", StringComparison.OrdinalIgnoreCase)
            || discovery
            || remote
            || path.StartsWith("/internal/mcp", StringComparison.OrdinalIgnoreCase);
        if (!protocol && !context.Request.Headers.ContainsKey(McpManagedDeployment.TransportHeader))
        { await next(context); return; }
        context.Response.Headers.CacheControl = "no-store";
        if (!deployment.IsTrusted(context)) { context.Response.StatusCode = 404; return; }
        if (discovery && (!HttpMethods.IsGet(context.Request.Method) || path is not
                (McpRemoteDiscovery.ResourceMetadataPath or McpRemoteDiscovery.ServerMetadataPath)))
        { context.Response.StatusCode = 404; return; }
        if (remote && path != McpRemoteResource.Path) { context.Response.StatusCode = 404; return; }
        if ((remote || discovery) && context.Request.QueryString.HasValue)
        { context.Response.StatusCode = 400; return; }
        if (remote)
        {
            if (context.Request.Headers.TryGetValue("Origin", out var origin)
                && (origin.Count != 1 || origin[0] != deployment.Origin))
            { context.Response.StatusCode = 403; return; }
        }
        // Caller-selected forwarding headers never supply public authority or an owner.
        context.Request.Headers.Remove("Forwarded");
        foreach (var header in context.Request.Headers.Keys.Where(name => name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)).ToArray())
            context.Request.Headers.Remove(header);
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(new Uri(deployment.Origin).Authority);
        if (path == McpManagedDeployment.ReadinessPath)
        {
            if (!HttpMethods.IsGet(context.Request.Method) || context.Request.QueryString.HasValue)
            { context.Response.StatusCode = 404; return; }
            var device = context.RequestServices.GetRequiredService<McpDeviceSettings>();
            if (!device.IsTrustedBff(context)
                || context.Request.Headers.Origin is not { Count: 1 } origins || origins[0] != deployment.Origin)
            { context.Response.StatusCode = 404; return; }
            try
            {
                var store = new McpManagedKeyStore(deployment.Directory);
                var state = store.ReadState();
                if (state.Generation != deployment.Generation || state.TransportKey != deployment.TransportKey
                    || state.ConsentKey != device.BffServiceKey
                    || state.PublicOrigin != deployment.Origin) throw new McpKeyStoreException("generation_mismatch");
                var readiness = store.Check();
                context.Response.StatusCode = readiness.Code == "ready" ? 200 : 503;
                await context.Response.WriteAsJsonAsync(new { code = readiness.Code }, context.RequestAborted);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                context.Response.StatusCode = 503;
                await context.Response.WriteAsJsonAsync(new { code = "managed_storage_unavailable" }, context.RequestAborted);
            }
            return;
        }
        await next(context);
    }
}
