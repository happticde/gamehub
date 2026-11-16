using Microsoft.Extensions.Options;
using OpenIddict.Server;

namespace GameHub.Core.Services;

/// <summary>Checks managed storage hourly and reloads retained key generations before expiry.</summary>
public sealed class McpKeyMaintenance(McpManagedKeyStore store, McpProtocolKeys keys,
    IOptionsMonitorCache<OpenIddictServerOptions> cache,
    IOptionsMonitorCache<OpenIddict.Validation.OpenIddictValidationOptions> validationCache,
    ILogger<McpKeyMaintenance> logger) : BackgroundService
{
    /// <summary>Runs independently from user requests; failures expose only stable operator codes.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var healthy = RunOnce();
            await Task.Delay(healthy ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    internal bool RunOnce()
    {
        lock (gate)
        {
            try
            {
                // Also reload an explicit CLI renewal. Healthy reads need no writer lock.
                var state = store.ReadState();
                var last = state.Certificates.Last(entry => entry.Purpose == "signing").Id;
                var renewed = store.Check().Code == "renewal_due" && store.Renew();
                if (renewed || last != loaded)
                {
                    using var replacement = store.LoadProtocolKeys();
                    keys.Reload(replacement);
                    cache.Clear();
                    validationCache.Clear();
                    loaded = store.ReadState().Certificates.Last(entry => entry.Purpose == "signing").Id;
                }
                var status = store.Check();
                logger.LogInformation("MCP managed key readiness: {Code}", status.Code);
                return true;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                keys.FailClosed();
                loaded = null;
                cache.Clear();
                validationCache.Clear();
                logger.LogError("MCP managed key maintenance failed: {Code}; run the private key check and restore consistent state if required.",
                    exception is McpKeyStoreException failure ? failure.Code : "invalid_storage");
                return false;
            }
        }
    }

    private readonly object gate = new();
    private string? loaded = store.ReadState().Certificates.Last(entry => entry.Purpose == "signing").Id;
}
