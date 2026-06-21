namespace GameHub.Core.Services;

/// <summary>
/// Background worker that automatically enriches un-enriched games after sync.
///
/// Runs continuously on startup, processing un-enriched games in configurable
/// batches with delays between batches to respect provider rate limits
/// (IGDB allows ~4 requests/second for non-commercial use).
///
/// Configuration (via appsettings.json or env vars):
///   - Igdb:EnableBackgroundEnrichment (bool, default: true)
///   - Igdb:BatchSize (int, default: 4)
///   - Igdb:DelayBetweenBatchesMs (int, default: 1500)
/// </summary>
public class EnrichmentBackgroundService : BackgroundService
{
    private readonly ITenantJobRunner _tenantJobs;
    private readonly IConfiguration _config;
    private readonly ILogger<EnrichmentBackgroundService> _logger;

    public EnrichmentBackgroundService(
        ITenantJobRunner tenantJobs,
        IConfiguration config,
        ILogger<EnrichmentBackgroundService> logger)
    {
        _tenantJobs = tenantJobs;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small initial delay to let the app fully start up
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        var batchSize = _config.GetValue("Igdb:BatchSize", 4);
        var delayMs = _config.GetValue("Igdb:DelayBetweenBatchesMs", 1500);

        _logger.LogInformation(
            "Enrichment background service started (batch size: {BatchSize}, delay: {DelayMs}ms)",
            batchSize, delayMs);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var (remaining, processed) = await RunCycleAsync(batchSize, stoppingToken);

                if (remaining == 0)
                {
                    // No more un-enriched games — wait longer before checking again
                    _logger.LogDebug("No un-enriched games found, sleeping for 5 minutes");
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                    continue;
                }

                if (processed > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(delayMs), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Enrichment background service encountered an error, retrying in 30s");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }

        _logger.LogInformation("Enrichment background service stopped");
    }

    /// <summary>
    /// Runs one tenant-isolated enrichment batch. Separating the batch from the timing loop keeps
    /// per-tenant limits and ownership verifiable without waiting for hosted-service delays.
    /// </summary>
    internal async Task<(int Remaining, int Processed)> RunCycleAsync(
        int batchSize,
        CancellationToken cancellationToken)
    {
        var remaining = 0;
        var processed = 0;
        await _tenantJobs.RunForEachTenantAsync(async (services, tenantId, tenantCancellationToken) =>
        {
            var enrichment = services.GetRequiredService<EnrichmentService>();
            var tenantRemaining = await enrichment.GetUnenrichedCountAsync(tenantCancellationToken);
            remaining += tenantRemaining;

            if (tenantRemaining == 0)
            {
                return;
            }

            _logger.LogInformation(
                "Enrichment progress for tenant {TenantId}: {Remaining} games remaining",
                tenantId,
                tenantRemaining);

            var tenantProcessed = await enrichment.EnrichUnenrichedAsync(
                batchSize,
                tenantCancellationToken);
            processed += tenantProcessed;
            if (tenantProcessed > 0)
            {
                _logger.LogInformation(
                    "Enrichment batch complete for tenant {TenantId}: {Count} games processed",
                    tenantId,
                    tenantProcessed);
            }
        }, cancellationToken);

        return (remaining, processed);
    }
}
