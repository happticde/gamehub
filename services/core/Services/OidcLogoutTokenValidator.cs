using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace GameHub.Core.Services;

/// <summary>
/// Validates OpenID Connect Back-Channel Logout Tokens independently from ID tokens. Logout
/// tokens deliberately have no nonce or required expiry, so reusing the login-token validator
/// would either reject valid provider messages or weaken the login boundary.
/// </summary>
public sealed class OidcLogoutTokenValidator
{
    private const int MaximumLogoutTokenLength = 32 * 1024;
    private const string LogoutTokenType = "logout+jwt";
    private const string BackChannelLogoutEvent =
        "http://schemas.openid.net/event/backchannel-logout";
    private static readonly TimeSpan MaximumTokenAge = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FutureClockSkew = TimeSpan.FromMinutes(1);

    private readonly OidcSettings _settings;
    private readonly IOidcMetadataProvider _metadata;
    private readonly ILogger<OidcLogoutTokenValidator> _logger;
    private readonly TimeProvider _timeProvider;

    public OidcLogoutTokenValidator(
        OidcSettings settings,
        IOidcMetadataProvider metadata,
        ILogger<OidcLogoutTokenValidator> logger,
        TimeProvider? timeProvider = null)
    {
        _settings = settings;
        _metadata = metadata;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Validates one signed provider logout message. GameHub's current account-wide logout
    /// boundary requires <c>sub</c>; an optional provider <c>sid</c> never selects a tenant or
    /// narrows revocation until durable per-provider-session mapping is introduced.
    /// </summary>
    public async Task<OidcLogoutTokenValidationResult> ValidateAsync(
        string? logoutToken,
        CancellationToken cancellationToken = default)
    {
        if (!_settings.IsEnabled)
        {
            return OidcLogoutTokenValidationResult.ConfigurationUnavailable;
        }
        if (string.IsNullOrWhiteSpace(logoutToken)
            || logoutToken.Length > MaximumLogoutTokenLength)
        {
            return OidcLogoutTokenValidationResult.Invalid;
        }

        OpenIdConnectConfiguration configuration;
        try
        {
            configuration = await _metadata.GetAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "OIDC discovery or logout signing keys are unavailable");
            return OidcLogoutTokenValidationResult.ProviderUnavailable;
        }

        if (!HasPinnedMetadata(configuration))
        {
            _logger.LogWarning("OIDC logout metadata did not match the configured issuer or contained no signing keys");
            return OidcLogoutTokenValidationResult.Invalid;
        }

        var validation = ValidateWithConfiguration(logoutToken, configuration);
        if (validation.Status != OidcLogoutTokenValidationStatus.SigningKeyUnavailable)
        {
            return validation;
        }

        try
        {
            _metadata.RequestRefresh();
            configuration = await _metadata.GetAsync(cancellationToken);
            return HasPinnedMetadata(configuration)
                ? ValidateWithConfiguration(logoutToken, configuration)
                : OidcLogoutTokenValidationResult.Invalid;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "OIDC logout signing-key refresh failed");
            return OidcLogoutTokenValidationResult.ProviderUnavailable;
        }
    }

    private bool HasPinnedMetadata(OpenIdConnectConfiguration configuration) =>
        string.Equals(configuration.Issuer, _settings.Issuer, StringComparison.Ordinal)
        && configuration.SigningKeys.Count > 0;

    private OidcLogoutTokenValidationResult ValidateWithConfiguration(
        string logoutToken,
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
                // Back-Channel Logout requires iat but does not require exp. Freshness is checked
                // explicitly below, while a persistent hashed jti marker closes replay races.
                RequireExpirationTime = false,
                ValidateLifetime = false,
                ValidAlgorithms = _settings.AllowedAlgorithms,
                NameClaimType = ClaimTypes.Name
            };
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var principal = handler.ValidateToken(logoutToken, parameters, out var securityToken);
            if (securityToken is not JwtSecurityToken jwt)
            {
                return OidcLogoutTokenValidationResult.Invalid;
            }
            // Explicit logout typing is recommended by the standard and emitted by current
            // Keycloak releases. The standard still permits an absent typ; any supplied value
            // must be the exact logout type to prevent cross-JWT confusion.
            if (jwt.Header.Typ is not null
                && !string.Equals(jwt.Header.Typ, LogoutTokenType, StringComparison.Ordinal))
            {
                return OidcLogoutTokenValidationResult.Invalid;
            }

            var issuer = principal.FindFirstValue(JwtRegisteredClaimNames.Iss);
            var subject = principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
            var jwtId = principal.FindFirstValue(JwtRegisteredClaimNames.Jti);
            var issuedAtValue = principal.FindFirstValue(JwtRegisteredClaimNames.Iat);
            var audiences = principal.FindAll(JwtRegisteredClaimNames.Aud)
                .Select(claim => claim.Value)
                .ToArray();
            var authorizedParty = principal.FindFirstValue("azp");
            var authorizedPartyIsValid = audiences.Length <= 1
                ? authorizedParty is null
                    || string.Equals(authorizedParty, _settings.Audience, StringComparison.Ordinal)
                : string.Equals(authorizedParty, _settings.Audience, StringComparison.Ordinal);
            if (!string.Equals(issuer, _settings.Issuer, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(subject)
                || subject.Length > 255
                || string.IsNullOrWhiteSpace(jwtId)
                || jwtId.Length > 512
                || !authorizedPartyIsValid
                || !long.TryParse(
                    issuedAtValue,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var issuedAtSeconds))
            {
                return OidcLogoutTokenValidationResult.Invalid;
            }

            DateTimeOffset issuedAt;
            try
            {
                issuedAt = DateTimeOffset.FromUnixTimeSeconds(issuedAtSeconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                return OidcLogoutTokenValidationResult.Invalid;
            }
            var now = _timeProvider.GetUtcNow();
            if (issuedAt > now.Add(FutureClockSkew) || now - issuedAt > MaximumTokenAge)
            {
                return OidcLogoutTokenValidationResult.Invalid;
            }

            var payloadBytes = Base64UrlEncoder.DecodeBytes(jwt.RawPayload);
            using var payload = JsonDocument.Parse(payloadBytes);
            var root = payload.RootElement;
            if (root.TryGetProperty("nonce", out _)
                || !root.TryGetProperty("events", out var events)
                || events.ValueKind != JsonValueKind.Object
                || !events.TryGetProperty(BackChannelLogoutEvent, out _))
            {
                return OidcLogoutTokenValidationResult.Invalid;
            }

            return OidcLogoutTokenValidationResult.Succeeded(
                new ValidatedOidcLogoutToken(_settings.Issuer, subject, jwtId, issuedAt));
        }
        catch (SecurityTokenSignatureKeyNotFoundException)
        {
            return OidcLogoutTokenValidationResult.SigningKeyUnavailable;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogInformation(
                "Rejected OIDC Back-Channel Logout Token: {ExceptionType}",
                exception.GetType().Name);
            return OidcLogoutTokenValidationResult.Invalid;
        }
    }
}

/// <summary>Minimal trusted logout identity projected after complete protocol validation.</summary>
public sealed record ValidatedOidcLogoutToken(
    string Issuer,
    string Subject,
    string JwtId,
    DateTimeOffset IssuedAtUtc);

/// <summary>Internal outcome category for provider logout-token validation.</summary>
public enum OidcLogoutTokenValidationStatus
{
    Succeeded,
    Invalid,
    ProviderUnavailable,
    ConfigurationUnavailable,
    SigningKeyUnavailable
}

/// <summary>Result containing identity material only after all logout-token checks succeed.</summary>
public sealed record OidcLogoutTokenValidationResult(
    OidcLogoutTokenValidationStatus Status,
    ValidatedOidcLogoutToken? Token)
{
    public static OidcLogoutTokenValidationResult Succeeded(ValidatedOidcLogoutToken token) =>
        new(OidcLogoutTokenValidationStatus.Succeeded, token);

    public static OidcLogoutTokenValidationResult Invalid { get; } =
        new(OidcLogoutTokenValidationStatus.Invalid, null);

    public static OidcLogoutTokenValidationResult ProviderUnavailable { get; } =
        new(OidcLogoutTokenValidationStatus.ProviderUnavailable, null);

    public static OidcLogoutTokenValidationResult ConfigurationUnavailable { get; } =
        new(OidcLogoutTokenValidationStatus.ConfigurationUnavailable, null);

    public static OidcLogoutTokenValidationResult SigningKeyUnavailable { get; } =
        new(OidcLogoutTokenValidationStatus.SigningKeyUnavailable, null);
}
