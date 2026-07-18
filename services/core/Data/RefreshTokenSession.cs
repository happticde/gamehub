namespace GameHub.Core.Data;

/// <summary>
/// Server-side state for one refresh-token session. Unlike a JWT access token, a refresh
/// token is opaque: the client sees random bytes while the database stores only their hash.
/// This lets the server revoke or consume the secret without being able to recover it.
/// </summary>
public sealed class RefreshTokenSession
{
    /// <summary>Stable database identity; this value is never sent as the refresh token.</summary>
    public Guid Id { get; set; }

    /// <summary>Account whose trusted tenant claim may be copied into a new access token.</summary>
    public Guid UserAccountId { get; set; }

    /// <summary>
    /// Account session epoch captured at issuance. Rotation fails when an account-wide logout
    /// advanced the owner epoch, including for a session inserted by an already in-flight login.
    /// </summary>
    public long SessionVersion { get; set; }

    /// <summary>Account navigation loaded only after the opaque secret has matched.</summary>
    public UserAccount? UserAccount { get; set; }

    /// <summary>
    /// Uppercase SHA-256 digest of the client secret. A unique index prevents two sessions
    /// from accidentally accepting the same random token.
    /// </summary>
    public string TokenHash { get; set; } = "";

    /// <summary>UTC time at which the random client secret was generated.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Absolute UTC expiry; refresh activity does not extend this row.</summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>
    /// Set by the first successful refresh. Consumed tokens are deliberately retained so a
    /// second use can be recognized as replay instead of looking like an unknown token.
    /// </summary>
    public DateTime? ConsumedAtUtc { get; set; }

    /// <summary>Set when logout or replay defense invalidates this session.</summary>
    public DateTime? RevokedAtUtc { get; set; }
}
