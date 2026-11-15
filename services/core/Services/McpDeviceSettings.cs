using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.HttpOverrides;

namespace GameHub.Core.Services;

/// <summary>Validated, default-off device authorization deployment trust.</summary>
public sealed record McpDeviceSettings(bool Enabled, McpRestResource? Resource, string BffServiceKey,
    IReadOnlyList<IPAddress> KnownProxies)
{
    /// <summary>Dedicated consent transport credential; never accepted as browser authentication.</summary>
    public const string BffHeader = "X-GameHub-Mcp-Bff-Key";

    /// <summary>Validates the complete opt-in before registering any protocol endpoint.</summary>
    public static McpDeviceSettings FromConfiguration(IConfiguration configuration, JwtSettings jwt, OidcSettings oidc)
    {
        var section = configuration.GetSection("Mcp:Device");
        var enabledText = section["Enabled"];
        if (enabledText is not null && !bool.TryParse(enabledText, out _)) throw Invalid();
        if (!bool.TryParse(enabledText, out var enabled) || !enabled) return new(false, null, "", []);
        if (!jwt.IsEnabled || !oidc.IsEnabled
            || configuration["Authentication:Frontend:Enabled"] != "true"
            || configuration["Authentication:Frontend:SecureCookies"] != "true") throw Invalid();
        var issuer = SecureUri(section["Issuer"]);
        var resource = SecureUri(section["Resource"]);
        if (issuer.AbsolutePath != "/" || issuer.AbsoluteUri != section["Issuer"]
            || resource.AbsoluteUri != issuer.AbsoluteUri + "api"
            || issuer.GetLeftPart(UriPartial.Authority) == new Uri(oidc.Issuer).GetLeftPart(UriPartial.Authority)
            || section["PublicOrigin"] != issuer.GetLeftPart(UriPartial.Authority)) throw Invalid();
        var key = section["BffServiceKey"] ?? "";
        if (key.Length is < 32 or > 512 || key.Any(character => character is < '!' or > '~')
            || new[] { jwt.SigningKey, oidc.BffServiceKey, configuration["Authentication:ApiKey"],
                configuration["Services:IngestServiceKey"] }.Select(value => value?.Trim()).Contains(key, StringComparer.Ordinal)) throw Invalid();
        var proxies = section.GetSection("KnownProxies").GetChildren().Select(child =>
            IPAddress.TryParse(child.Value, out var address) ? address : throw Invalid()).ToArray();
        return new(true, new(issuer, resource), key, proxies);
    }

    /// <summary>Loads existing private certificates and an independent persistent encrypted key ring.</summary>
    public static McpProtocolKeys LoadKeys(IConfiguration configuration)
    {
        var section = configuration.GetSection("Mcp:Device");
        string PathFor(string key)
        {
            var path = section[key];
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw Invalid();
            return path;
        }
        X509Certificate2 Load(string key) => X509CertificateLoader.LoadPkcs12FromFile(PathFor(key),
            section[key + "Password"], X509KeyStorageFlags.EphemeralKeySet);
        var retired = section.GetSection("RetiredProtectionCertificates").GetChildren().Select(child =>
        {
            if (child.Value is not { } path || !Path.IsPathFullyQualified(path)) throw Invalid();
            return X509CertificateLoader.LoadPkcs12FromFile(path, section["ProtectionCertificatePassword"],
                X509KeyStorageFlags.EphemeralKeySet);
        }).ToArray();
        return new(new DirectoryInfo(PathFor("KeyDirectory")), Load("SigningCertificate"),
            Load("EncryptionCertificate"), Load("ProtectionCertificate"), retired);
    }

    /// <summary>Only an independently trusted server may reach preview or consent processing.</summary>
    public bool IsTrustedBff(HttpContext http)
    {
        var values = http.Request.Headers[BffHeader];
        return Enabled && values.Count == 1 && values[0] is { Length: >= 32 and <= 512 } value
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(BffServiceKey), Encoding.UTF8.GetBytes(value));
    }

    /// <summary>Trusts exactly the configured proxy addresses; forwarding never selects trust anchors.</summary>
    public void ConfigureForwarding(ForwardedHeadersOptions options)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        options.ForwardLimit = 1;
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var address in KnownProxies) options.KnownProxies.Add(address);
        options.AllowedHosts.Add(Resource!.Issuer.IdnHost);
    }

    private static Uri SecureUri(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
        ? uri : throw Invalid();

    private static InvalidOperationException Invalid() => new("MCP device authorization deployment trust is incomplete or invalid.");
}
