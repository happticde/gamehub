using System.Collections.Immutable;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using GameHub.Core.Data;
using GameHub.Core.Mcp;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace GameHub.Core.Services;

/// <summary>Independent persistent protocol keys with retained decryptors and reloadable managed state.</summary>
public sealed class McpProtocolKeys : IDisposable
{
    private readonly ReloadableProvider provider = new();
    private volatile KeySnapshot snapshot = null!;
    private readonly List<IDisposable> owned = [];
    internal IDataProtectionProvider Provider => provider;
    internal X509Certificate2 Signing => snapshot.Signing[^1];
    internal X509Certificate2 Encryption => snapshot.Encryption[^1];
    internal IReadOnlyList<X509Certificate2> SigningCertificates => snapshot.Signing;
    internal IReadOnlyList<X509Certificate2> EncryptionCertificates => snapshot.Encryption;

    /// <summary>Requires an existing dedicated ring and three distinct, valid RSA private certificates.</summary>
    public McpProtocolKeys(DirectoryInfo directory, X509Certificate2 signing,
        X509Certificate2 encryption, X509Certificate2 protection, params X509Certificate2[] retiredProtection)
    {
        if (!directory.Exists || directory.LinkTarget is not null)
            throw new ArgumentException("An existing dedicated protocol key directory is required.");
        var certificates = new[] { signing, encryption, protection };
        if (certificates.Select(certificate => Convert.ToHexString(certificate.GetPublicKey())).Distinct().Count() != 3
            || certificates.Any(certificate => !ValidCertificate(certificate)))
            throw new ArgumentException("Independent valid RSA protocol certificates are required.");
        Set(directory, [signing], [encryption], [.. retiredProtection, protection], managed: false);
    }

    private McpProtocolKeys() { }

    internal static McpProtocolKeys Managed(DirectoryInfo directory, X509Certificate2[] signing,
        X509Certificate2[] encryption, X509Certificate2[] protection)
    {
        var keys = new McpProtocolKeys();
        if (new[] { signing[^1], encryption[^1], protection[^1] }.Any(certificate => !ValidCertificate(certificate)))
            throw new McpKeyStoreException("expired_certificate");
        keys.Set(directory, signing, encryption, protection, managed: true);
        return keys;
    }

    private void Set(DirectoryInfo directory, X509Certificate2[] signing, X509Certificate2[] encryption,
        X509Certificate2[] protection, bool managed)
    {
        var currentProvider = DataProtectionProvider.Create(directory, options =>
        {
            options.SetApplicationName("GameHub.Mcp.Protocol.v1")
                .ProtectKeysWithCertificate(protection[^1]).UnprotectKeysWithAnyCertificate(protection);
            if (managed) options.Services.Configure<KeyManagementOptions>(settings =>
                settings.XmlRepository = new McpPrivateXmlRepository(directory.FullName));
        });
        snapshot = new(signing, encryption);
        Volatile.Write(ref provider.Current, currentProvider);
        if (currentProvider is IDisposable disposable) owned.Add(disposable);
        if (managed) owned.AddRange(signing.Concat(encryption).Concat(protection));
    }

    /// <summary>Switches managed issuance without dropping historical payload decryptors.</summary>
    internal void Reload(McpProtocolKeys replacement)
    {
        snapshot = replacement.snapshot;
        Volatile.Write(ref provider.Current, replacement.provider.Current);
        owned.AddRange(replacement.owned);
        replacement.owned.Clear();
    }

    internal void FailClosed() => Volatile.Write(ref provider.Current, new UnavailableProvider());

    private sealed class UnavailableProvider : IDataProtectionProvider
    {
        public IDataProtector CreateProtector(string purpose) => throw new McpKeyStoreException("key_store_unavailable");
    }

    /// <summary>Releases owned managed certificates only when the host no longer serves requests.</summary>
    public void Dispose() { foreach (var item in owned) item.Dispose(); owned.Clear(); }

    private sealed record KeySnapshot(X509Certificate2[] Signing, X509Certificate2[] Encryption);
    private sealed class ReloadableProvider : IDataProtectionProvider
    {
        internal IDataProtectionProvider Current = null!;
        public IDataProtector CreateProtector(string purpose) => new Protector(this, [purpose]);
        private sealed class Protector(ReloadableProvider owner, string[] purposes) : IDataProtector
        {
            public IDataProtector CreateProtector(string purpose) => new Protector(owner, [.. purposes, purpose]);
            private IDataProtector Resolve()
            {
                var current = Volatile.Read(ref owner.Current).CreateProtector(purposes[0]);
                foreach (var purpose in purposes.Skip(1)) current = current.CreateProtector(purpose);
                return current;
            }
            public byte[] Protect(byte[] plaintext) => Resolve().Protect(plaintext);
            public byte[] Unprotect(byte[] protectedData) => Resolve().Unprotect(protectedData);
        }
    }

    private static bool ValidCertificate(X509Certificate2 certificate)
    {
        using var rsa = certificate.GetRSAPublicKey();
        return certificate.HasPrivateKey && certificate.NotBefore.ToUniversalTime() <= DateTime.UtcNow
            && certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow && rsa is { KeySize: >= 2048 };
    }
}

/// <summary>RFC 8628 machinery behind independently validated default-off deployment trust.</summary>
public static class McpDeviceProtocol
{
    /// <summary>Public software identifier; it conveys no account, tenant or device trust.</summary>
    public const string ClientId = "gamehub-mcp-stdio";
    /// <summary>Device request endpoint, routed directly to Core when the consent gate is delivered.</summary>
    public const string DevicePath = "/connect/device";
    /// <summary>Device redemption and integration refresh endpoint.</summary>
    public const string TokenPath = "/connect/token";
    /// <summary>Client credential revocation, closing only its exact connection.</summary>
    public const string RevocationPath = "/connect/revoke";
    /// <summary>Internal BFF consent endpoint; never a public browser page.</summary>
    public const string VerifyPath = "/connect/verify";
    /// <summary>Short absolute pending lifetime, independent of eventual grant lifetime.</summary>
    public const int LifetimeSeconds = 600;
    /// <summary>Maximum live pending requests across the protocol database.</summary>
    public const int MaximumPending = 256;

    /// <summary>
    /// Registers unmodified OpenIddict processing and GameHub admission policy. Call only with
    /// independently validated deployment trust; Core startup calls it only when explicitly enabled.
    /// </summary>
    public static IServiceCollection AddMcpDeviceProtocol(this IServiceCollection services,
        McpRestResource resource, McpProtocolKeys keys, bool browserConsent = false, bool remoteAuthorization = false)
    {
        foreach (var uri in new[] { resource.Issuer, resource.Resource })
            if (!uri.IsAbsoluteUri || uri.Scheme != "https" || uri.UserInfo.Length != 0
                || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new ArgumentException("Exact HTTPS protocol identifiers are required.");
        if (resource.Issuer.AbsolutePath != "/")
            throw new ArgumentException("The staged device protocol requires a root HTTPS issuer.");
        services.AddSingleton(resource);
        services.AddSingleton(_ => keys);
        services.AddSingleton<McpProtocolBudget>();
        services.AddScoped<McpPollingAdmission>();
        services.AddHttpContextAccessor();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<McpGrantAdmission>();
        var remote = remoteAuthorization ? McpRemoteResource.Create(resource) : null;
        if (remote is not null) McpRemoteAuthorization.Register(services, remote);
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
                options.SetRevocationEndpointUris(RevocationPath);
                options.SetEndUserVerificationEndpointUris(VerifyPath);
                options.AllowDeviceAuthorizationFlow().AllowRefreshTokenFlow();
                options.RegisterScopes(McpPermissions.LibraryRead, McpPermissions.TrackingWrite);
                options.RegisterResources(resource.Resource);
                options.SetDeviceCodeLifetime(TimeSpan.FromSeconds(LifetimeSeconds));
                options.SetUserCodeLifetime(TimeSpan.FromSeconds(LifetimeSeconds));
                options.SetAccessTokenLifetime(TimeSpan.FromMinutes(10));
                options.SetRefreshTokenLifetime(TimeSpan.FromDays(30));
                options.SetRefreshTokenReuseLeeway(null);
                options.DisableSlidingRefreshTokenExpiration();
                options.UseReferenceAccessTokens().UseReferenceRefreshTokens();
                foreach (var certificate in keys.SigningCertificates) options.AddSigningCertificate(certificate);
                foreach (var certificate in keys.EncryptionCertificates) options.AddEncryptionCertificate(certificate);
                options.UseDataProtection().Configure(configuration => configuration.DataProtectionProvider = keys.Provider);
                options.UseAspNetCore().EnableTokenEndpointPassthrough().EnableEndUserVerificationEndpointPassthrough();
                if (remote is not null) McpRemoteAuthorization.Configure(options, resource.Issuer, remote);
                // Keep native credential/client validation; replace only the single-token mutation
                // with GameHub's connection-wide transaction and ownership admission.
                options.RemoveEventHandler(OpenIddictServerHandlers.Revocation.RevokeToken.Descriptor);
                options.AddEventHandler<OpenIddictServerEvents.HandleRevocationRequestContext>(builder => builder
                    .UseScopedHandler<McpClientRevocation>()
                    .SetOrder(OpenIddictServerHandlers.Revocation.RevokeToken.Descriptor.Order));
                options.AddEventHandler<OpenIddictServerEvents.ApplyRevocationResponseContext>(builder => builder
                    .UseInlineHandler(context => { Sanitize(context.Response); return default; })
                    .SetOrder(int.MinValue + 100));
                options.AddEventHandler<OpenIddictServerEvents.ProcessSignInContext>(builder => builder
                    .UseScopedHandler<McpTokenIssuance>()
                    .SetOrder(OpenIddictServerHandlers.RedeemTokenEntry.Descriptor.Order - 500));
                options.AddEventHandler<OpenIddictServerEvents.ProcessSignInContext>(builder => builder
                    .UseScopedHandler<McpTokenExpiry>()
                    .SetOrder(OpenIddictServerHandlers.PrepareRefreshTokenPrincipal.Descriptor.Order + 500));
                options.AddEventHandler<OpenIddictServerEvents.ValidateTokenContext>(builder => builder
                    .UseScopedHandler<McpRefreshReuse>()
                    .SetOrder(OpenIddictServerHandlers.Protection.ValidateTokenEntry.Descriptor.Order - 500));
                // GameHub authorizations require an owned independent family. Pending requests
                // have no owner; consent must explicitly persist their authorization in #71.
                // Replace only automatic ad-hoc creation, never token/authorization validation.
                options.RemoveEventHandler(OpenIddictServerHandlers.AttachAuthorization.Descriptor);
                options.AddEventHandler<OpenIddictServerEvents.ProcessSignInContext>(builder =>
                    builder.UseInlineHandler(context =>
                    {
                        if (context.EndpointType != OpenIddictServerEndpointType.DeviceAuthorization
                            && !(remote is not null && context.EndpointType == OpenIddictServerEndpointType.Authorization
                                && context.GenerateRequestToken && !context.GenerateAuthorizationCode)
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
                        var expected = McpRemoteResource.ForClient(context.ClientId, resource, remote);
                        if (expected is null) context.Reject(Errors.InvalidClient);
                        else if (!ExactResource(context.Request, expected)) context.Reject(Errors.InvalidTarget);
                        else if (context.ClientId == ClientId ? context.Request.IsAuthorizationCodeGrantType()
                            : !(context.Request.IsAuthorizationCodeGrantType() || context.Request.IsRefreshTokenGrantType()))
                            context.Reject(Errors.UnauthorizedClient);
                        return default;
                    }).SetOrder(int.MinValue + 100));
                options.AddEventHandler<OpenIddictServerEvents.HandleTokenRequestContext>(builder =>
                    builder.UseInlineHandler(context =>
                    {
                        if (context.Principal is not { } principal
                            || McpRemoteResource.ForClient(context.ClientId, resource, remote) is not { } expected
                            || !principal.GetResources().SequenceEqual([expected])
                            || !AllowedScopes(principal.GetScopes())) context.Reject(Errors.InvalidGrant);
                        // OpenIddict also enforces no scope widening on redemption/refresh.
                        return default;
                    }).SetOrder(int.MaxValue - 100));
                options.AddEventHandler<OpenIddictServerEvents.ApplyDeviceAuthorizationResponseContext>(builder =>
                    builder.UseInlineHandler(context =>
                    {
                        if (context.Response.Error is null)
                        {
                            context.Response["interval"] = 5;
                            // Manual entry keeps the human code out of browser URLs and logs.
                            if (browserConsent)
                            {
                                context.Response.VerificationUri = new Uri(resource.Issuer, "/mcp/verify").AbsoluteUri;
                                context.Response.VerificationUriComplete = null;
                            }
                        }
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
                Permissions.Endpoints.DeviceAuthorization, Permissions.Endpoints.Token, Permissions.Endpoints.Revocation,
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
            var previousPermissions = descriptor.Permissions.Where(permission => permission != Permissions.Endpoints.Revocation);
            var upgrade = current.Permissions.SetEquals(previousPermissions);
            if (current.ClientType != descriptor.ClientType || current.ConsentType != descriptor.ConsentType
                || current.ClientSecret is not null || current.RedirectUris.Count != 0
                || current.PostLogoutRedirectUris.Count != 0 || current.Requirements.Count != 0
                || current.Settings.Count != 0 || current.JsonWebKeySet is not null
                || (!upgrade && !current.Permissions.SetEquals(descriptor.Permissions)))
                throw new InvalidOperationException("The stored MCP client registration is incompatible.");
            if (upgrade)
            {
                current.Permissions.Add(Permissions.Endpoints.Revocation);
                await manager.UpdateAsync(existing, current, cancellationToken);
            }
        }
    }

    private static bool ExactResource(OpenIddictRequest request, McpRestResource resource) =>
        ExactResource(request, resource.Resource.AbsoluteUri);

    internal static bool ExactResource(OpenIddictRequest request, string resource) =>
        (ImmutableArray<string?>)request[Parameters.Resource] is [var value]
        && value == resource;

    internal static bool AllowedScopes(ImmutableArray<string> scopes) => scopes.Contains(McpPermissions.LibraryRead)
        && scopes.All(scope => scope is McpPermissions.LibraryRead or McpPermissions.TrackingWrite or Scopes.OfflineAccess);

    private static void Sanitize(OpenIddictResponse response)
    {
        response.ErrorDescription = null;
        response.ErrorUri = null;
    }
}
