using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameHub.Shared.Authentication;

/// <summary>
/// Authenticates internal service calls that delegate a server-selected tenant.
/// </summary>
public sealed class TenantDelegationAuthenticationHandler
    : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>The authentication scheme used for trusted tenant delegation.</summary>
    public const string SchemeName = "TenantDelegation";

    /// <summary>The header carrying the internal Core-to-Ingest credential.</summary>
    public const string ServiceKeyHeaderName = "X-GameHub-Service-Key";

    /// <summary>The header carrying the tenant selected by authenticated Core execution.</summary>
    public const string TenantHeaderName = "X-GameHub-Tenant-Id";

    private const string ConfigKey = "Authentication:ServiceKey";
    private readonly IConfiguration _configuration;

    /// <summary>Creates the internal service-key authentication handler.</summary>
    public TenantDelegationAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory loggerFactory,
        UrlEncoder encoder,
        IConfiguration configuration)
        : base(options, loggerFactory, encoder)
    {
        _configuration = configuration;
    }

    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var configuredKey = _configuration[ConfigKey];
        if (string.IsNullOrWhiteSpace(configuredKey))
        {
            return Task.FromResult(AuthenticateResult.Fail(
                $"Internal tenant delegation is disabled because '{ConfigKey}' is not configured"));
        }

        if (!Request.Headers.TryGetValue(ServiceKeyHeaderName, out var providedKeys) ||
            providedKeys.Count != 1 ||
            string.IsNullOrEmpty(providedKeys[0]))
        {
            return Task.FromResult(AuthenticateResult.Fail(
                $"Missing or invalid {ServiceKeyHeaderName} header"));
        }

        if (!KeysMatch(providedKeys[0]!, configuredKey))
        {
            Logger.LogWarning(
                "Invalid internal service key provided from {IP}",
                Context.Connection.RemoteIpAddress);
            return Task.FromResult(AuthenticateResult.Fail("Invalid internal service key"));
        }

        if (!Request.Headers.TryGetValue(TenantHeaderName, out var tenantValues) ||
            tenantValues.Count != 1 ||
            !Guid.TryParse(tenantValues[0], out var tenantId) ||
            tenantId == Guid.Empty)
        {
            return Task.FromResult(AuthenticateResult.Fail(
                $"Header '{TenantHeaderName}' must contain one non-empty tenant GUID"));
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "CoreService"),
            new Claim(ClaimTypes.AuthenticationMethod, SchemeName),
            new Claim(GameHubClaimTypes.TenantId, tenantId.ToString("D"))
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);

        Logger.LogDebug("Internal tenant delegation authenticated for tenant {TenantId}", tenantId);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private static bool KeysMatch(string providedKey, string configuredKey)
    {
        var providedBytes = Encoding.UTF8.GetBytes(providedKey);
        var configuredBytes = Encoding.UTF8.GetBytes(configuredKey);
        return providedBytes.Length == configuredBytes.Length &&
            CryptographicOperations.FixedTimeEquals(providedBytes, configuredBytes);
    }
}
