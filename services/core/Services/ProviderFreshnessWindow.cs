namespace GameHub.Core.Services;

/// <summary>Resolves the configured provider freshness window consistently across Core services.</summary>
public static class ProviderFreshnessWindow
{
    /// <summary>Returns the configured non-negative provider freshness window.</summary>
    public static TimeSpan FromConfiguration(IConfiguration configuration) =>
        TimeSpan.FromMinutes(
            Math.Max(0, configuration.GetValue("Sync:ProviderFreshnessMinutes", 30)));
}
