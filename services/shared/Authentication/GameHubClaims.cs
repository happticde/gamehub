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
}

/// <summary>Stable identity used by existing single-user installations.</summary>
public static class GameHubTenantDefaults
{
    /// <summary>The tenant assigned to the legacy API key and unauthenticated local mode.</summary>
    public static readonly Guid DefaultTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
}
