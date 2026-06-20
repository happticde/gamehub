namespace GameHub.Core.Data;

/// <summary>
/// Local credentials for the single account that owns a tenant library. This identity model
/// is intentionally separate from tenant-owned game data: login must locate an account by
/// email before any tenant is trusted, then successful password verification reveals TenantId.
/// </summary>
public sealed class UserAccount
{
    /// <summary>Stable account identifier written into JWT subject claims.</summary>
    public Guid Id { get; set; }

    /// <summary>The library boundary granted after this account authenticates.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Navigation to the tenant; never serialized by authentication DTOs.</summary>
    public Tenant? Tenant { get; set; }

    /// <summary>Canonical email retained for display and the JWT email claim.</summary>
    public string Email { get; set; } = "";

    /// <summary>Uppercase lookup key with a global unique index for case-insensitive login.</summary>
    public string NormalizedEmail { get; set; } = "";

    /// <summary>Human-readable name written into the principal's name claim.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>
    /// Versioned, salted ASP.NET password hash. This is not encrypted password data and cannot
    /// be turned back into the original password; it must never be returned by an API.
    /// </summary>
    public string PasswordHash { get; set; } = "";

    /// <summary>UTC timestamp at which bootstrap persisted this account.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>UTC timestamp of the latest successful password verification.</summary>
    public DateTime? LastLoginAtUtc { get; set; }

    /// <summary>
    /// Independent device/login sessions. They are identity records rather than tenant-
    /// filtered library rows because refresh begins before an authenticated tenant exists.
    /// </summary>
    public ICollection<RefreshTokenSession> RefreshTokenSessions { get; set; } = [];
}
