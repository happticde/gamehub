namespace GameHub.Core.Data;

/// <summary>
/// Issuer-bound, one-way mapping from an OIDC <c>sid</c> to one tenant-bound local session.
///
/// A row with no local mapping is a durable revocation tombstone created when a valid logout wins
/// a login race or names an unknown session. A mapped row can outlive a locally revoked GameHub
/// family so a later, fully validated OIDC transaction can bind the same provider SSO session to a
/// new local family. Raw provider session IDs and subjects are never stored here.
/// </summary>
public sealed class OidcProviderSession
{
    /// <summary>Internal database identity with no meaning at the provider.</summary>
    public Guid Id { get; set; }

    /// <summary>SHA-256 hash over the exact validated issuer and opaque provider session ID.</summary>
    public string SessionKeyHash { get; set; } = "";

    /// <summary>Exact validated external-identity mapping, or null for an unmapped tombstone.</summary>
    public Guid? ExternalIdentityId { get; set; }

    /// <summary>Stable local session family, or null for an unmapped tombstone.</summary>
    public Guid? AccountSessionId { get; set; }

    /// <summary>Mapped account copied only from the validated external identity.</summary>
    public Guid? UserAccountId { get; set; }

    /// <summary>Mapped tenant copied only from the validated external identity.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>Navigation to the validated external identity mapping.</summary>
    public ExternalIdentity? ExternalIdentity { get; set; }

    /// <summary>Navigation to the tenant-bound local session family.</summary>
    public AccountSession? AccountSession { get; set; }

    /// <summary>UTC time at which the hashed provider-session state was first persisted.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// UTC time at which the mapped local GameHub family ended. This alone does not prove that the
    /// provider SSO session ended; a fresh OIDC transaction may replace that local family.
    /// </summary>
    public DateTime? RevokedAtUtc { get; set; }

    /// <summary>
    /// UTC time at which provider logout or terminal account deletion permanently blocked this
    /// issuer-bound session from issuing another local family. Unlike a local family revocation,
    /// this is a durable replay and login-race tombstone.
    /// </summary>
    public DateTime? ReissueBlockedAtUtc { get; set; }
}
