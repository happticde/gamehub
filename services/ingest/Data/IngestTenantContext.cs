using System.Security.Claims;
using GameHub.Shared.Authentication;

namespace GameHub.Ingest.Data;

/// <summary>Stable tenant used by legacy Ingest data and migration/design-time work.</summary>
public static class IngestTenantDefaults
{
    public static readonly Guid DefaultTenantId = GameHubTenantDefaults.DefaultTenantId;
}

/// <summary>Exposes the tenant selected by Ingest's trusted authentication principal.</summary>
public interface IIngestTenantContext
{
    Guid TenantId { get; }
}

/// <summary>Compatibility context for tests, migrations, and auth-disabled local mode.</summary>
public sealed class DefaultIngestTenantContext : IIngestTenantContext
{
    public Guid TenantId => IngestTenantDefaults.DefaultTenantId;
}

/// <summary>
/// Reads the tenant claim created by the API-key authentication handler. Request payloads and
/// query parameters never participate in tenant selection; unauthenticated infrastructure work
/// remains assigned to the migrated default tenant.
/// </summary>
public sealed class AuthenticatedIngestTenantContext(IHttpContextAccessor httpContextAccessor)
    : IIngestTenantContext
{
    public Guid TenantId
    {
        get
        {
            var principal = httpContextAccessor.HttpContext?.User;
            if (principal?.Identity?.IsAuthenticated != true)
            {
                return IngestTenantDefaults.DefaultTenantId;
            }

            var claimValue = principal.FindFirstValue(GameHubClaimTypes.TenantId);
            if (!Guid.TryParse(claimValue, out var tenantId) || tenantId == Guid.Empty)
            {
                throw new InvalidOperationException(
                    $"Authenticated principals must contain a valid '{GameHubClaimTypes.TenantId}' claim.");
            }

            return tenantId;
        }
    }
}

/// <summary>Marks raw or audit data as owned by exactly one Ingest tenant.</summary>
public interface IIngestTenantOwned
{
    Guid TenantId { get; set; }
}
