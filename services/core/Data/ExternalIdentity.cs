namespace GameHub.Core.Data;

/// <summary>
/// A provider-issued identity linked to exactly one existing GameHub account and tenant.
/// Login resolves only the stable issuer/subject pair; email and other provider claims are
/// deliberately not persisted as authorization inputs.
/// </summary>
public sealed class ExternalIdentity
{
    /// <summary>Internal identifier with no meaning at the identity provider.</summary>
    public Guid Id { get; set; }

    /// <summary>The exact OIDC issuer that asserted the subject.</summary>
    public string Issuer { get; set; } = "";

    /// <summary>The provider-stable OIDC subject; unique only together with Issuer.</summary>
    public string Subject { get; set; } = "";

    /// <summary>The account selected by an already authenticated linking operation.</summary>
    public Guid UserAccountId { get; set; }

    /// <summary>
    /// Duplicated tenant boundary used in the composite foreign key. It can never be supplied
    /// by an OIDC claim or request body and must match the linked account's tenant.
    /// </summary>
    public Guid TenantId { get; set; }

    /// <summary>Navigation to the exact account/tenant pair.</summary>
    public UserAccount? UserAccount { get; set; }

    /// <summary>UTC timestamp at which the signed-in account approved the link.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>UTC timestamp of the latest successful OIDC login through this mapping.</summary>
    public DateTime? LastLoginAtUtc { get; set; }
}
