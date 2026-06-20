using System.Security.Claims;
using GameHub.Shared.Authentication;

namespace GameHub.Core.Data;

/// <summary>
/// Stable identifiers used while GameHub operates in its single-user compatibility mode.
/// </summary>
public static class TenantDefaults
{
    /// <summary>The tenant that owns records created before user accounts are enabled.</summary>
    public static readonly Guid DefaultTenantId = GameHubTenantDefaults.DefaultTenantId;
}

/// <summary>
/// A GameHub account boundary selected through trusted authentication claims.
/// </summary>
public sealed class Tenant
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>Exposes the tenant selected from trusted server-side identity.</summary>
public interface ITenantContext
{
    Guid TenantId { get; }
}

/// <summary>
/// Provides deterministic default-tenant behavior for tests and compatibility tooling.
/// </summary>
public sealed class DefaultTenantContext : ITenantContext
{
    public Guid TenantId => TenantDefaults.DefaultTenantId;
}

/// <summary>
/// Resolves the active tenant from trusted authentication claims or a server-controlled
/// background execution scope. Anonymous local mode remains assigned to the default tenant.
/// </summary>
public sealed class AuthenticatedTenantContext : ITenantContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly TenantExecutionContext _executionContext;

    public AuthenticatedTenantContext(
        IHttpContextAccessor httpContextAccessor,
        TenantExecutionContext executionContext)
    {
        _httpContextAccessor = httpContextAccessor;
        _executionContext = executionContext;
    }

    /// <inheritdoc />
    public Guid TenantId
    {
        get
        {
            // Background workers have no HttpContext. TenantJobRunner creates an AsyncLocal
            // execution scope for each tenant, and that explicit server-side choice takes
            // precedence over any ambient request principal.
            if (_executionContext.TenantId is { } backgroundTenantId)
            {
                return backgroundTenantId;
            }

            // For HTTP requests, UseAuthentication has already validated the selected scheme
            // and populated HttpContext.User before a controller resolves CoreContext.
            var principal = _httpContextAccessor.HttpContext?.User;
            if (principal?.Identity?.IsAuthenticated != true)
            {
                // This is legacy local mode, retained only while API-key authentication is
                // allowed to be disabled. It never accepts a tenant ID from request data.
                return TenantDefaults.DefaultTenantId;
            }

            // A tenant claim is trusted because the API-key handler created it from server
            // configuration or JwtBearer validated the signature before exposing it here.
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

/// <summary>
/// Carries a server-selected tenant through nested dependency-injection scopes during jobs.
/// </summary>
public sealed class TenantExecutionContext
{
    private readonly AsyncLocal<Guid?> _tenantId = new();

    internal Guid? TenantId => _tenantId.Value;

    internal IDisposable BeginScope(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("The tenant identifier cannot be empty.", nameof(tenantId));
        }

        var previousTenantId = _tenantId.Value;
        _tenantId.Value = tenantId;
        return new TenantExecutionScope(_tenantId, previousTenantId);
    }

    private sealed class TenantExecutionScope(AsyncLocal<Guid?> tenantId, Guid? previousTenantId) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            tenantId.Value = previousTenantId;
            _disposed = true;
        }
    }
}

/// <summary>Marks a durable Core record as belonging to exactly one tenant.</summary>
public interface ITenantOwned
{
    Guid TenantId { get; set; }
}
