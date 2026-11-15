namespace GameHub.Mcp.Authentication;

/// <summary>Exact operator-selected trust and a local connection selector; never credentials.</summary>
public sealed class McpLoginSettings
{
    /// <summary>Public software identifier, without device or owner authority.</summary>
    public const string ClientId = "gamehub-mcp-stdio";
    /// <summary>Validated root HTTPS issuer.</summary>
    public Uri Issuer { get; }
    /// <summary>Exact REST resource for this issuer.</summary>
    public string Resource { get; }
    /// <summary>Independent local connection name, unique per assistant installation.</summary>
    public string Connection { get; }
    /// <summary>Dedicated credential volume root.</summary>
    public string Directory { get; }

    /// <summary>Rejects ambiguous destinations and unsafe selectors before any storage or network work.</summary>
    public McpLoginSettings(string? issuer, string? resource, string? connection, string? directory)
    {
        if (!Uri.TryCreate(issuer, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.AbsolutePath != "/" || uri.UserInfo.Length != 0 || uri.Query.Length != 0
            || uri.Fragment.Length != 0 || uri.AbsoluteUri != issuer
            || resource != new Uri(uri, "api").AbsoluteUri
            || string.IsNullOrEmpty(connection) || connection.Length > 64
            || connection.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))
            || string.IsNullOrEmpty(directory) || !Path.IsPathFullyQualified(directory))
            throw new McpLoginException("Configure Mcp__Issuer (root HTTPS URL with trailing /), Mcp__Resource (issuer + api), Mcp__Connection (1-64 letters/digits/-/_) and an absolute Mcp__CredentialDirectory.");
        Issuer = uri;
        Resource = resource;
        Connection = connection;
        Directory = Path.GetFullPath(directory);
    }
}

/// <summary>A fixed, safe user message. Never wrap remote bodies or filesystem/network exceptions.</summary>
public sealed class McpLoginException(string message) : Exception(message);
