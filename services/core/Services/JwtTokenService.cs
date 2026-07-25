using System.Globalization;
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
/// <param name="RefreshTokenLifetimeDays">Absolute lifetime of an opaque refresh session.</param>
public sealed record JwtSettings(
    bool IsEnabled,
    string Issuer,
    string Audience,
    string SigningKey,
    int LifetimeMinutes,
    int RefreshTokenLifetimeDays)
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
        var refreshTokenLifetimeDays = section.GetValue("RefreshTokenLifetimeDays", 30);
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
        // lifetimes increase exposure because individual access JWTs cannot be recalled without
        // advancing the account epoch and invalidating every session for that account.
        if (lifetimeMinutes is < 5 or > 1440)
        {
            throw new InvalidOperationException(
                $"{SectionName}:LifetimeMinutes must be between 5 and 1440.");
        }

        // Refresh tokens are revocable server-side, but a bounded absolute lifetime still
        // limits how long a copied secret can be useful if its theft is never detected.
        if (refreshTokenLifetimeDays is < 1 or > 90)
        {
            throw new InvalidOperationException(
                $"{SectionName}:RefreshTokenLifetimeDays must be between 1 and 90.");
        }

        return new JwtSettings(
            isEnabled,
            issuer,
            audience,
            signingKey,
            lifetimeMinutes,
            refreshTokenLifetimeDays);
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
    /// Creates a normal access JWT bound to one durable local session family. The payload is
    /// signed but readable, so it contains only trusted identity and revocation references.
    /// </summary>
    public IssuedJwtToken CreateToken(UserAccount account, Guid accountSessionId)
    {
        if (accountSessionId == Guid.Empty)
        {
            throw new ArgumentException(
                "A normal access token requires a stable local session.",
                nameof(accountSessionId));
        }

        return CreateToken(
            account,
            _settings.LifetimeMinutes,
            accountSessionId,
            sessionPurpose: null,
            deletionAuthorization: null);
    }

    /// <summary>
    /// Creates a short-lived, non-refreshable token whose authorization is restricted by the
    /// bearer validation event to the explicit OIDC linking endpoint. This is the only local
    /// password bridge retained while production multi-user mode is enabled.
    /// </summary>
    public IssuedJwtToken CreateOidcLinkToken(UserAccount account) =>
        CreateToken(
            account,
            lifetimeMinutes: 10,
            accountSessionId: null,
            sessionPurpose: GameHubClaimTypes.OidcLinkOnlySessionPurpose,
            deletionAuthorization: null);

    /// <summary>
    /// Creates a short-lived, non-refreshable token accepted only by the idempotent deletion
    /// endpoint. Pending deletion remains blocked everywhere else while ADR-010 retry stays usable.
    /// </summary>
    public IssuedJwtToken CreateAccountDeletionToken(
        UserAccount account,
        bool externalOidcReauthentication = false) =>
        CreateToken(
            account,
            lifetimeMinutes: 10,
            accountSessionId: null,
            sessionPurpose: GameHubClaimTypes.AccountDeletionOnlySessionPurpose,
            deletionAuthorization: externalOidcReauthentication
                ? GameHubClaimTypes.ExternalOidcDeletionAuthorization
                : null);

    private IssuedJwtToken CreateToken(
        UserAccount account,
        int lifetimeMinutes,
        Guid? accountSessionId,
        string? sessionPurpose,
        string? deletionAuthorization)
    {
        if (!_settings.IsEnabled)
        {
            throw new InvalidOperationException("JWT authentication is not configured.");
        }

        var issuedAt = _timeProvider.GetUtcNow();
        var expiresAt = issuedAt.AddMinutes(lifetimeMinutes);

        // Claims are the authenticated facts carried by the token:
        //   sub / NameIdentifier: stable account identity
        //   email / Name: display information for the authenticated principal
        //   jti: unique ID for this individual token (useful for future auditing/revocation)
        //   tenant_id: authorization boundary consumed by AuthenticatedTenantContext
        // The tenant ID is copied from the verified database account, never from the login
        // request, so a caller cannot choose which library a token grants access to.
        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub, account.Id.ToString("D")),
            new Claim(ClaimTypes.NameIdentifier, account.Id.ToString("D")),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("D")),
            new Claim(ClaimTypes.Name, account.DisplayName),
            new Claim(GameHubClaimTypes.TenantId, account.TenantId.ToString("D")),
            new Claim(
                GameHubClaimTypes.SessionVersion,
                account.SessionVersion.ToString(CultureInfo.InvariantCulture),
                ClaimValueTypes.Integer64)
        };
        if (!string.IsNullOrWhiteSpace(account.Email))
        {
            // External email is optional profile data only. Its presence in a signed display claim
            // never changes the account/tenant mapping selected from the database.
            claims.Add(new Claim(JwtRegisteredClaimNames.Email, account.Email));
        }
        if (accountSessionId is not null)
        {
            claims.Add(new Claim(
                GameHubClaimTypes.AccountSessionId,
                accountSessionId.Value.ToString("D")));
        }
        if (sessionPurpose is not null)
        {
            // The purpose is signed with the token. A request cannot add/remove it through a
            // header, query string, or provider claim.
            claims.Add(new Claim(GameHubClaimTypes.SessionPurpose, sessionPurpose));
        }
        if (deletionAuthorization is not null)
        {
            claims.Add(new Claim(
                GameHubClaimTypes.AccountDeletionAuthorization,
                deletionAuthorization));
        }
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
