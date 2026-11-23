using System.Security.Claims;
using GameHub.Core.Data;
using GameHub.Core.Services;
using GameHub.Shared.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using ModelContextProtocol.AspNetCore;
using OpenIddict.Abstractions;

namespace GameHub.Core.Mcp;

internal static class PrivateMcpTransport
{
    internal const string Scheme = "GameHub.Mcp.Remote";
    internal const string ReadPolicy = Scheme + ".Read";
    internal const string TrackingPolicy = Scheme + ".Tracking";
    private const string ConcurrencyPolicy = Scheme + ".Concurrency";

    internal static void AddPrivateMcpTransport(this IServiceCollection services)
    {
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, McpIntegrationAuthenticationHandler>(Scheme, null);
        services.AddAuthorization(options =>
        {
            options.AddPolicy(ReadPolicy, policy => policy.AddAuthenticationSchemes(Scheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => HasRemotePermission(context.User, McpPermissions.LibraryRead)));
            options.AddPolicy(TrackingPolicy, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context => HasRemotePermission(context.User, McpPermissions.TrackingWrite)));
        });
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddConcurrencyLimiter(ConcurrencyPolicy, limiter =>
            {
                // Bound work before protocol validation, independently of login and refresh traffic.
                limiter.PermitLimit = 16;
                limiter.QueueLimit = 0;
            });
        });
        services.AddScoped<McpRemoteTracking>();
        services.AddMcpServer()
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools<RemoteLibraryTools>()
            .AddAuthorizationFilters();
    }

    internal static void MapPrivateMcpTransport(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapMcp(McpRemoteResource.Path).RequireAuthorization(ReadPolicy).RequireRateLimiting(ConcurrencyPolicy);

    internal static bool HasRemotePermission(ClaimsPrincipal principal, string permission) =>
        HasRemoteIdentity(principal) && principal.HasScope(permission);

    private static bool HasRemoteIdentity(ClaimsPrincipal principal) =>
        principal.Identities.ToArray() is [{ IsAuthenticated: true, AuthenticationType: Scheme }]
        && principal.FindAll(GameHubClaimTypes.TenantId).ToArray() is [var tenant]
        && Guid.TryParse(tenant.Value, out var tenantId) && tenantId != Guid.Empty;
}
