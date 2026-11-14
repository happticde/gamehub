using OpenIddict.EntityFrameworkCore.Models;

namespace GameHub.Core.Data;

/// <summary>OpenIddict client registration; no user ownership is inferred from its identifier.</summary>
public sealed class McpProtocolApplication
    : OpenIddictEntityFrameworkCoreApplication<string, McpProtocolAuthorization, McpProtocolToken>;

/// <summary>Protocol scope metadata managed by OpenIddict.</summary>
public sealed class McpProtocolScope : OpenIddictEntityFrameworkCoreScope<string>;

/// <summary>
/// One protocol authorization owned by the same account, tenant and independent family as its
/// consent grant. Pending device tokens need no authorization until approved redemption.
/// </summary>
public sealed class McpProtocolAuthorization
    : OpenIddictEntityFrameworkCoreAuthorization<string, McpProtocolApplication, McpProtocolToken>
{
    /// <summary>Trusted tenant, never a protocol request parameter.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Account that approved the connection.</summary>
    public Guid UserAccountId { get; set; }

    /// <summary>Independent connection family, distinct from the consenting browser family.</summary>
    public Guid AccountSessionId { get; set; }
}

/// <summary>
/// OpenIddict alone owns code and rotating integration-token storage. Payloads must be produced
/// by its encrypted token format; browser RefreshTokenSession rows are never used for these tokens.
/// </summary>
public sealed class McpProtocolToken
    : OpenIddictEntityFrameworkCoreToken<string, McpProtocolApplication, McpProtocolAuthorization>;
