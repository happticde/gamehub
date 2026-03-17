namespace GameHub.Core.Services;

/// <summary>
/// Thread-safe status record for the background sync scheduler.
/// Registered as a singleton so both the scheduler and the status endpoint
/// can read/write the same instance.
/// </summary>
public class SyncSchedulerStatus
{
    private readonly object _lock = new();

    /// <summary>Whether the scheduler is enabled in configuration.</summary>
    public bool IsEnabled { get; set; }

    /// <summary>True while a sync cycle is actively running.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>UTC timestamp of the last completed sync.</summary>
    public DateTime? LastRunAt { get; private set; }

    /// <summary>UTC timestamp when the next sync is expected to start.</summary>
    public DateTime? NextRunAt { get; private set; }

    /// <summary>"Success" or the error message from the last run.</summary>
    public string? LastResult { get; private set; }

    /// <summary>Number of game-platform records processed in the last run.</summary>
    public int ItemsProcessed { get; private set; }

    /// <summary>Marks the start of a sync cycle.</summary>
    public void MarkStarted()
    {
        lock (_lock)
        {
            IsRunning = true;
        }
    }

    /// <summary>Marks a successful completion of a sync cycle.</summary>
    public void MarkCompleted(int itemsProcessed, DateTime nextRunAt)
    {
        lock (_lock)
        {
            IsRunning = false;
            LastRunAt = DateTime.UtcNow;
            NextRunAt = nextRunAt;
            LastResult = "Success";
            ItemsProcessed = itemsProcessed;
        }
    }

    /// <summary>Marks a failed sync cycle.</summary>
    public void MarkFailed(string errorMessage, DateTime nextRunAt)
    {
        lock (_lock)
        {
            IsRunning = false;
            LastRunAt = DateTime.UtcNow;
            NextRunAt = nextRunAt;
            LastResult = errorMessage;
            ItemsProcessed = 0;
        }
    }

    /// <summary>Clears NextRunAt when the scheduler is stopping.</summary>
    public void MarkStopped()
    {
        lock (_lock)
        {
            IsRunning = false;
            NextRunAt = null;
        }
    }
}

/// <summary>
/// Background worker that runs a full library sync on a configurable interval.
///
/// Mirrors the pattern from <see cref="EnrichmentBackgroundService"/>:
///   - Uses IServiceScopeFactory to resolve scoped services (UnificationService)
///   - Respects CancellationToken for graceful shutdown
///   - Logs progress and errors with structured logging
///
/// Configuration (via appsettings.json or env vars):
///   - Sync:EnableScheduler (bool, default: false) — must be true to start
///   - Sync:IntervalMinutes (int, default: 360) — time between sync cycles
/// </summary>
public class SyncSchedulerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<SyncSchedulerService> _logger;
    private readonly SyncSchedulerStatus _status;

    public SyncSchedulerService(
        IServiceScopeFactory scopeFactory,
        IConfiguration config,
        ILogger<SyncSchedulerService> logger,
        SyncSchedulerStatus status)
    {
        _scopeFactory = scopeFactory;
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
            _status.MarkStarted();

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var unifier = scope.ServiceProvider.GetRequiredService<UnificationService>();

                var count = await unifier.RunFullSyncAsync();

                var nextRun = DateTime.UtcNow.Add(interval);
                _status.MarkCompleted(count, nextRun);

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
                _status.MarkFailed(ex.Message, nextRun);

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
}
