using System.Security.Claims;
using System.Text.Encodings.Web;
using GameHub.Core.Data;
using GameHub.Shared.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using OpenIddict.Abstractions;

namespace GameHub.Core.Mcp;

internal static class PrivateMcpTransport
{
    internal const string Scheme = "GameHub.Mcp.Remote";
    internal const string ReadPolicy = Scheme + ".Read";
    internal const string TrackingPolicy = Scheme + ".Tracking";

    internal static void AddPrivateMcpTransport(this IServiceCollection services)
    {
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, UnavailableRemoteAuthentication>(Scheme, null);
        services.AddAuthorization(options =>
        {
            options.AddPolicy(ReadPolicy, policy => policy.AddAuthenticationSchemes(Scheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => HasRemotePermission(context.User, McpPermissions.LibraryRead)));
            // Tracking requires the remote grant's transactional admission, delivered with #79.
            options.AddPolicy(TrackingPolicy, policy => policy.RequireAssertion(_ => false));
        });
        services.AddMcpServer()
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools<RemoteLibraryTools>()
            .AddAuthorizationFilters();
    }

    internal static void MapPrivateMcpTransport(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapMcp("/mcp").RequireAuthorization(ReadPolicy);

    internal static bool HasRemotePermission(ClaimsPrincipal principal, string permission) =>
        principal.Identities.ToArray() is [{ IsAuthenticated: true, AuthenticationType: Scheme }]
        && principal.FindAll(GameHubClaimTypes.TenantId).ToArray() is [var tenant]
        && Guid.TryParse(tenant.Value, out var tenantId) && tenantId != Guid.Empty
        && principal.HasScope(permission);

    // Production has no remote user validator until the exact-resource authorization path is ready.
    private sealed class UnavailableRemoteAuthentication(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());
    }
}
