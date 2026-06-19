namespace GameHub.Shared.Authentication;

/// <summary>Claim types issued by trusted GameHub authentication handlers.</summary>
public static class GameHubClaimTypes
{
    /// <summary>Identifies the tenant whose data the authenticated principal may access.</summary>
    public const string TenantId = "gamehub:tenant_id";
}

/// <summary>Stable identity used by existing single-user installations.</summary>
public static class GameHubTenantDefaults
{
    /// <summary>The tenant assigned to the legacy API key and unauthenticated local mode.</summary>
    public static readonly Guid DefaultTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
}
