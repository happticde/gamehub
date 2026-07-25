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

    /// <summary>
    /// Monotonic account session epoch. Core compares this signed value with durable account
    /// state on every ordinary bearer request so an account-wide logout invalidates access JWTs
    /// immediately instead of waiting for their normal expiry.
    /// </summary>
    public const string SessionVersion = "gamehub:session_version";

    /// <summary>
    /// Stable server-generated local session family. Core checks this signed reference against
    /// live tenant-bound state on every ordinary bearer request.
    /// </summary>
    public const string AccountSessionId = "gamehub:account_session_id";

    /// <summary>Value permitting only the Core OIDC linking callback.</summary>
    public const string OidcLinkOnlySessionPurpose = "oidc_link_only";

    /// <summary>Value permitting only an idempotent pending account-deletion retry.</summary>
    public const string AccountDeletionOnlySessionPurpose = "account_deletion_only";

    /// <summary>Records the server-validated proof used to authorize a deletion-only token.</summary>
    public const string AccountDeletionAuthorization = "gamehub:account_deletion_authorization";

    /// <summary>Value proving that Core accepted a fresh exact external OIDC reauthentication.</summary>
    public const string ExternalOidcDeletionAuthorization = "external_oidc";
}

/// <summary>Stable identity used by existing single-user installations.</summary>
public static class GameHubTenantDefaults
{
    /// <summary>The tenant assigned to the legacy API key and unauthenticated local mode.</summary>
    public static readonly Guid DefaultTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
}
