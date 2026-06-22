using Microsoft.Extensions.Configuration;

namespace GameHub.Shared.Authentication;

/// <summary>
/// Immutable operator-controlled multi-user mode switch shared by Core and Ingest.
///
/// The value is read once during process startup. Request parameters, headers, claims, and
/// browser state therefore cannot promote a running process into production multi-user mode.
/// </summary>
public sealed record MultiUserMode(bool IsEnabled)
{
    /// <summary>Configuration key for the explicit production enablement switch.</summary>
    public const string ConfigurationKey = "MultiUser:Enabled";

    /// <summary>
    /// Parses only the literal values <c>true</c> and <c>false</c>. Missing configuration is the
    /// safe default (<c>false</c>); an invalid value aborts startup instead of being guessed.
    /// </summary>
    public static MultiUserMode FromConfiguration(IConfiguration configuration)
    {
        var configured = configuration[ConfigurationKey]?.Trim();
        if (string.IsNullOrEmpty(configured))
        {
            return new MultiUserMode(false);
        }

        if (!bool.TryParse(configured, out var enabled))
        {
            throw new InvalidOperationException(
                $"{ConfigurationKey} must be either 'true' or 'false'.");
        }

        return new MultiUserMode(enabled);
    }
}
