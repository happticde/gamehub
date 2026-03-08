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
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<EnrichmentBackgroundService> _logger;

    public EnrichmentBackgroundService(
        IServiceScopeFactory scopeFactory,
        IConfiguration config,
        ILogger<EnrichmentBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
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
                using var scope = _scopeFactory.CreateScope();
                var enrichment = scope.ServiceProvider.GetRequiredService<EnrichmentService>();

                var remaining = await enrichment.GetUnenrichedCountAsync(stoppingToken);

                if (remaining == 0)
                {
                    // No more un-enriched games — wait longer before checking again
                    _logger.LogDebug("No un-enriched games found, sleeping for 5 minutes");
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                    continue;
                }

                _logger.LogInformation("Enrichment progress: {Remaining} games remaining in the queue", remaining);

                var processed = await enrichment.EnrichUnenrichedAsync(batchSize, stoppingToken);

                if (processed > 0)
                {
                    _logger.LogInformation("Enrichment batch complete: {Count} games processed", processed);
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
}
