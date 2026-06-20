using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using GameHub.Core.Data;
using GameHub.Shared.Authentication;
using Microsoft.IdentityModel.Tokens;

namespace GameHub.Core.Services;

/// <summary>
/// Validated server configuration shared by token issuance and bearer-token validation.
/// Using one immutable object prevents the issuer and validator from accidentally using
/// different keys, issuers, audiences, or lifetimes.
/// </summary>
/// <param name="IsEnabled">True when a signing key was explicitly configured.</param>
/// <param name="Issuer">The authority that creates the token, normally this GameHub server.</param>
/// <param name="Audience">The clients for which the token is intended.</param>
/// <param name="SigningKey">Server-only HMAC secret; it is never placed inside the JWT.</param>
/// <param name="LifetimeMinutes">Access-token lifetime measured from issuance.</param>
public sealed record JwtSettings(
    bool IsEnabled,
    string Issuer,
    string Audience,
    string SigningKey,
    int LifetimeMinutes)
{
    /// <summary>Configuration section containing JWT settings.</summary>
    public const string SectionName = "Authentication:Jwt";

    /// <summary>
    /// Loads JWT settings and rejects unsafe signing configuration. JWT stays disabled
    /// when SigningKey is empty, which lets existing API-key installations upgrade without
    /// immediately provisioning another secret.
    /// </summary>
    public static JwtSettings FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        var signingKey = section["SigningKey"]?.Trim() ?? "";
        var issuer = section["Issuer"]?.Trim() ?? "GameHub";
        var audience = section["Audience"]?.Trim() ?? "GameHub.Clients";
        var lifetimeMinutes = section.GetValue("LifetimeMinutes", 60);
        // The presence of a key is the feature flag. There is no second boolean that could
        // say "enabled" while the cryptographic material is missing.
        var isEnabled = !string.IsNullOrEmpty(signingKey);

        // HS256 security depends entirely on secret entropy. A 32-byte minimum matches the
        // 256-bit output size of SHA-256 and prevents obviously weak configuration.
        if (isEnabled && Encoding.UTF8.GetByteCount(signingKey) < 32)
        {
            throw new InvalidOperationException(
                $"{SectionName}:SigningKey must contain at least 32 UTF-8 bytes.");
        }

        if (isEnabled && (string.IsNullOrEmpty(issuer) || string.IsNullOrEmpty(audience)))
        {
            throw new InvalidOperationException(
                $"{SectionName}:Issuer and Audience are required when JWT is enabled.");
        }

        // Extremely short lifetimes create clock-related login failures; extremely long
        // lifetimes increase exposure because access tokens are not revocable in this slice.
        if (lifetimeMinutes is < 5 or > 1440)
        {
            throw new InvalidOperationException(
                $"{SectionName}:LifetimeMinutes must be between 5 and 1440.");
        }

        return new JwtSettings(isEnabled, issuer, audience, signingKey, lifetimeMinutes);
    }

    /// <summary>
    /// Creates the symmetric security key used by both issuer and bearer validation.
    /// "Symmetric" means the same server secret signs and verifies a token; clients must
    /// receive only the serialized token, never this key.
    /// </summary>
    public SymmetricSecurityKey CreateSecurityKey() =>
        new(Encoding.UTF8.GetBytes(SigningKey));
}

/// <summary>
/// Issues signed access tokens for accounts whose passwords were already verified by
/// <see cref="AccountService"/>. This class does not authenticate passwords itself; keeping
/// those steps separate makes it harder to issue a token accidentally before verification.
/// </summary>
public sealed class JwtTokenService
{
    private readonly JwtSettings _settings;
    private readonly TimeProvider _timeProvider;

    public JwtTokenService(JwtSettings settings, TimeProvider timeProvider)
    {
        _settings = settings;
        _timeProvider = timeProvider;
    }

    /// <summary>Whether JWT issuance is enabled by a configured signing key.</summary>
    public bool IsEnabled => _settings.IsEnabled;

    /// <summary>
    /// Creates a signed token containing account and trusted tenant claims.
    /// A JWT has three Base64Url-encoded sections: header, payload, and signature. The
    /// payload is readable by clients, so it must contain identity data only—never secrets.
    /// The signature makes payload modification detectable; it does not encrypt the payload.
    /// </summary>
    public IssuedJwtToken CreateToken(UserAccount account)
    {
        if (!_settings.IsEnabled)
        {
            throw new InvalidOperationException("JWT authentication is not configured.");
        }

        var issuedAt = _timeProvider.GetUtcNow();
        var expiresAt = issuedAt.AddMinutes(_settings.LifetimeMinutes);

        // Claims are the authenticated facts carried by the token:
        //   sub / NameIdentifier: stable account identity
        //   email / Name: display information for the authenticated principal
        //   jti: unique ID for this individual token (useful for future auditing/revocation)
        //   tenant_id: authorization boundary consumed by AuthenticatedTenantContext
        // The tenant ID is copied from the verified database account, never from the login
        // request, so a caller cannot choose which library a token grants access to.
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, account.Id.ToString("D")),
            new Claim(ClaimTypes.NameIdentifier, account.Id.ToString("D")),
            new Claim(JwtRegisteredClaimNames.Email, account.Email),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("D")),
            new Claim(ClaimTypes.Name, account.DisplayName),
            new Claim(GameHubClaimTypes.TenantId, account.TenantId.ToString("D"))
        };
        // notBefore and expires bound the validity window. SigningCredentials describes
        // how the header and payload are authenticated; HS256 is HMAC using SHA-256.
        var token = new JwtSecurityToken(
            _settings.Issuer,
            _settings.Audience,
            claims,
            notBefore: issuedAt.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: new SigningCredentials(
                _settings.CreateSecurityKey(),
                SecurityAlgorithms.HmacSha256));

        // WriteToken performs the compact "header.payload.signature" serialization sent to
        // the client. Only this string and its expiry leave the service boundary.
        return new IssuedJwtToken(
            new JwtSecurityTokenHandler().WriteToken(token),
            expiresAt);
    }
}

/// <summary>
/// Internal token value and its exact UTC expiry. Returning the expiry separately lets the
/// client renew or discard the token without decoding its payload.
/// </summary>
public sealed record IssuedJwtToken(string AccessToken, DateTimeOffset ExpiresAtUtc);
