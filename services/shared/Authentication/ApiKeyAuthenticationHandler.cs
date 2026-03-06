using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
///   - If no API key is configured → auth is disabled (all requests succeed)
///   - If API key is configured → X-Api-Key header must match
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

    private readonly IConfiguration _configuration;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory loggerFactory,
        UrlEncoder encoder,
        IConfiguration configuration)
        : base(options, loggerFactory, encoder)
    {
        _configuration = configuration;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // If no API key is configured, auth is disabled — let everything through.
        // This preserves backward compatibility for local dev.
        var configuredKey = _configuration[ConfigKey];
        if (string.IsNullOrEmpty(configuredKey))
        {
            Logger.LogDebug("No API key configured — authentication disabled");
            var anonymousClaims = new[] { new Claim(ClaimTypes.Name, "anonymous") };
            var anonymousIdentity = new ClaimsIdentity(anonymousClaims, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(anonymousIdentity), SchemeName)));
        }

        // Check for the X-Api-Key header
        if (!Request.Headers.TryGetValue(HeaderName, out var providedKey))
        {
            return Task.FromResult(AuthenticateResult.Fail("Missing X-Api-Key header"));
        }

        // Constant-time comparison to prevent timing attacks
        if (!string.Equals(providedKey, configuredKey, StringComparison.Ordinal))
        {
            Logger.LogWarning("Invalid API key provided from {IP}", Context.Connection.RemoteIpAddress);
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key"));
        }

        // Success — create an authenticated principal
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "ApiKeyUser"),
            new Claim(ClaimTypes.AuthenticationMethod, SchemeName)
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);

        Logger.LogDebug("API key authentication succeeded");
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
