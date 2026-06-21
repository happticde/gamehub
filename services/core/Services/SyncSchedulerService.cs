namespace GameHub.Core.Services;

/// <summary>
/// Thread-safe status record for the background sync scheduler.
/// Registered as a singleton so both the scheduler and the status endpoint
/// can read/write the same instance.
/// </summary>
public class SyncSchedulerStatus
{
    private readonly object _lock = new();
    private readonly Dictionary<Guid, TenantSyncSchedulerStatus> _tenantStatuses = [];
    private DateTime? _nextRunAt;

    /// <summary>Whether the scheduler is enabled in configuration.</summary>
    public bool IsEnabled { get; set; }

    /// <summary>Returns scheduler state containing only the selected tenant's last result.</summary>
    public SyncSchedulerSnapshot GetSnapshot(Guid tenantId)
    {
        lock (_lock)
        {
            var tenantStatus = GetOrCreateTenantStatus(tenantId);
            return new SyncSchedulerSnapshot(
                IsEnabled,
                tenantStatus.IsRunning,
                tenantStatus.LastRunAt,
                _nextRunAt,
                tenantStatus.LastResult,
                tenantStatus.ItemsProcessed);
        }
    }

    /// <summary>Marks the start of one tenant's work in a scheduler cycle.</summary>
    public void MarkStarted(Guid tenantId)
    {
        lock (_lock)
        {
            GetOrCreateTenantStatus(tenantId).IsRunning = true;
        }
    }

    /// <summary>Marks successful completion of one tenant's work.</summary>
    public void MarkCompleted(Guid tenantId, int itemsProcessed)
    {
        lock (_lock)
        {
            CompleteTenant(tenantId, "Success", itemsProcessed);
        }
    }

    /// <summary>Marks one tenant as skipped by its server-controlled sync policy.</summary>
    public void MarkSkipped(Guid tenantId)
    {
        lock (_lock)
        {
            CompleteTenant(tenantId, "Skipped", 0);
        }
    }

    /// <summary>Marks failed completion of one tenant's work.</summary>
    public void MarkFailed(Guid tenantId, string errorMessage)
    {
        lock (_lock)
        {
            CompleteTenant(tenantId, errorMessage, 0);
        }
    }

    /// <summary>Sets the shared time at which the scheduler will start its next cycle.</summary>
    public void SetNextRunAt(DateTime nextRunAt)
    {
        lock (_lock)
        {
            _nextRunAt = nextRunAt;
        }
    }

    /// <summary>Clears the shared schedule and any in-progress flags when stopping.</summary>
    public void MarkStopped()
    {
        lock (_lock)
        {
            _nextRunAt = null;
            foreach (var status in _tenantStatuses.Values)
            {
                status.IsRunning = false;
            }
        }
    }

    private TenantSyncSchedulerStatus GetOrCreateTenantStatus(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("The tenant identifier cannot be empty.", nameof(tenantId));
        }

        if (!_tenantStatuses.TryGetValue(tenantId, out var status))
        {
            status = new TenantSyncSchedulerStatus();
            _tenantStatuses.Add(tenantId, status);
        }

        return status;
    }

    private void CompleteTenant(Guid tenantId, string result, int itemsProcessed)
    {
        var status = GetOrCreateTenantStatus(tenantId);
        status.IsRunning = false;
        status.LastRunAt = DateTime.UtcNow;
        status.LastResult = result;
        status.ItemsProcessed = itemsProcessed;
    }

    private sealed class TenantSyncSchedulerStatus
    {
        public bool IsRunning { get; set; }
        public DateTime? LastRunAt { get; set; }
        public string? LastResult { get; set; }
        public int ItemsProcessed { get; set; }
    }
}

/// <summary>Tenant-safe scheduler status returned by the sync status endpoint.</summary>
public sealed record SyncSchedulerSnapshot(
    bool IsEnabled,
    bool IsRunning,
    DateTime? LastRunAt,
    DateTime? NextRunAt,
    string? LastResult,
    int ItemsProcessed);

/// <summary>
/// Background worker that runs a full library sync on a configurable interval.
///
/// Mirrors the pattern from <see cref="EnrichmentBackgroundService"/>:
///   - Uses ITenantJobRunner to isolate scoped work for every configured tenant
///   - Respects CancellationToken for graceful shutdown
///   - Logs progress and errors with structured logging
///
/// Configuration (via appsettings.json or env vars):
///   - Sync:EnableScheduler (bool, default: false) — must be true to start
///   - Sync:IntervalMinutes (int, default: 360) — time between sync cycles
/// </summary>
public class SyncSchedulerService : BackgroundService
{
    private readonly ITenantJobRunner _tenantJobs;
    private readonly IConfiguration _config;
    private readonly ILogger<SyncSchedulerService> _logger;
    private readonly SyncSchedulerStatus _status;

    public SyncSchedulerService(
        ITenantJobRunner tenantJobs,
        IConfiguration config,
        ILogger<SyncSchedulerService> logger,
        SyncSchedulerStatus status)
    {
        _tenantJobs = tenantJobs;
        _config = config;
        _logger = logger;
        _status = status;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small initial delay to let the app fully start up
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        var intervalMinutes = _config.GetValue("Sync:IntervalMinutes", 360);
        var interval = TimeSpan.FromMinutes(intervalMinutes);

        _logger.LogInformation(
            "Sync scheduler started (interval: {IntervalMinutes} minutes)",
            intervalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Sync scheduler: starting sync cycle");
            try
            {
                var count = await RunCycleAsync(stoppingToken);

                var nextRun = DateTime.UtcNow.Add(interval);
                _status.SetNextRunAt(nextRun);

                _logger.LogInformation(
                    "Sync scheduler: cycle complete — {Count} items processed. Next run at {NextRun}",
                    count, nextRun.ToString("O"));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown — exit the loop
                break;
            }
            catch (Exception ex)
            {
                var nextRun = DateTime.UtcNow.Add(interval);
                _status.SetNextRunAt(nextRun);

                _logger.LogError(ex,
                    "Sync scheduler: cycle failed. Next retry at {NextRun}",
                    nextRun.ToString("O"));
            }

            // Wait for the configured interval before the next cycle
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _status.MarkStopped();
        _logger.LogInformation("Sync scheduler stopped");
    }

    /// <summary>
    /// Runs one tenant-isolated scheduler cycle. Kept separate from the timing loop so tenant
    /// boundaries and policy skips can be verified without waiting for hosted-service delays.
    /// </summary>
    internal async Task<int> RunCycleAsync(CancellationToken cancellationToken)
    {
        var count = 0;
        await _tenantJobs.RunForEachTenantAsync(async (services, tenantId, tenantCancellationToken) =>
        {
            var unifier = services.GetRequiredService<UnificationService>();
            var activityLog = services.GetRequiredService<ActivityLogService>();

            _status.MarkStarted(tenantId);
            try
            {
                await activityLog.LogAsync("SyncStarted", "Scheduled sync cycle started");
                var tenantCount = await unifier.RunFullSyncAsync();
                count += tenantCount;
                _status.MarkCompleted(tenantId, tenantCount);
                await activityLog.LogAsync(
                    "SyncFinished",
                    $"Scheduled sync completed — {tenantCount} items processed");

                _logger.LogInformation(
                    "Sync scheduler completed tenant {TenantId} with {Count} items processed",
                    tenantId,
                    tenantCount);
            }
            catch (TenantPlatformSyncDisabledException)
            {
                // Missing supported credentials are a normal policy skip, not a failed
                // scheduler cycle, and must not prevent later tenants from being visited.
                _status.MarkSkipped(tenantId);
                await activityLog.LogAsync(
                    "SyncSkipped",
                    "Scheduled sync skipped because no supported credential is active");
                _logger.LogInformation(
                    "Sync scheduler skipped tenant {TenantId}: platform sync disabled",
                    tenantId);
            }
            catch (Exception exception)
            {
                _status.MarkFailed(tenantId, exception.Message);
                try
                {
                    await activityLog.LogAsync(
                        "SyncFinished",
                        $"Scheduled sync failed: {exception.Message}");
                }
                catch (Exception activityException)
                {
                    _logger.LogWarning(
                        activityException,
                        "Could not record sync failure for tenant {TenantId}",
                        tenantId);
                }

                throw;
            }
        }, cancellationToken);

        return count;
    }
}
