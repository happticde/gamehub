using System.Security.Claims;
using System.Text.Encodings.Web;
using GameHub.Core.Data;
using GameHub.Shared.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;

namespace GameHub.Core.Services;

/// <summary>Marks an individually approved library operation; unmarked operations deny MCP access.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class McpOperationAttribute(string permission) : Attribute
{
    /// <summary>Permission required at this server operation.</summary>
    public string Permission { get; } = permission;
}

/// <summary>Trusted deployment identifiers, independent from browser and upstream provider trust.</summary>
public sealed record McpRestResource(Uri Issuer, Uri Resource);

/// <summary>Registers the default-closed library boundary and the later protocol validation adapter.</summary>
public static class McpAuthorization
{
    /// <summary>Policy applied to the library, preserving web/operator behavior on unmarked operations.</summary>
    public const string LibraryPolicy = "GameHub.Library";
    /// <summary>Explicit integration scheme. Ordinary account policies never select it.</summary>
    public const string IntegrationScheme = "GameHub.Mcp";

    /// <summary>Installs operation authorization without enabling protocol issuance or production trust.</summary>
    public static IServiceCollection AddMcpLibraryAuthorization(
        this IServiceCollection services, bool jwtEnabled, bool multiUserEnabled)
    {
        services.AddScoped<McpSessionValidationService>();
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, McpIntegrationAuthenticationHandler>(IntegrationScheme, null)
            .AddPolicyScheme(LibraryPolicy, null, options =>
            {
                options.ForwardDefaultSelector = context =>
                {
                    var authorization = context.Request.Headers.Authorization.ToString();
                    if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    {
                        // Select by wire format only, never unverified token claims. Opaque
                        // integration credentials never fall back to web or operator handlers.
                        return jwtEnabled && authorization.AsSpan(7).Contains('.')
                            ? JwtBearerDefaults.AuthenticationScheme : IntegrationScheme;
                    }
                    return multiUserEnabled ? JwtBearerDefaults.AuthenticationScheme
                        : jwtEnabled ? "GameHub" : ApiKeyAuthenticationHandler.SchemeName;
                };
            });
        services.AddAuthorization(options => options.AddPolicy(LibraryPolicy, policy =>
            policy.AddAuthenticationSchemes(LibraryPolicy).RequireAuthenticatedUser()
                .RequireAssertion(context =>
                {
                    var integration = context.User.Identities.Any(identity =>
                        identity.IsAuthenticated && identity.AuthenticationType == IntegrationScheme);
                    if (!integration) return true;
                    var operation = (context.Resource as HttpContext)?.GetEndpoint()
                        ?.Metadata.GetMetadata<McpOperationAttribute>();
                    return operation is not null && context.User.HasScope(operation.Permission);
                })));
        return services;
    }

    /// <summary>
    /// Installs local protocol validation with mandatory live entry checks. The device/consent
    /// package must supply persistent independent keys and validated operator settings before use.
    /// This method is deliberately not called by production startup in the storage/authorization stage.
    /// </summary>
    public static IServiceCollection AddMcpRestTokenValidation(
        this IServiceCollection services, McpRestResource resource,
        Action<OpenIddictValidationBuilder> configure)
    {
        foreach (var uri in new[] { resource.Issuer, resource.Resource })
            if (!uri.IsAbsoluteUri || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo)
                || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new ArgumentException("MCP trust requires exact absolute HTTPS identifiers.", nameof(resource));
        services.AddSingleton(resource);
        services.AddOpenIddict()
            .AddCore(options =>
            {
                // Central transactional revocation uses database updates, bypassing manager caches.
                options.DisableEntityCaching();
                options.UseEntityFrameworkCore().UseDbContext<CoreContext>()
                    .ReplaceDefaultEntities<McpProtocolApplication, McpProtocolAuthorization,
                        McpProtocolScope, McpProtocolToken, string>();
            })
            .AddValidation(options =>
            {
                configure(options);
                options.SetIssuer(resource.Issuer);
                options.AddAudiences(resource.Resource.AbsoluteUri);
                options.EnableTokenEntryValidation();
                options.EnableAuthorizationEntryValidation();
                options.UseAspNetCore();
            });
        return services;
    }
}

/// <summary>Accepts only protocol-validated reference access tokens with live owned consent.</summary>
public sealed class McpIntegrationAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <inheritdoc/>
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var resource = Context.RequestServices.GetService<McpRestResource>();
        if (resource is null) return AuthenticateResult.NoResult();
        var header = Request.Headers.Authorization;
        if (header.Count != 1 || header[0] is not { } value
            || !value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();
        var token = value[7..];
        if (token.Length != 43 || token.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            return AuthenticateResult.Fail("Invalid integration credential.");
        try
        {
            var protocol = await Context.AuthenticateAsync(
                OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
            if (!protocol.Succeeded || protocol.Principal is null
                || !await Context.RequestServices.GetRequiredService<McpSessionValidationService>()
                    .IsActiveAsync(protocol.Principal, resource, Context.RequestAborted))
                return AuthenticateResult.Fail("Integration session unavailable.");

            // Rebuild one trusted identity: arbitrary protocol claims cannot add roles or a
            // competing tenant identity to the library request.
            var principal = protocol.Principal;
            var identity = new ClaimsIdentity(McpAuthorization.IntegrationScheme);
            foreach (var type in new[] { OpenIddictConstants.Claims.Subject, GameHubClaimTypes.TenantId,
                         GameHubClaimTypes.AccountSessionId, GameHubClaimTypes.SessionVersion })
                identity.AddClaim(new Claim(type, principal.FindFirstValue(type)!));
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier,
                principal.GetClaim(OpenIddictConstants.Claims.Subject)!));
            new ClaimsPrincipal(identity).SetScopes(principal.GetScopes());
            return AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(identity), Scheme.Name));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Storage and protocol failures deny access without logging credentials or payloads.
            Logger.LogWarning("Integration authentication is unavailable.");
            return AuthenticateResult.Fail("Integration session unavailable.");
        }
    }
}
