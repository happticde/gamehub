using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace GameHub.Core.Services;

/// <summary>
/// Validated, provider-neutral OIDC trust configuration used by Core.
/// This is intentionally separate from the BFF configuration: both components read the same
/// issuer/client trust anchors, but only Core decides whether an ID token represents a GameHub
/// identity and only Core can issue a tenant-scoped GameHub session.
/// </summary>
/// <param name="IsEnabled">Explicit feature switch; disabled preserves the local compatibility path.</param>
/// <param name="Issuer">Exact provider identifier required in discovery metadata and the ID token.</param>
/// <param name="Audience">GameHub provider client ID required in the ID token audience.</param>
/// <param name="MetadataAddress">HTTPS discovery document used to obtain issuer metadata and JWKS.</param>
/// <param name="AllowedAlgorithms">Closed asymmetric signature-algorithm allowlist.</param>
/// <param name="BffServiceKey">Independent credential authorizing only the server-side BFF exchange.</param>
public sealed record OidcSettings(
    bool IsEnabled,
    string Issuer,
    string Audience,
    string MetadataAddress,
    IReadOnlyList<string> AllowedAlgorithms,
    string BffServiceKey)
{
    /// <summary>Configuration section containing production OIDC trust settings.</summary>
    public const string SectionName = "Authentication:Oidc";

    // Do not accept an arbitrary configuration string as a valid algorithm. This outer allowlist
    // excludes `none` and symmetric algorithms before the deployment-specific subset is parsed.
    private static readonly HashSet<string> SupportedAsymmetricAlgorithms = new(StringComparer.Ordinal)
    {
        SecurityAlgorithms.RsaSha256,
        SecurityAlgorithms.RsaSha384,
        SecurityAlgorithms.RsaSha512,
        SecurityAlgorithms.RsaSsaPssSha256,
        SecurityAlgorithms.RsaSsaPssSha384,
        SecurityAlgorithms.RsaSsaPssSha512,
        SecurityAlgorithms.EcdsaSha256,
        SecurityAlgorithms.EcdsaSha384,
        SecurityAlgorithms.EcdsaSha512
    };

    /// <summary>
    /// Parses OIDC configuration at startup. Enabling the feature with missing, non-HTTPS,
    /// symmetric, or incomplete trust material aborts startup instead of weakening validation.
    /// Configuration errors are therefore deployment failures, not per-request authentication
    /// branches that might accidentally fall back to a less trusted mode.
    /// </summary>
    public static OidcSettings FromConfiguration(IConfiguration configuration, JwtSettings jwtSettings)
    {
        var section = configuration.GetSection(SectionName);
        var enabled = section.GetValue("Enabled", false);
        if (!enabled)
        {
            // Disabled is the only state in which the remaining OIDC values may be absent.
            return new OidcSettings(false, "", "", "", [], "");
        }

        if (!jwtSettings.IsEnabled)
        {
            throw new InvalidOperationException(
                $"{SectionName} requires Authentication:Jwt:SigningKey for GameHub sessions.");
        }

        // Issuer is an exact security identifier. It is validated as a URL but deliberately not
        // normalized (for example by silently adding/removing a trailing slash).
        var issuer = section["Issuer"]?.Trim() ?? "";
        if (!IsSecureAbsoluteUri(issuer, out _))
        {
            throw new InvalidOperationException($"{SectionName}:Issuer must be an absolute HTTPS URI.");
        }

        var audience = section["Audience"]?.Trim() ?? "";
        if (string.IsNullOrEmpty(audience) || audience.Length > 512)
        {
            throw new InvalidOperationException($"{SectionName}:Audience is required and must not exceed 512 characters.");
        }

        // A custom metadata address supports provider-specific deployments while the issuer
        // remains fixed. Metadata still has to assert the configured issuer later.
        var metadataAddress = section["MetadataAddress"]?.Trim();
        if (string.IsNullOrEmpty(metadataAddress))
        {
            metadataAddress = $"{issuer.TrimEnd('/')}/.well-known/openid-configuration";
        }
        if (!IsSecureAbsoluteUri(metadataAddress, out _))
        {
            throw new InvalidOperationException(
                $"{SectionName}:MetadataAddress must be an absolute HTTPS URI.");
        }

        // The deployment chooses a subset (normally RS256); no algorithm is inferred from a token.
        var configuredAlgorithms = (section["AllowedAlgorithms"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (configuredAlgorithms.Length == 0
            || configuredAlgorithms.Any(algorithm => !SupportedAsymmetricAlgorithms.Contains(algorithm)))
        {
            throw new InvalidOperationException(
                $"{SectionName}:AllowedAlgorithms must contain only explicitly supported asymmetric algorithms.");
        }

        // Printable ASCII makes the key safe to carry in a dedicated internal HTTP header. It is
        // an independent trust boundary and must not reuse the cookie key or provider secret.
        var bffServiceKey = section["BffServiceKey"]?.Trim() ?? "";
        if (bffServiceKey.Length is < 32 or > 512
            || bffServiceKey.Any(character => character is < '!' or > '~'))
        {
            throw new InvalidOperationException(
                $"{SectionName}:BffServiceKey must contain 32-512 printable ASCII characters.");
        }

        return new OidcSettings(
            true,
            issuer,
            audience,
            metadataAddress,
            configuredAlgorithms,
            bffServiceKey);
    }

    private static bool IsSecureAbsoluteUri(string value, out Uri? uri)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            uri = null;
            return false;
        }

        return true;
    }
}

/// <summary>
/// Fetches and caches provider discovery metadata and signing keys. The abstraction lets tests
/// provide deterministic keys or simulated outages without weakening production validation.
/// </summary>
public interface IOidcMetadataProvider
{
    /// <summary>Gets current discovery metadata, throwing when the provider is unavailable.</summary>
    Task<OpenIdConnectConfiguration> GetAsync(CancellationToken cancellationToken);

    /// <summary>Requests a metadata refresh after a signing-key miss.</summary>
    void RequestRefresh();
}

/// <summary>
/// Microsoft.IdentityModel-backed discovery and JWKS provider.
/// <see cref="ConfigurationManager{T}"/> caches successful metadata, automatically respects its
/// refresh intervals, and can be explicitly refreshed when a valid provider rotates signing keys.
/// </summary>
public sealed class OidcMetadataProvider : IOidcMetadataProvider, IDisposable
{
    private readonly ConfigurationManager<OpenIdConnectConfiguration>? _configurationManager;
    private readonly HttpClient? _httpClient;

    public OidcMetadataProvider(OidcSettings settings)
    {
        if (!settings.IsEnabled)
        {
            return;
        }

        // A bounded timeout turns provider failure into a closed, observable login failure instead
        // of tying up request resources for the much longer HttpClient default timeout.
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _configurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            settings.MetadataAddress,
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever(_httpClient) { RequireHttps = true });
    }

    /// <inheritdoc />
    public Task<OpenIdConnectConfiguration> GetAsync(CancellationToken cancellationToken) =>
        _configurationManager?.GetConfigurationAsync(cancellationToken)
        ?? throw new InvalidOperationException("OIDC metadata is disabled.");

    /// <inheritdoc />
    public void RequestRefresh() => _configurationManager?.RequestRefresh();

    /// <inheritdoc />
    public void Dispose() => _httpClient?.Dispose();
}

/// <summary>
/// Validates provider ID tokens without trusting browser, email, tenant, account, or role claims.
/// Only the configured issuer and the provider-stable subject leave this trust boundary.
///
/// The BFF has already validated redirect protocol state, but Core repeats every check needed to
/// trust the token itself. This defense-in-depth separation prevents a compromised/misconfigured
/// BFF from manufacturing account or tenant claims.
/// </summary>
public sealed class OidcTokenValidator
{
    private const int MaximumIdTokenLength = 32 * 1024;
    private readonly OidcSettings _settings;
    private readonly IOidcMetadataProvider _metadata;
    private readonly ILogger<OidcTokenValidator> _logger;

    public OidcTokenValidator(
        OidcSettings settings,
        IOidcMetadataProvider metadata,
        ILogger<OidcTokenValidator> logger)
    {
        _settings = settings;
        _metadata = metadata;
        _logger = logger;
    }

    /// <summary>
    /// Validates signature, issuer, audience, lifetime, algorithm, subject, and nonce.
    /// Provider availability and invalid-token outcomes remain distinct internally so the API can
    /// return 503 for infrastructure failure and a generic 401 for untrusted identity proof.
    /// </summary>
    public async Task<OidcTokenValidationResult> ValidateAsync(
        string? idToken,
        string? expectedNonce,
        CancellationToken cancellationToken = default)
    {
        // Gate 1: no token processing occurs unless the complete configuration passed startup checks.
        if (!_settings.IsEnabled)
        {
            return OidcTokenValidationResult.ConfigurationUnavailable;
        }

        // Gate 2: bound attacker-controlled work before parsing JSON or performing cryptography.
        if (string.IsNullOrWhiteSpace(idToken)
            || idToken.Length > MaximumIdTokenLength
            || string.IsNullOrWhiteSpace(expectedNonce)
            || expectedNonce.Length is < 32 or > 128)
        {
            return OidcTokenValidationResult.Invalid;
        }

        // Gate 3: obtain cached provider metadata/JWKS over HTTPS. An outage never causes fallback
        // to unsigned claims, email lookup, or a locally supplied signing key.
        OpenIdConnectConfiguration configuration;
        try
        {
            configuration = await _metadata.GetAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "OIDC discovery or signing keys are unavailable");
            return OidcTokenValidationResult.ProviderUnavailable;
        }

        // Discovery is data, not a trust anchor. Pin its issuer to explicit local configuration
        // and require at least one signing key before parsing a token.
        if (!string.Equals(configuration.Issuer, _settings.Issuer, StringComparison.Ordinal)
            || configuration.SigningKeys.Count == 0)
        {
            _logger.LogWarning("OIDC discovery metadata did not match the configured issuer or contained no signing keys");
            return OidcTokenValidationResult.Invalid;
        }

        // Gate 4: validate the token against the current metadata snapshot.
        var validation = ValidateWithConfiguration(idToken, expectedNonce, configuration);
        if (validation.Status != OidcTokenValidationStatus.SigningKeyUnavailable)
        {
            return validation;
        }

        // A provider may rotate keys between cached discovery responses. Refresh exactly once;
        // every other failure stays closed and does not trigger unbounded provider traffic.
        try
        {
            _metadata.RequestRefresh();
            configuration = await _metadata.GetAsync(cancellationToken);
            return ValidateWithConfiguration(idToken, expectedNonce, configuration);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "OIDC signing-key refresh failed");
            return OidcTokenValidationResult.ProviderUnavailable;
        }
    }

    private OidcTokenValidationResult ValidateWithConfiguration(
        string idToken,
        string expectedNonce,
        OpenIdConnectConfiguration configuration)
    {
        try
        {
            // These parameters intentionally turn on every relevant validation switch instead of
            // relying on library defaults that may change between framework versions.
            var parameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = configuration.SigningKeys,
                RequireSignedTokens = true,
                ValidateIssuer = true,
                ValidIssuer = _settings.Issuer,
                ValidateAudience = true,
                ValidAudience = _settings.Audience,
                RequireExpirationTime = true,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(1),
                ValidAlgorithms = _settings.AllowedAlgorithms,
                NameClaimType = ClaimTypes.Name
            };
            // Keep original OIDC claim names (`iss`, `sub`, `aud`, `nonce`, `azp`). Automatic .NET
            // claim mapping would make the security checks harder to audit against the standard.
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var principal = handler.ValidateToken(idToken, parameters, out _);
            var issuer = principal.FindFirstValue(JwtRegisteredClaimNames.Iss);
            var subject = principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
            var nonce = principal.FindFirstValue("nonce");
            // OIDC requires `azp` when more than one audience exists. If supplied for a single
            // audience, it must still identify this configured GameHub client.
            var audiences = principal.FindAll(JwtRegisteredClaimNames.Aud).Select(claim => claim.Value).ToArray();
            var authorizedParty = principal.FindFirstValue("azp");
            var authorizedPartyIsValid = audiences.Length <= 1
                ? authorizedParty is null || string.Equals(authorizedParty, _settings.Audience, StringComparison.Ordinal)
                : string.Equals(authorizedParty, _settings.Audience, StringComparison.Ordinal);
            if (!string.Equals(issuer, _settings.Issuer, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(subject)
                || subject.Length > 255
                || !string.Equals(nonce, expectedNonce, StringComparison.Ordinal)
                || !authorizedPartyIsValid)
            {
                return OidcTokenValidationResult.Invalid;
            }

            // Deliberately project a narrow value. Provider tenant, organization, group, role, and
            // email-verification claims cannot accidentally flow into authorization code later.
            return OidcTokenValidationResult.Succeeded(new ValidatedOidcIdentity(
                _settings.Issuer,
                subject,
                principal.FindFirstValue(JwtRegisteredClaimNames.Email)));
        }
        catch (SecurityTokenSignatureKeyNotFoundException)
        {
            return OidcTokenValidationResult.SigningKeyUnavailable;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogInformation("Rejected OIDC ID token: {ExceptionType}", exception.GetType().Name);
            return OidcTokenValidationResult.Invalid;
        }
    }
}

/// <summary>
/// Stable provider identity after all cryptographic/protocol token checks.
/// <paramref name="Email"/> is display-only metadata and is never used to find, create, or link an
/// account. Authorization identity is exclusively the exact <c>(Issuer, Subject)</c> pair.
/// </summary>
/// <param name="Issuer">Exact validated OIDC issuer.</param>
/// <param name="Subject">Provider-stable subject scoped to that issuer.</param>
/// <param name="Email">Optional untrusted-for-linking display claim.</param>
public sealed record ValidatedOidcIdentity(string Issuer, string Subject, string? Email);

/// <summary>Outcome category used to distinguish invalid input from provider availability.</summary>
public enum OidcTokenValidationStatus
{
    /// <summary>All token checks passed and a stable identity is available.</summary>
    Succeeded,
    /// <summary>The proof is malformed, expired, mismatched, or cryptographically invalid.</summary>
    Invalid,
    /// <summary>Discovery or signing keys could not be obtained from the provider.</summary>
    ProviderUnavailable,
    /// <summary>OIDC is disabled; no external token may be accepted.</summary>
    ConfigurationUnavailable,
    /// <summary>The token key ID was not cached; the caller may force one bounded metadata refresh.</summary>
    SigningKeyUnavailable
}

/// <summary>Result of validating one provider ID token.</summary>
public sealed record OidcTokenValidationResult(
    OidcTokenValidationStatus Status,
    ValidatedOidcIdentity? Identity)
{
    public static OidcTokenValidationResult Succeeded(ValidatedOidcIdentity identity) =>
        new(OidcTokenValidationStatus.Succeeded, identity);

    public static OidcTokenValidationResult Invalid { get; } =
        new(OidcTokenValidationStatus.Invalid, null);

    public static OidcTokenValidationResult ProviderUnavailable { get; } =
        new(OidcTokenValidationStatus.ProviderUnavailable, null);

    public static OidcTokenValidationResult ConfigurationUnavailable { get; } =
        new(OidcTokenValidationStatus.ConfigurationUnavailable, null);

    public static OidcTokenValidationResult SigningKeyUnavailable { get; } =
        new(OidcTokenValidationStatus.SigningKeyUnavailable, null);
}
