using System.Security.Claims;
using GameHub.Core.Data;
using GameHub.Core.Models;
using GameHub.Core.Services;
using GameHub.Shared.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GameHub.Core.Controllers;

/// <summary>Browser account management for owned consent connections, never integration credentials.</summary>
[ApiController]
[Route("api/auth/mcp-connections")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class McpConnectionsController(CoreContext db, ITenantContext tenantContext,
    TimeProvider clock, ILogger<McpConnectionsController> logger) : ControllerBase
{
    /// <summary>Lists minimal consent metadata for active connections owned by the signed account.</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        if (!TryGetOwner(out var account, out var tenant)) return Unauthorized();
        var now = clock.GetUtcNow().UtcDateTime;
        var rows = await db.McpGrants.AsNoTracking()
            .Where(grant => grant.UserAccountId == account && grant.TenantId == tenant
                && grant.RevokedAtUtc == null && grant.ExpiresAtUtc > now
                && grant.AccountSession != null && grant.AccountSession.RevokedAtUtc == null
                && grant.AccountSession.UserAccount != null
                && grant.AccountSession.UserAccount.SessionVersion == grant.ApprovedSessionVersion
                && grant.AccountSession.UserAccount.Tenant != null
                && grant.AccountSession.UserAccount.Tenant.DeletionRequestedAtUtc == null
                && grant.OidcProviderSession != null && grant.OidcProviderSession.ReissueBlockedAtUtc == null)
            .OrderByDescending(grant => grant.CreatedAtUtc).ThenBy(grant => grant.Id)
            .Select(grant => new { grant.Id, grant.ClientId, grant.Resource, grant.Scopes,
                grant.CreatedAtUtc, grant.ExpiresAtUtc, grant.RevokedAtUtc })
            .ToListAsync(cancellationToken);
        return Ok(rows.Select(grant => new McpConnectionDto(grant.Id, grant.ClientId,
            grant.Resource, grant.Scopes.Split(' '),
            DateTime.SpecifyKind(grant.CreatedAtUtc, DateTimeKind.Utc),
            DateTime.SpecifyKind(grant.ExpiresAtUtc, DateTimeKind.Utc), grant.RevokedAtUtc)));
    }

    /// <summary>Revokes one owned grant; unknown, foreign and ended IDs are indistinguishable no-ops.</summary>
    [HttpDelete("{connectionId:guid}")]
    public async Task<IActionResult> Revoke(Guid connectionId, CancellationToken cancellationToken)
    {
        if (!TryGetOwner(out var account, out var tenant)) return Unauthorized();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Match issuance's tenant-first lock order before selecting a family. A renewal that
        // commits first is included in revocation; one ordered after this commit cannot revive it.
        await db.Tenants.Where(row => row.Id == tenant)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.DeletionRequestedAtUtc,
                row => row.DeletionRequestedAtUtc), cancellationToken);
        var family = await db.McpGrants.Where(grant => grant.Id == connectionId
                && grant.UserAccountId == account && grant.TenantId == tenant)
            .Select(grant => (Guid?)grant.AccountSessionId).SingleOrDefaultAsync(cancellationToken);
        if (family is { } familyId)
            await AccountSessionRevocation.RevokeFamilyAsync(db, familyId, account, tenant,
                clock.GetUtcNow().UtcDateTime, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Processed an owned assistant connection revocation");
        return NoContent();
    }

    private bool TryGetOwner(out Guid account, out Guid tenant)
    {
        tenant = Guid.Empty;
        return Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out account)
            && account != Guid.Empty
            && Guid.TryParse(User.FindFirstValue(GameHubClaimTypes.TenantId), out tenant)
            && tenant != Guid.Empty && tenant == tenantContext.TenantId
            && User.FindFirstValue(GameHubClaimTypes.SessionPurpose) is null
            && Guid.TryParse(User.FindFirstValue(GameHubClaimTypes.AccountSessionId), out var family)
            && family != Guid.Empty;
    }
}
