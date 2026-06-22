namespace GameHub.Core.Models.Auth;

/// <summary>Named rate-limit policies used by authentication endpoints.</summary>
public static class AuthRateLimitPolicies
{
    /// <summary>Per-IP fixed-window policy applied to password/token exchange.</summary>
    public const string Token = "auth-token";
}

/// <summary>
/// Credentials used to create the first local account for a tenant. TenantId is deliberately
/// absent: bootstrap gets it from the authenticated API-key claim.
/// </summary>
public sealed record BootstrapAccountRequest(
    string Email,
    string Password,
    string DisplayName);

/// <summary>
/// Administrative request that creates a tenant and its first owner account atomically.
/// TenantId is deliberately absent: the server generates an unpredictable identifier.
/// </summary>
public sealed record ProvisionTenantRequest(
    string TenantName,
    string Email,
    string Password,
    string DisplayName);

/// <summary>
/// Credentials exchanged for a signed access token. Password is input-only and is never
/// included in TokenResponse, JWT claims, logs, or persisted plaintext.
/// </summary>
public sealed record TokenRequest(string Email, string Password);

/// <summary>
/// Provider ID token and the BFF-generated nonce expected inside it. Account, tenant, email,
/// and role selectors are deliberately absent.
/// </summary>
public sealed record OidcTokenRequest(string IdToken, string Nonce);

/// <summary>
/// Opaque secret used to rotate or revoke a login session. It contains no claims and must be
/// protected like a password; the server persists only its SHA-256 digest.
/// </summary>
public sealed record RefreshTokenRequest(string RefreshToken);

/// <summary>Current and replacement password for an authenticated password change.</summary>
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>
/// Current password confirmation for destructive account and tenant deletion. Tenant and
/// account identifiers are deliberately absent and come only from the signed JWT.
/// </summary>
public sealed record DeleteAccountRequest(string CurrentPassword);

/// <summary>Public account data returned after bootstrap.</summary>
public sealed record AccountResponse(
    Guid Id,
    Guid TenantId,
    string Email,
    string DisplayName,
    DateTime CreatedAtUtc);

/// <summary>Public identifiers returned after a tenant and owner account are provisioned.</summary>
public sealed record TenantProvisioningResponse(
    Guid TenantId,
    string TenantName,
    bool PlatformSyncEnabled,
    AccountResponse Account);

/// <summary>
/// Signed bearer token response. TokenType is "Bearer" and ExpiresAtUtc lets clients stop
/// using an expired token without parsing JWT internals.
/// </summary>
public sealed record TokenResponse(
    string AccessToken,
    string TokenType,
    DateTimeOffset ExpiresAtUtc,
    string? RefreshToken,
    DateTimeOffset? RefreshTokenExpiresAtUtc,
    string SessionPurpose = "full");

/// <summary>
/// Secret-free operator readiness response. Blockers are stable configuration/data-state codes;
/// the response never contains configured values, identities, account counts, or tenant data.
/// </summary>
public sealed record MultiUserStatusResponse(
    bool OidcEnabled,
    bool ReadinessSatisfied,
    bool MultiUserEnabled,
    bool MultiUserActive,
    string LocalPasswordMode,
    IReadOnlyList<string> Blockers);

/// <summary>Structured authentication error safe to return to API clients.</summary>
public sealed record AuthErrorResponse(string Code, string Message);
