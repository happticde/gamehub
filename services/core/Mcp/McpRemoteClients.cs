using System.Globalization;
using GameHub.Core.Services;

namespace GameHub.Core.Mcp;

/// <summary>The two native registration profiles accepted for the public MCP resource.</summary>
internal sealed record McpRemoteClient(string Id, string Name, IReadOnlyList<string> RedirectUris, bool EphemeralPort)
{
    private static readonly string[] Hosts = ["127.0.0.1", "localhost"];
    private static readonly McpRemoteClient Codex = new("https://chatgpt.com/oauth/codex/client.json",
        "Codex", Hosts.Select(host => $"http://{host}/callback").ToArray(), EphemeralPort: true);
    private static readonly McpRemoteClient Hermes = new("https://nousresearch.github.io/hermes-agent/docs/oauth/client-metadata.json",
        "Hermes Agent", (from host in Hosts from port in Enumerable.Range(27890, 5)
                         select $"http://{host}:{port}/callback").ToArray(), EphemeralPort: false);

    internal static McpRemoteClient? Find(string? id) =>
        id == Codex.Id ? Codex : id == Hermes.Id ? Hermes : null;

    internal bool AllowsRedirect(string? redirect, IReadOnlyCollection<string> declared)
    {
        if (redirect is null) return false;
        if (declared.Contains(redirect, StringComparer.Ordinal)) return true;
        if (!EphemeralPort || !Uri.TryCreate(redirect, UriKind.Absolute, out var uri)
            || uri.IsDefaultPort || uri.Port <= 0 || !Hosts.Contains(uri.Host, StringComparer.Ordinal))
            return false;
        // Comparing the original text with this exact spelling excludes URI normalization,
        // user info, suffixes, fragments, queries and encoded path aliases.
        return redirect == $"http://{uri.Host}:{uri.Port.ToString(CultureInfo.InvariantCulture)}/callback"
            && declared.Contains($"http://{uri.Host}/callback", StringComparer.Ordinal);
    }
}

internal sealed record McpRemoteResource(Uri Issuer, Uri Uri)
{
    internal const string Path = "/mcp";
    internal const string AuthorizationPath = "/connect/authorize";
    internal const string ConsentPath = "/connect/authorize/consent";
    internal const string BrowserPath = "/mcp/authorize";

    internal static McpRemoteResource Create(McpRestResource rest) => new(rest.Issuer, new Uri(rest.Issuer, Path));

    internal static string? ForClient(string? client, McpRestResource rest, McpRemoteResource? remote) =>
        client == McpDeviceProtocol.ClientId ? rest.Resource.AbsoluteUri
            : McpRemoteClient.Find(client) is not null ? remote?.Uri.AbsoluteUri : null;
}
