namespace GameHub.Core.Data;

/// <summary>
/// Short-lived durable replay marker for a validated OIDC Back-Channel Logout Token.
/// The provider JWT ID is stored only as a one-way hash and never written to logs.
/// </summary>
public sealed class ProcessedOidcLogoutToken
{
    public Guid Id { get; set; }

    /// <summary>SHA-256 hash over the exact validated issuer and JWT ID.</summary>
    public string TokenHash { get; set; } = "";

    /// <summary>UTC time at which the logout action committed.</summary>
    public DateTime ProcessedAtUtc { get; set; }

    /// <summary>
    /// UTC retention boundary. Once expired, the original logout token is already too old to
    /// pass protocol validation, so its marker can be removed safely.
    /// </summary>
    public DateTime ExpiresAtUtc { get; set; }
}
