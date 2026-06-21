using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace GameHub.Core.Services;

/// <summary>Validated, provider-neutral OIDC configuration for server-side ID-token checks.</summary>
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
    /// </summary>
    public static OidcSettings FromConfiguration(IConfiguration configuration, JwtSettings jwtSettings)
    {
        var section = configuration.GetSection(SectionName);
        var enabled = section.GetValue("Enabled", false);
        if (!enabled)
        {
            return new OidcSettings(false, "", "", "", [], "");
        }

        if (!jwtSettings.IsEnabled)
        {
            throw new InvalidOperationException(
                $"{SectionName} requires Authentication:Jwt:SigningKey for GameHub sessions.");
        }

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

/// <summary>Fetches and caches provider discovery metadata and signing keys.</summary>
public interface IOidcMetadataProvider
{
    /// <summary>Gets current discovery metadata, throwing when the provider is unavailable.</summary>
    Task<OpenIdConnectConfiguration> GetAsync(CancellationToken cancellationToken);

    /// <summary>Requests a metadata refresh after a signing-key miss.</summary>
    void RequestRefresh();
}

/// <summary>Microsoft.IdentityModel-backed discovery and JWKS provider.</summary>
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

    /// <summary>Validates signature, issuer, audience, lifetime, algorithm, subject, and nonce.</summary>
    public async Task<OidcTokenValidationResult> ValidateAsync(
        string? idToken,
        string? expectedNonce,
        CancellationToken cancellationToken = default)
    {
        if (!_settings.IsEnabled)
        {
            return OidcTokenValidationResult.ConfigurationUnavailable;
        }

        if (string.IsNullOrWhiteSpace(idToken)
            || idToken.Length > MaximumIdTokenLength
            || string.IsNullOrWhiteSpace(expectedNonce)
            || expectedNonce.Length is < 32 or > 128)
        {
            return OidcTokenValidationResult.Invalid;
        }

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

        if (!string.Equals(configuration.Issuer, _settings.Issuer, StringComparison.Ordinal)
            || configuration.SigningKeys.Count == 0)
        {
            _logger.LogWarning("OIDC discovery metadata did not match the configured issuer or contained no signing keys");
            return OidcTokenValidationResult.Invalid;
        }

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
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var principal = handler.ValidateToken(idToken, parameters, out _);
            var issuer = principal.FindFirstValue(JwtRegisteredClaimNames.Iss);
            var subject = principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
            var nonce = principal.FindFirstValue("nonce");
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

/// <summary>Stable provider identity after all token checks; Email is display-only metadata.</summary>
public sealed record ValidatedOidcIdentity(string Issuer, string Subject, string? Email);

/// <summary>Outcome category used to distinguish invalid input from provider availability.</summary>
public enum OidcTokenValidationStatus
{
    Succeeded,
    Invalid,
    ProviderUnavailable,
    ConfigurationUnavailable,
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
