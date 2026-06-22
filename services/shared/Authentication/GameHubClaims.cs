namespace GameHub.Shared.Authentication;

/// <summary>
/// Claim types issued by trusted GameHub authentication handlers. Keeping the name in the
/// shared project guarantees API-key issuance, JWT issuance, and Core tenant resolution all
/// refer to exactly the same claim instead of duplicating a security-sensitive string.
/// </summary>
public static class GameHubClaimTypes
{
    /// <summary>
    /// Identifies the tenant whose data the authenticated principal may access. This value is
    /// accepted only after an authentication handler validates the API key or JWT signature.
    /// </summary>
    public const string TenantId = "gamehub:tenant_id";

    /// <summary>
    /// Restricts a locally authenticated bootstrap session to explicit OIDC identity linking.
    /// Absence means a normal historical/full session so upgrades do not invalidate issued JWTs.
    /// </summary>
    public const string SessionPurpose = "gamehub:session_purpose";

    /// <summary>Value permitting only the Core OIDC linking callback.</summary>
    public const string OidcLinkOnlySessionPurpose = "oidc_link_only";

    /// <summary>Value permitting only an idempotent pending account-deletion retry.</summary>
    public const string AccountDeletionOnlySessionPurpose = "account_deletion_only";
}

/// <summary>Stable identity used by existing single-user installations.</summary>
public static class GameHubTenantDefaults
{
    /// <summary>The tenant assigned to the legacy API key and unauthenticated local mode.</summary>
    public static readonly Guid DefaultTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
}
