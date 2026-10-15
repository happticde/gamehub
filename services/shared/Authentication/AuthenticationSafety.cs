using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace GameHub.Shared.Authentication;

/// <summary>Fails closed unless anonymous access is explicitly chosen for development.</summary>
public static class AuthenticationSafety
{
    /// <summary>The opt-in is deliberately independent from an absent API key.</summary>
    public const string AnonymousDevelopmentKey = "Authentication:AllowAnonymousLocalDevelopment";

    /// <summary>Checks the complete configuration before either service starts.</summary>
    public static void Validate(IConfiguration configuration, IHostEnvironment environment)
    {
        var anonymous = ReadAnonymousOptIn(configuration);
        if (anonymous && (!environment.IsDevelopment() || HasBrowserAuthentication(configuration)))
        {
            throw new InvalidOperationException(
                "Anonymous access requires Development and cannot be combined with JWT, frontend authentication, OIDC, or multi-user mode.");
        }

        if (!environment.IsDevelopment())
        {
            foreach (var key in new[] { "Authentication:ApiKey", "Authentication:ServiceKey" })
            {
                var secret = configuration[key];
                if (!string.IsNullOrEmpty(secret)
                    && (secret.Length is < 32 or > 512 || secret.Any(character => character is < '!' or > '~')))
                {
                    throw new InvalidOperationException($"{key} requires 32-512 printable non-space ASCII characters outside Development.");
                }
            }
        }

        if (!anonymous
            && string.IsNullOrWhiteSpace(configuration["Authentication:ApiKey"])
            && string.IsNullOrWhiteSpace(configuration["Authentication:Jwt:SigningKey"])
            && string.IsNullOrWhiteSpace(configuration["Authentication:ServiceKey"]))
        {
            throw new InvalidOperationException(
                "Configure authentication before starting GameHub. Anonymous development requires an explicit Development-only opt-in.");
        }
    }

    /// <summary>Also protects directly constructed handlers and configuration changes.</summary>
    public static bool AllowsAnonymous(IConfiguration configuration, IHostEnvironment environment) =>
        environment.IsDevelopment() && ReadAnonymousOptIn(configuration) && !HasBrowserAuthentication(configuration);

    private static bool ReadAnonymousOptIn(IConfiguration configuration)
    {
        var value = configuration[AnonymousDevelopmentKey];
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (bool.TryParse(value, out var enabled)) return enabled;
        throw new InvalidOperationException($"{AnonymousDevelopmentKey} must be true or false.");
    }

    private static bool HasBrowserAuthentication(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration["Authentication:Jwt:SigningKey"])
        || configuration.GetValue<bool>("Authentication:Frontend:Enabled")
        || configuration.GetValue<bool>("Authentication:Oidc:Enabled")
        || configuration.GetValue<bool>("MultiUser:Enabled");
}
