using GameHub.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace GameHub.Core.Services;

/// <summary>Runs server-side background work once for every configured tenant.</summary>
public interface ITenantJobRunner
{
    /// <summary>
    /// Creates an isolated service scope for each tenant and continues with remaining tenants
    /// if one execution fails. Failures are aggregated after all tenants have been attempted.
    /// </summary>
    Task RunForEachTenantAsync(
        Func<IServiceProvider, Guid, CancellationToken, Task> operation,
        CancellationToken cancellationToken = default);
}

/// <summary>Default sequential tenant job orchestrator.</summary>
public sealed class TenantJobRunner : ITenantJobRunner
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TenantExecutionContext _executionContext;
    private readonly ILogger<TenantJobRunner> _logger;

    public TenantJobRunner(
        IServiceScopeFactory scopeFactory,
        TenantExecutionContext executionContext,
        ILogger<TenantJobRunner> logger)
    {
        _scopeFactory = scopeFactory;
        _executionContext = executionContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task RunForEachTenantAsync(
        Func<IServiceProvider, Guid, CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        List<Guid> tenantIds;
        using (var discoveryScope = _scopeFactory.CreateScope())
        {
            var db = discoveryScope.ServiceProvider.GetRequiredService<CoreContext>();
            tenantIds = await db.Tenants
                .AsNoTracking()
                .OrderBy(tenant => tenant.Id)
                .Select(tenant => tenant.Id)
                .ToListAsync(cancellationToken);
        }

        var failures = new List<Exception>();
        foreach (var tenantId in tenantIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var tenantExecution = _executionContext.BeginScope(tenantId);
            using var tenantScope = _scopeFactory.CreateScope();
            try
            {
                await operation(tenantScope.ServiceProvider, tenantId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Background operation failed for tenant {TenantId}",
                    tenantId);
                failures.Add(new InvalidOperationException(
                    $"Background operation failed for tenant '{tenantId}'.",
                    exception));
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("One or more tenant background operations failed.", failures);
        }
    }
}
