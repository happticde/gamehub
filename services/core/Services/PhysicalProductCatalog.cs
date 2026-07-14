using System.Diagnostics;
using GameHub.Core.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GameHub.Core.Services;

/// <summary>
/// Server-only configuration for optional physical-product catalogue lookup. Phase 2B deliberately
/// ships with only the neutral <c>None</c> provider until licensing and representative coverage are
/// verified.
/// </summary>
public sealed class PhysicalProductCatalogSettings
{
    /// <summary>The configuration section used by Core and the benchmark harness.</summary>
    public const string SectionName = "ProductCatalog";

    /// <summary>The only production provider accepted before a catalogue is selected.</summary>
    public const string DisabledProvider = "None";

    private PhysicalProductCatalogSettings(string provider, TimeSpan timeout)
    {
        Provider = provider;
        Timeout = timeout;
    }

    /// <summary>Configured provider name. Phase 2B accepts only <c>None</c>.</summary>
    public string Provider { get; }

    /// <summary>Maximum wall-clock duration of one provider lookup.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>Parses and validates provider configuration without reading any credential value.</summary>
    public static PhysicalProductCatalogSettings FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        var provider = section["Provider"]?.Trim();
        provider = string.IsNullOrEmpty(provider) ? DisabledProvider : provider;

        if (!string.Equals(provider, DisabledProvider, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Unsupported {SectionName}:Provider value. Phase 2B accepts only '{DisabledProvider}' " +
                "until a provider passes the documented coverage, licensing, and operations benchmark.");
        }

        var timeoutMilliseconds = 1500;
        var configuredTimeout = section["TimeoutMilliseconds"];
        if (!string.IsNullOrWhiteSpace(configuredTimeout)
            && (!int.TryParse(configuredTimeout, out timeoutMilliseconds)
                || timeoutMilliseconds is < 250 or > 5000))
        {
            throw new InvalidOperationException(
                $"{SectionName}:TimeoutMilliseconds must be an integer between 250 and 5000.");
        }

        return new PhysicalProductCatalogSettings(
            DisabledProvider,
            TimeSpan.FromMilliseconds(timeoutMilliseconds));
    }
}

/// <summary>Provider-neutral server-side contract for one canonical GTIN lookup.</summary>
public interface IPhysicalProductCatalogProvider
{
    /// <summary>Safe provider label for benchmark output. It must never contain credentials.</summary>
    string Name { get; }

    /// <summary>Whether this provider has complete server-side configuration.</summary>
    bool IsConfigured { get; }

    /// <summary>Looks up untrusted title and platform suggestions for a canonical GTIN-14.</summary>
    Task<PhysicalProductCatalogResponse> LookupAsync(
        string normalizedGtin,
        CancellationToken cancellationToken = default);
}

/// <summary>Provider response before GameHub validates and bounds its untrusted fields.</summary>
public sealed record PhysicalProductCatalogResponse(
    IReadOnlyList<PhysicalProductCatalogSuggestion> Suggestions);

/// <summary>One untrusted external title and optional supported-platform hint.</summary>
public sealed record PhysicalProductCatalogSuggestion(
    string Title,
    PhysicalGamePlatform? Platform);

/// <summary>Expected failure classes which provider-specific adapters must map without raw details.</summary>
public enum PhysicalProductCatalogFailureKind
{
    /// <summary>The provider rejected the request because a quota or rate limit was reached.</summary>
    RateLimited,

    /// <summary>The provider returned a temporary upstream or transport failure.</summary>
    Unavailable,

    /// <summary>The provider returned a payload that did not satisfy its documented contract.</summary>
    MalformedResponse
}

/// <summary>
/// Typed provider failure. Messages are diagnostic input only and are intentionally never logged or
/// returned by the neutral client.
/// </summary>
public sealed class PhysicalProductCatalogException : Exception
{
    /// <summary>Initializes a classified provider failure.</summary>
    public PhysicalProductCatalogException(
        PhysicalProductCatalogFailureKind kind,
        string? diagnosticMessage = null,
        Exception? innerException = null)
        : base(diagnosticMessage ?? "Physical product catalogue request failed.", innerException)
    {
        Kind = kind;
    }

    /// <summary>Secret-free failure class used for fallback behavior.</summary>
    public PhysicalProductCatalogFailureKind Kind { get; }
}

/// <summary>Safe outcome categories shared by request handling and the benchmark harness.</summary>
public enum PhysicalProductCatalogLookupStatus
{
    /// <summary>No provider is configured.</summary>
    Disabled,

    /// <summary>At least one validated suggestion was returned.</summary>
    Hit,

    /// <summary>The provider completed successfully without a suggestion.</summary>
    NoResult,

    /// <summary>The configured short timeout elapsed.</summary>
    Timeout,

    /// <summary>The provider reported rate limiting.</summary>
    RateLimited,

    /// <summary>The provider or transport was temporarily unavailable.</summary>
    Unavailable,

    /// <summary>The response did not satisfy the bounded neutral contract.</summary>
    MalformedResponse,

    /// <summary>A benchmark case contained a GTIN outside GameHub's accepted normalization rules.</summary>
    InvalidBenchmarkInput
}

/// <summary>Validated provider attempt with no credentials, raw GTIN, payload, or error text.</summary>
public sealed record PhysicalProductCatalogAttempt(
    PhysicalProductCatalogLookupStatus Status,
    IReadOnlyList<PhysicalProductCatalogSuggestion> Suggestions,
    long ElapsedMilliseconds);

/// <summary>
/// Applies the common timeout, failure, and payload boundary around every provider-specific adapter.
/// </summary>
public sealed class PhysicalProductCatalogClient
{
    private const int MaxSuggestions = 5;
    private const int MaxTitleLength = 200;

    private readonly IPhysicalProductCatalogProvider _provider;
    private readonly PhysicalProductCatalogSettings _settings;
    private readonly ILogger<PhysicalProductCatalogClient> _logger;

    /// <summary>Initializes the bounded catalogue client.</summary>
    public PhysicalProductCatalogClient(
        IPhysicalProductCatalogProvider provider,
        PhysicalProductCatalogSettings settings,
        ILogger<PhysicalProductCatalogClient> logger)
    {
        _provider = provider;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Looks up one already-normalized GTIN and returns only safe outcome data.</summary>
    public async Task<PhysicalProductCatalogAttempt> LookupAsync(
        string normalizedGtin,
        CancellationToken cancellationToken = default)
    {
        if (!_provider.IsConfigured)
        {
            return new(
                PhysicalProductCatalogLookupStatus.Disabled,
                [],
                0);
        }

        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_settings.Timeout);

        try
        {
            var response = await _provider.LookupAsync(normalizedGtin, timeout.Token);
            if (!TryValidate(response, out var suggestions))
            {
                return Complete(PhysicalProductCatalogLookupStatus.MalformedResponse, [], stopwatch);
            }

            return suggestions.Count == 0
                ? Complete(PhysicalProductCatalogLookupStatus.NoResult, [], stopwatch)
                : Complete(PhysicalProductCatalogLookupStatus.Hit, suggestions, stopwatch);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Complete(PhysicalProductCatalogLookupStatus.Timeout, [], stopwatch);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PhysicalProductCatalogException exception)
        {
            var status = exception.Kind switch
            {
                PhysicalProductCatalogFailureKind.RateLimited =>
                    PhysicalProductCatalogLookupStatus.RateLimited,
                PhysicalProductCatalogFailureKind.MalformedResponse =>
                    PhysicalProductCatalogLookupStatus.MalformedResponse,
                _ => PhysicalProductCatalogLookupStatus.Unavailable
            };
            return Complete(status, [], stopwatch);
        }
        catch (Exception)
        {
            // Provider implementations are untrusted operational boundaries. The raw exception is
            // deliberately discarded so provider URLs, payloads, keys, and diagnostics cannot leak.
            return Complete(PhysicalProductCatalogLookupStatus.Unavailable, [], stopwatch);
        }
    }

    private PhysicalProductCatalogAttempt Complete(
        PhysicalProductCatalogLookupStatus status,
        IReadOnlyList<PhysicalProductCatalogSuggestion> suggestions,
        Stopwatch stopwatch)
    {
        stopwatch.Stop();
        _logger.LogInformation(
            "Physical product catalogue lookup completed with safe outcome {CatalogueOutcome}",
            status);
        return new(status, suggestions, stopwatch.ElapsedMilliseconds);
    }

    private static bool TryValidate(
        PhysicalProductCatalogResponse? response,
        out IReadOnlyList<PhysicalProductCatalogSuggestion> suggestions)
    {
        suggestions = [];
        if (response?.Suggestions is null || response.Suggestions.Count > MaxSuggestions)
        {
            return false;
        }

        var validated = new List<PhysicalProductCatalogSuggestion>(response.Suggestions.Count);
        foreach (var suggestion in response.Suggestions)
        {
            if (suggestion is null)
            {
                return false;
            }

            var title = suggestion.Title?.Trim();
            if (string.IsNullOrEmpty(title)
                || title.Length > MaxTitleLength
                || suggestion.Platform is { } platform && !Enum.IsDefined(platform))
            {
                return false;
            }

            if (!validated.Any(candidate =>
                    string.Equals(candidate.Title, title, StringComparison.OrdinalIgnoreCase)
                    && candidate.Platform == suggestion.Platform))
            {
                validated.Add(new(title, suggestion.Platform));
            }
        }

        suggestions = validated;
        return true;
    }
}

/// <summary>Combined tenant-local-first lookup result used by the authenticated API.</summary>
public sealed record PhysicalProductLookupResult(
    IReadOnlyList<PhysicalGtinCandidate> LocalCandidates,
    IReadOnlyList<PhysicalProductCatalogSuggestion> ExternalSuggestions);

/// <summary>
/// Keeps confirmed tenant-owned mappings authoritative and contacts the optional external adapter
/// only when no local mapping exists.
/// </summary>
public sealed class PhysicalProductLookupService
{
    private readonly PhysicalGameCaptureService _physicalCapture;
    private readonly PhysicalProductCatalogClient _catalogue;

    /// <summary>Initializes local-first product lookup.</summary>
    public PhysicalProductLookupService(
        PhysicalGameCaptureService physicalCapture,
        PhysicalProductCatalogClient catalogue)
    {
        _physicalCapture = physicalCapture;
        _catalogue = catalogue;
    }

    /// <summary>Returns local candidates first and optional external hints only for a local miss.</summary>
    public async Task<PhysicalProductLookupResult> LookupAsync(
        string normalizedGtin,
        CancellationToken cancellationToken = default)
    {
        var localCandidates = await _physicalCapture.LookupAsync(normalizedGtin, cancellationToken);
        if (localCandidates.Count > 0)
        {
            return new(localCandidates, []);
        }

        var external = await _catalogue.LookupAsync(normalizedGtin, cancellationToken);
        return new(
            localCandidates,
            external.Status == PhysicalProductCatalogLookupStatus.Hit ? external.Suggestions : []);
    }
}

/// <summary>Neutral disabled provider used when no production catalogue has passed evaluation.</summary>
public sealed class DisabledPhysicalProductCatalogProvider : IPhysicalProductCatalogProvider
{
    /// <inheritdoc />
    public string Name => PhysicalProductCatalogSettings.DisabledProvider;

    /// <inheritdoc />
    public bool IsConfigured => false;

    /// <inheritdoc />
    public Task<PhysicalProductCatalogResponse> LookupAsync(
        string normalizedGtin,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new PhysicalProductCatalogResponse([]));
}

/// <summary>Registers the same neutral catalogue boundary for Core and the benchmark harness.</summary>
public static class PhysicalProductCatalogServiceCollectionExtensions
{
    /// <summary>Adds validated Phase 2B product-catalogue services.</summary>
    public static IServiceCollection AddPhysicalProductCatalog(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var settings = PhysicalProductCatalogSettings.FromConfiguration(configuration);
        services.AddSingleton(settings);
        services.AddSingleton<IPhysicalProductCatalogProvider, DisabledPhysicalProductCatalogProvider>();
        services.AddSingleton<PhysicalProductCatalogClient>();
        services.AddSingleton<PhysicalProductCatalogBenchmarkRunner>();
        services.AddScoped<PhysicalProductLookupService>();
        return services;
    }
}
