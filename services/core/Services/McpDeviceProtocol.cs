using System.Collections.Immutable;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using GameHub.Core.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace GameHub.Core.Services;

/// <summary>Independent persistent protocol keys. Operators provision and protect the certificates.</summary>
public sealed class McpProtocolKeys
{
    internal IDataProtectionProvider Provider { get; }
    internal X509Certificate2 Signing { get; }
    internal X509Certificate2 Encryption { get; }

    /// <summary>Requires an existing dedicated ring and three distinct, valid RSA private certificates.</summary>
    public McpProtocolKeys(DirectoryInfo directory, X509Certificate2 signing,
        X509Certificate2 encryption, X509Certificate2 protection, params X509Certificate2[] retiredProtection)
    {
        if (!directory.Exists || directory.LinkTarget is not null)
            throw new ArgumentException("An existing dedicated protocol key directory is required.");
        var certificates = new[] { signing, encryption, protection };
        if (certificates.Select(certificate => certificate.Thumbprint).Distinct().Count() != 3
            || certificates.Any(certificate => !ValidCertificate(certificate)))
            throw new ArgumentException("Independent valid RSA protocol certificates are required.");
        Signing = signing;
        Encryption = encryption;
        Provider = DataProtectionProvider.Create(directory, options => options
            .SetApplicationName("GameHub.Mcp.Protocol.v1")
            .ProtectKeysWithCertificate(protection)
            .UnprotectKeysWithAnyCertificate([protection, .. retiredProtection]));
    }

    private static bool ValidCertificate(X509Certificate2 certificate)
    {
        using var rsa = certificate.GetRSAPublicKey();
        return certificate.HasPrivateKey && certificate.NotBefore.ToUniversalTime() <= DateTime.UtcNow
            && certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow && rsa is { KeySize: >= 2048 };
    }
}

/// <summary>Staged RFC 8628 machinery. Production activation and live consent belong to #71.</summary>
public static class McpDeviceProtocol
{
    /// <summary>Public software identifier; it conveys no account, tenant or device trust.</summary>
    public const string ClientId = "gamehub-mcp-stdio";
    /// <summary>Device request endpoint, routed directly to Core when the consent gate is delivered.</summary>
    public const string DevicePath = "/connect/device";
    /// <summary>Device redemption and integration refresh endpoint.</summary>
    public const string TokenPath = "/connect/token";
    /// <summary>Reserved consent endpoint. This package always denies browser verification.</summary>
    public const string VerifyPath = "/connect/verify";
    /// <summary>Short absolute pending lifetime, independent of eventual grant lifetime.</summary>
    public const int LifetimeSeconds = 600;
    /// <summary>Maximum live pending requests across the protocol database.</summary>
    public const int MaximumPending = 256;

    /// <summary>
    /// Registers unmodified OpenIddict processing and GameHub admission policy. Call only with
    /// independently validated deployment trust; Core startup deliberately does not call it yet.
    /// </summary>
    public static IServiceCollection AddMcpDeviceProtocol(this IServiceCollection services,
        McpRestResource resource, McpProtocolKeys keys)
    {
        foreach (var uri in new[] { resource.Issuer, resource.Resource })
            if (!uri.IsAbsoluteUri || uri.Scheme != "https" || uri.UserInfo.Length != 0
                || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new ArgumentException("Exact HTTPS protocol identifiers are required.");
        if (resource.Issuer.AbsolutePath != "/")
            throw new ArgumentException("The staged device protocol requires a root HTTPS issuer.");
        services.AddSingleton(resource);
        services.AddSingleton<McpProtocolBudget>();
        services.AddScoped<McpPollingAdmission>();
        // Framework request logging can print query strings; protocol logging can print payloads.
        // Application-owned structured diagnostics remain available, without protocol values.
        services.AddLogging(logging =>
        {
            logging.AddFilter("OpenIddict", LogLevel.None);
            logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.None);
        });
        services.AddOpenIddict()
            .AddCore(options =>
            {
                options.DisableEntityCaching();
                options.UseEntityFrameworkCore().UseDbContext<CoreContext>()
                    .ReplaceDefaultEntities<McpProtocolApplication, McpProtocolAuthorization,
                        McpProtocolScope, McpProtocolToken, string>();
            })
            .AddServer(options =>
            {
                options.SetIssuer(resource.Issuer);
                options.SetDeviceAuthorizationEndpointUris(DevicePath);
                options.SetTokenEndpointUris(TokenPath);
                options.SetEndUserVerificationEndpointUris(VerifyPath);
                options.AllowDeviceAuthorizationFlow().AllowRefreshTokenFlow();
                options.RegisterScopes(McpPermissions.LibraryRead, McpPermissions.TrackingWrite);
                options.RegisterResources(resource.Resource);
                options.SetDeviceCodeLifetime(TimeSpan.FromSeconds(LifetimeSeconds));
                options.SetUserCodeLifetime(TimeSpan.FromSeconds(LifetimeSeconds));
                options.SetAccessTokenLifetime(TimeSpan.FromMinutes(10));
                options.SetRefreshTokenLifetime(TimeSpan.FromDays(30));
                options.SetRefreshTokenReuseLeeway(null);
                options.UseReferenceAccessTokens().UseReferenceRefreshTokens();
                options.AddSigningCertificate(keys.Signing).AddEncryptionCertificate(keys.Encryption);
                options.UseDataProtection().Configure(configuration => configuration.DataProtectionProvider = keys.Provider);
                options.UseAspNetCore().EnableTokenEndpointPassthrough().EnableEndUserVerificationEndpointPassthrough();
                // GameHub authorizations require an owned independent family. Pending requests
                // have no owner; consent must explicitly persist their authorization in #71.
                // Replace only automatic ad-hoc creation, never token/authorization validation.
                options.RemoveEventHandler(OpenIddictServerHandlers.AttachAuthorization.Descriptor);
                options.AddEventHandler<OpenIddictServerEvents.ProcessSignInContext>(builder =>
                    builder.UseInlineHandler(context =>
                    {
                        if (context.EndpointType != OpenIddictServerEndpointType.DeviceAuthorization
                            && (context.Principal is null || string.IsNullOrEmpty(context.Principal.GetAuthorizationId())))
                            context.Reject(Errors.AccessDenied);
                        return default;
                    }).SetOrder(OpenIddictServerHandlers.AttachAuthorization.Descriptor.Order));
                options.AddEventHandler<OpenIddictServerEvents.ValidateDeviceAuthorizationRequestContext>(builder =>
                    builder.UseInlineHandler(context =>
                    {
                        if (!ExactResource(context.Request, resource)) context.Reject(Errors.InvalidTarget);
                        else if (context.ClientId != ClientId) context.Reject(Errors.InvalidClient);
                        else if (!AllowedScopes(context.Request.GetScopes())) context.Reject(Errors.InvalidScope);
                        return default;
                    }).SetOrder(int.MinValue + 100));
                options.AddEventHandler<OpenIddictServerEvents.HandleDeviceAuthorizationRequestContext>(builder =>
                    builder.UseInlineHandler(context =>
                    {
                        // Ownerless pending principal. Request parameters never supply identity.
                        context.Principal = new ClaimsPrincipal(new ClaimsIdentity());
                        context.Principal.SetScopes(context.Request.GetScopes()).SetResources(resource.Resource.AbsoluteUri);
                        return default;
                    }));
                options.AddEventHandler<OpenIddictServerEvents.ValidateTokenRequestContext>(builder =>
                    builder.UseInlineHandler(context =>
                    {
                        if (!ExactResource(context.Request, resource)) context.Reject(Errors.InvalidTarget);
                        else if (context.ClientId != ClientId) context.Reject(Errors.InvalidClient);
                        return default;
                    }).SetOrder(int.MinValue + 100));
                options.AddEventHandler<OpenIddictServerEvents.HandleTokenRequestContext>(builder =>
                    builder.UseInlineHandler(context =>
                    {
                        if (context.Principal is not { } principal
                            || !principal.GetResources().SequenceEqual([resource.Resource.AbsoluteUri])
                            || !AllowedScopes(principal.GetScopes())) context.Reject(Errors.InvalidGrant);
                        // OpenIddict also enforces no scope widening on redemption/refresh.
                        return default;
                    }).SetOrder(int.MaxValue - 100));
                options.AddEventHandler<OpenIddictServerEvents.ApplyDeviceAuthorizationResponseContext>(builder =>
                    builder.UseInlineHandler(context =>
                    {
                        if (context.Response.Error is null) context.Response["interval"] = 5;
                        Sanitize(context.Response);
                        return default;
                    }).SetOrder(int.MinValue + 100));
                options.AddEventHandler<OpenIddictServerEvents.ApplyTokenResponseContext>(builder =>
                    builder.UseInlineHandler(context => { Sanitize(context.Response); return default; })
                        .SetOrder(int.MinValue + 100));
            });
        return services;
    }

    /// <summary>Wraps protocol processing before authentication with bounded, sanitized admission.</summary>
    public static IApplicationBuilder UseMcpDeviceProtocol(this IApplicationBuilder app) =>
        app.UseMiddleware<McpProtocolMiddleware>();

    /// <summary>Installs the explicit closed consent/redemption gate; no usable grant can issue.</summary>
    public static void MapClosedMcpConsent(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMethods(VerifyPath, ["GET", "POST"], () => Results.StatusCode(403));
        endpoints.MapPost(TokenPath, () => Results.Forbid(
            new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.AccessDenied
            }), [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]));
    }

    /// <summary>Creates the public device/refresh registration, rejecting incompatible persisted trust.</summary>
    public static async Task RegisterMcpDeviceClientAsync(this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        var manager = services.GetRequiredService<IOpenIddictApplicationManager>();
        var resource = services.GetRequiredService<McpRestResource>();
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = ClientId, ClientType = ClientTypes.Public, ConsentType = ConsentTypes.Explicit,
            Permissions =
            {
                Permissions.Endpoints.DeviceAuthorization, Permissions.Endpoints.Token,
                Permissions.GrantTypes.DeviceCode, Permissions.GrantTypes.RefreshToken,
                Permissions.Prefixes.Scope + McpPermissions.LibraryRead,
                Permissions.Prefixes.Scope + McpPermissions.TrackingWrite,
                Permissions.Prefixes.Resource + resource.Resource.AbsoluteUri
            }
        };
        var existing = await manager.FindByClientIdAsync(ClientId, cancellationToken);
        if (existing is null) await manager.CreateAsync(descriptor, cancellationToken);
        else
        {
            var current = new OpenIddictApplicationDescriptor();
            await manager.PopulateAsync(current, existing, cancellationToken);
            if (current.ClientType != descriptor.ClientType || current.ConsentType != descriptor.ConsentType
                || current.ClientSecret is not null || current.RedirectUris.Count != 0
                || current.PostLogoutRedirectUris.Count != 0 || current.Requirements.Count != 0
                || current.Settings.Count != 0 || current.JsonWebKeySet is not null
                || !current.Permissions.SetEquals(descriptor.Permissions))
                throw new InvalidOperationException("The stored MCP client registration is incompatible.");
        }
    }

    private static bool ExactResource(OpenIddictRequest request, McpRestResource resource) =>
        (ImmutableArray<string?>)request[Parameters.Resource] is [var value]
        && value == resource.Resource.AbsoluteUri;

    private static bool AllowedScopes(ImmutableArray<string> scopes) => scopes.Contains(McpPermissions.LibraryRead)
        && scopes.All(scope => scope is McpPermissions.LibraryRead or McpPermissions.TrackingWrite or Scopes.OfflineAccess);

    private static void Sanitize(OpenIddictResponse response)
    {
        response.ErrorDescription = null;
        response.ErrorUri = null;
    }
}
