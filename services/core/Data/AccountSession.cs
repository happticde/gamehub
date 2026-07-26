namespace GameHub.Core.Data;

/// <summary>
/// Durable local login/session family shared by every access token and rotating refresh token
/// issued for one browser or device login. Its identifier is local and carries no provider or
/// tenant-selection meaning.
/// </summary>
public sealed class AccountSession
{
    /// <summary>Stable local session identifier written into normal GameHub access JWTs.</summary>
    public Guid Id { get; set; }

    /// <summary>Account that owned this session when it was created.</summary>
    public Guid UserAccountId { get; set; }

    /// <summary>
    /// Duplicated tenant boundary used by the composite account foreign key. It always comes from
    /// the authenticated account and can never be selected by an OIDC claim.
    /// </summary>
    public Guid TenantId { get; set; }

    /// <summary>Navigation to the exact account/tenant pair.</summary>
    public UserAccount? UserAccount { get; set; }

    /// <summary>UTC time at which the local session family was created.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// UTC time at which every access and refresh credential in this family became invalid.
    /// Refresh rotation never changes this value.
    /// </summary>
    public DateTime? RevokedAtUtc { get; set; }

    /// <summary>Rotating opaque refresh-token rows belonging to this stable session family.</summary>
    public ICollection<RefreshTokenSession> RefreshTokenSessions { get; set; } = [];

    /// <summary>Optional validated OIDC provider-session mapping for this local session.</summary>
    public OidcProviderSession? OidcProviderSession { get; set; }
}
