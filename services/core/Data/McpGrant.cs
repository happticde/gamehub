namespace GameHub.Core.Data;

/// <summary>Durable, individually revocable consent for one Model Context Protocol connection.</summary>
public sealed class McpGrant
{
    /// <summary>Local connection identifier; never a bearer credential.</summary>
    public Guid Id { get; set; }
    /// <summary>Trusted owner tenant.</summary>
    public Guid TenantId { get; set; }
    /// <summary>Trusted approving account.</summary>
    public Guid UserAccountId { get; set; }
    /// <summary>Exactly one independent local family.</summary>
    public Guid AccountSessionId { get; set; }
    /// <summary>Exactly one owner-bound OpenIddict authorization.</summary>
    public string ProtocolAuthorizationId { get; set; } = "";
    /// <summary>Verified provider provenance shared by any number of this owner's grants.</summary>
    public Guid OidcProviderSessionId { get; set; }
    /// <summary>Approved public client identifier, not a secret or proof of client identity.</summary>
    public string ClientId { get; set; } = "";
    /// <summary>One exact canonical resource URI. No aliases or multiple resources.</summary>
    public string Resource { get; set; } = "";
    /// <summary>Canonical read scope with optional tracking scope; excludes protocol offline_access.</summary>
    public string Scopes { get; set; } = McpPermissions.LibraryRead;
    /// <summary>Original account session epoch; refresh must never advance it.</summary>
    public long ApprovedSessionVersion { get; set; }
    /// <summary>UTC approval/creation time.</summary>
    public DateTime CreatedAtUtc { get; set; }
    /// <summary>Absolute UTC end of renewal; rotation never extends it.</summary>
    public DateTime ExpiresAtUtc { get; set; }
    /// <summary>UTC terminal revocation marker.</summary>
    public DateTime? RevokedAtUtc { get; set; }
    /// <summary>Exact owned connection family.</summary>
    public AccountSession? AccountSession { get; set; }
    /// <summary>Exact owned protocol authorization.</summary>
    public McpProtocolAuthorization? ProtocolAuthorization { get; set; }
    /// <summary>Existing verified provider row, retaining its original browser mapping.</summary>
    public OidcProviderSession? OidcProviderSession { get; set; }
}

/// <summary>Accepted application permission names from Architecture Decision Record 016.</summary>
public static class McpPermissions
{
    /// <summary>Default consent: search and read the library.</summary>
    public const string LibraryRead = "library:read";
    /// <summary>Optional explicit consent: change play status and personal rating only.</summary>
    public const string TrackingWrite = "tracking:write";
    /// <summary>Canonical persisted read/write scope set.</summary>
    public const string ReadAndWrite = LibraryRead + " " + TrackingWrite;
}
