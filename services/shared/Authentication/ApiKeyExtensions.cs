using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GameHub.Shared.Authentication;

/// <summary>
/// Extension methods for registering API key authentication.
///
/// Usage in Program.cs:
///   builder.Services.AddApiKeyAuthentication(builder.Configuration, builder.Environment);
///
/// This keeps the service registration clean and centralizes the auth setup
/// so both Ingest and Core use identical configuration.
///
/// When JWT is added later, this method can register both schemes:
///   builder.Services.AddApiKeyAuthentication(builder.Configuration, builder.Environment);
///   builder.Services.AddAuthentication().AddJwtBearer(...);
/// </summary>
public static class ApiKeyExtensions
{
    /// <summary>
    /// Registers API key authentication as the default scheme.
    /// Missing credentials fail closed; anonymous access requires an explicit development opt-in.
    /// </summary>
    public static IServiceCollection AddApiKeyAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        AuthenticationSafety.Validate(configuration, environment);
        services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                ApiKeyAuthenticationHandler.SchemeName, null);

        services.AddAuthorization();

        return services;
    }
}
