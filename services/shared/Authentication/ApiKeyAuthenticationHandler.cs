using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;

namespace GameHub.Shared.Authentication;

/// <summary>
/// Custom ASP.NET Core authentication handler that validates requests using a static API key.
///
/// Why AuthenticationHandler instead of raw middleware:
///   - Integrates with ASP.NET's standard [Authorize] attribute
///   - When JWT is added later, both schemes coexist — no refactoring needed
///   - Standard 401/403 responses via the authentication pipeline
///
/// Behavior:
///   - Missing API keys fail closed unless anonymous development is explicitly enabled
///   - If API key is configured → X-Api-Key header must match
///   - Assigns the configured server-side tenant, or the legacy default tenant
///   - Creates a ClaimsPrincipal with "ApiKeyUser" identity (ready for role claims)
/// </summary>
public class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>The authentication scheme name used to register this handler.</summary>
    public const string SchemeName = "ApiKey";

    /// <summary>The HTTP header name that clients must set.</summary>
    public const string HeaderName = "X-Api-Key";

    /// <summary>Configuration key for the API key value.</summary>
    private const string ConfigKey = "Authentication:ApiKey";

    /// <summary>Optional trusted tenant assignment for the configured API key.</summary>
    private const string TenantConfigKey = "Authentication:TenantId";

    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory loggerFactory,
        UrlEncoder encoder,
        IConfiguration configuration,
        IHostEnvironment environment)
        : base(options, loggerFactory, encoder)
    {
        _configuration = configuration;
        _environment = environment;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Tenant delegation is a separate service-to-service credential flow. Rejecting the
        // header here ensures that a normal API key can never select a tenant, even if this
        // handler is invoked directly instead of through Ingest's policy scheme.
        if (Request.Headers.ContainsKey(TenantDelegationAuthenticationHandler.TenantHeaderName))
        {
            return Task.FromResult(AuthenticateResult.Fail(
                $"Header '{TenantDelegationAuthenticationHandler.TenantHeaderName}' requires internal service authentication"));
        }

        var tenantId = _configuration[TenantConfigKey];
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            tenantId = GameHubTenantDefaults.DefaultTenantId.ToString("D");
        }
        else if (!Guid.TryParse(tenantId, out var configuredTenantId) || configuredTenantId == Guid.Empty)
        {
            return Task.FromResult(AuthenticateResult.Fail(
                $"Configuration value '{TenantConfigKey}' must be a non-empty GUID"));
        }
        else
        {
            tenantId = configuredTenantId.ToString("D");
        }

        // JWT or frontend login must never leave an anonymous alternative authentication path.
        var configuredKey = _configuration[ConfigKey];
        if (string.IsNullOrWhiteSpace(configuredKey))
        {
            if (!AuthenticationSafety.AllowsAnonymous(_configuration, _environment))
            {
                return Task.FromResult(AuthenticateResult.Fail("API-key authentication is not configured"));
            }
            Logger.LogDebug("Explicit anonymous development access enabled");
            var anonymousClaims = new[]
            {
                new Claim(ClaimTypes.Name, "anonymous"),
                new Claim(GameHubClaimTypes.TenantId, tenantId)
            };
            var anonymousIdentity = new ClaimsIdentity(anonymousClaims, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(anonymousIdentity), SchemeName)));
        }

        // Check for the X-Api-Key header
        if (!Request.Headers.TryGetValue(HeaderName, out var providedKey)
            || providedKey.Count != 1
            || string.IsNullOrEmpty(providedKey[0]))
        {
            return Task.FromResult(AuthenticateResult.Fail("Missing X-Api-Key header"));
        }

        // Constant-time comparison to prevent timing attacks
        var providedBytes = Encoding.UTF8.GetBytes(providedKey[0]!);
        var configuredBytes = Encoding.UTF8.GetBytes(configuredKey);
        var keysMatch = providedBytes.Length == configuredBytes.Length
            && CryptographicOperations.FixedTimeEquals(providedBytes, configuredBytes);
        CryptographicOperations.ZeroMemory(providedBytes);
        CryptographicOperations.ZeroMemory(configuredBytes);
        if (!keysMatch)
        {
            Logger.LogWarning("Invalid API key provided from {IP}", Context.Connection.RemoteIpAddress);
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key"));
        }

        // Success — create an authenticated principal
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "ApiKeyUser"),
            new Claim(ClaimTypes.AuthenticationMethod, SchemeName),
            new Claim(GameHubClaimTypes.TenantId, tenantId)
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);

        Logger.LogDebug("API key authentication succeeded");
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
