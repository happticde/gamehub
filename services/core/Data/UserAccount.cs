namespace GameHub.Core.Data;

/// <summary>Credential boundary for a tenant owner account.</summary>
public enum UserAccountType
{
    /// <summary>Account with a canonical email and usable local password hash.</summary>
    Local = 0,

    /// <summary>OIDC-only account whose identity is resolved exclusively through an exact mapping.</summary>
    ExternalOidc = 1
}

/// <summary>
/// The single owner account for a tenant library. This identity model is intentionally separate
/// from tenant-owned game data: local login establishes ownership through a password, while OIDC
/// login establishes it through an exact external mapping before TenantId is trusted.
/// </summary>
public sealed class UserAccount
{
    /// <summary>Stable account identifier written into JWT subject claims.</summary>
    public Guid Id { get; set; }

    /// <summary>The library boundary granted after this account authenticates.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Navigation to the tenant; never serialized by authentication DTOs.</summary>
    public Tenant? Tenant { get; set; }

    /// <summary>Determines whether local password operations are permitted for this account.</summary>
    public UserAccountType AccountType { get; set; } = UserAccountType.Local;

    /// <summary>
    /// Canonical local-login email or optional external profile email. External email is never a
    /// lookup, uniqueness, linking, or authorization key.
    /// </summary>
    public string? Email { get; set; }

    /// <summary>Uppercase local-login key. It is always null for external OIDC accounts.</summary>
    public string? NormalizedEmail { get; set; }

    /// <summary>Human-readable name written into the principal's name claim.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>
    /// Versioned, salted ASP.NET password hash for local accounts. External accounts keep this
    /// null. A hash cannot be reversed and must never be returned by an API.
    /// </summary>
    public string? PasswordHash { get; set; }

    /// <summary>UTC timestamp at which bootstrap persisted this account.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>UTC timestamp of the latest successful local or external authentication.</summary>
    public DateTime? LastLoginAtUtc { get; set; }

    /// <summary>
    /// Independent device/login sessions. They are identity records rather than tenant-
    /// filtered library rows because refresh begins before an authenticated tenant exists.
    /// </summary>
    public ICollection<RefreshTokenSession> RefreshTokenSessions { get; set; } = [];

    /// <summary>
    /// Provider identities explicitly linked while this account was authenticated. They are
    /// removed with the account and never discovered or attached by matching email addresses.
    /// </summary>
    public ICollection<ExternalIdentity> ExternalIdentities { get; set; } = [];
}
