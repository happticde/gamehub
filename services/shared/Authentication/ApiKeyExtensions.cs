using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GameHub.Shared.Authentication;

/// <summary>
/// Extension methods for registering API key authentication.
///
/// Usage in Program.cs:
///   builder.Services.AddApiKeyAuthentication(builder.Configuration);
///
/// This keeps the service registration clean and centralizes the auth setup
/// so both Ingest and Core use identical configuration.
///
/// When JWT is added later, this method can register both schemes:
///   builder.Services.AddApiKeyAuthentication(builder.Configuration);
///   builder.Services.AddAuthentication().AddJwtBearer(...);
/// </summary>
public static class ApiKeyExtensions
{
    /// <summary>
    /// Registers API key authentication as the default scheme.
    /// If no key is configured in Authentication:ApiKey, auth is effectively disabled.
    /// </summary>
    public static IServiceCollection AddApiKeyAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                ApiKeyAuthenticationHandler.SchemeName, null);

        services.AddAuthorization();

        return services;
    }
}
