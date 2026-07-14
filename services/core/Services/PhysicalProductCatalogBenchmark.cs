using GameHub.Core.Data;

namespace GameHub.Core.Services;

/// <summary>One operator-curated benchmark case with known release provenance.</summary>
public sealed record PhysicalProductCatalogBenchmarkCase(
    string CaseId,
    string Gtin,
    string Region,
    string ExpectedTitle,
    PhysicalGamePlatform ExpectedPlatform,
    bool ExpectedResult = true);

/// <summary>Input document consumed by the provider-neutral benchmark harness.</summary>
public sealed record PhysicalProductCatalogBenchmarkDataset(
    string DatasetName,
    IReadOnlyList<PhysicalProductCatalogBenchmarkCase> Cases);

/// <summary>Safe per-case benchmark output. Raw GTINs and provider payloads are omitted.</summary>
public sealed record PhysicalProductCatalogBenchmarkCaseResult(
    string CaseId,
    string Region,
    PhysicalGamePlatform ExpectedPlatform,
    PhysicalProductCatalogLookupStatus Status,
    long ElapsedMilliseconds,
    bool ExpectedOutcomeMatched,
    bool TitleMatched,
    bool PlatformMatched);

/// <summary>Measured benchmark report. Every ratio derives only from the supplied dataset and run.</summary>
public sealed record PhysicalProductCatalogBenchmarkReport(
    string DatasetName,
    string Provider,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    int TotalCases,
    IReadOnlyDictionary<PhysicalProductCatalogLookupStatus, int> OutcomeCounts,
    int HitCount,
    int ExpectedOutcomeMatches,
    int TitleMatches,
    int PlatformMatches,
    double HitRatePercent,
    double ExpectedOutcomeMatchRatePercent,
    double TitleMatchRatePercent,
    double PlatformMatchRatePercent,
    double? MedianLatencyMilliseconds,
    double? P95LatencyMilliseconds,
    IReadOnlyList<PhysicalProductCatalogBenchmarkCaseResult> Cases);

/// <summary>Executes a reproducible, provider-neutral benchmark without live calls from tests.</summary>
public sealed class PhysicalProductCatalogBenchmarkRunner
{
    private readonly IPhysicalProductCatalogProvider _provider;
    private readonly PhysicalProductCatalogClient _client;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes the benchmark runner.</summary>
    public PhysicalProductCatalogBenchmarkRunner(
        IPhysicalProductCatalogProvider provider,
        PhysicalProductCatalogClient client,
        TimeProvider? timeProvider = null)
    {
        _provider = provider;
        _client = client;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Runs every case sequentially so provider rate limits and latency remain observable.</summary>
    public async Task<PhysicalProductCatalogBenchmarkReport> RunAsync(
        PhysicalProductCatalogBenchmarkDataset dataset,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataset.DatasetName);
        ArgumentNullException.ThrowIfNull(dataset.Cases);

        var startedAt = _timeProvider.GetUtcNow();
        var results = new List<PhysicalProductCatalogBenchmarkCaseResult>(dataset.Cases.Count);
        var seenCaseIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var benchmarkCase in dataset.Cases)
        {
            ValidateCase(benchmarkCase, seenCaseIds);
            var normalization = GtinNormalizer.Normalize(benchmarkCase.Gtin);
            if (!normalization.IsValid)
            {
                results.Add(new(
                    benchmarkCase.CaseId,
                    benchmarkCase.Region,
                    benchmarkCase.ExpectedPlatform,
                    PhysicalProductCatalogLookupStatus.InvalidBenchmarkInput,
                    0,
                    false,
                    false,
                    false));
                continue;
            }

            var attempt = await _client.LookupAsync(
                normalization.NormalizedGtin!,
                cancellationToken);
            var titleMatched = attempt.Suggestions.Any(suggestion =>
                string.Equals(
                    suggestion.Title,
                    benchmarkCase.ExpectedTitle,
                    StringComparison.OrdinalIgnoreCase));
            var platformMatched = attempt.Suggestions.Any(suggestion =>
                suggestion.Platform == benchmarkCase.ExpectedPlatform);
            var expectedOutcomeMatched = benchmarkCase.ExpectedResult
                ? attempt.Status == PhysicalProductCatalogLookupStatus.Hit
                    && titleMatched
                    && platformMatched
                : attempt.Status == PhysicalProductCatalogLookupStatus.NoResult;

            results.Add(new(
                benchmarkCase.CaseId,
                benchmarkCase.Region,
                benchmarkCase.ExpectedPlatform,
                attempt.Status,
                attempt.ElapsedMilliseconds,
                expectedOutcomeMatched,
                titleMatched,
                platformMatched));
        }

        var measuredLatencies = results
            .Where(result => result.Status is not (
                PhysicalProductCatalogLookupStatus.Disabled
                or PhysicalProductCatalogLookupStatus.InvalidBenchmarkInput))
            .Select(result => result.ElapsedMilliseconds)
            .OrderBy(value => value)
            .ToArray();
        var hitCount = results.Count(result => result.Status == PhysicalProductCatalogLookupStatus.Hit);
        var expectedOutcomeMatches = results.Count(result => result.ExpectedOutcomeMatched);
        var titleMatches = results.Count(result => result.TitleMatched);
        var platformMatches = results.Count(result => result.PlatformMatched);

        return new(
            dataset.DatasetName,
            _provider.Name,
            startedAt,
            _timeProvider.GetUtcNow(),
            results.Count,
            results.GroupBy(result => result.Status)
                .ToDictionary(group => group.Key, group => group.Count()),
            hitCount,
            expectedOutcomeMatches,
            titleMatches,
            platformMatches,
            Percentage(hitCount, results.Count),
            Percentage(expectedOutcomeMatches, results.Count),
            Percentage(titleMatches, results.Count),
            Percentage(platformMatches, results.Count),
            Percentile(measuredLatencies, 0.5),
            Percentile(measuredLatencies, 0.95),
            results);
    }

    private static double Percentage(int count, int total) =>
        total == 0 ? 0 : Math.Round((double)count / total * 100, 2);

    private static void ValidateCase(
        PhysicalProductCatalogBenchmarkCase benchmarkCase,
        HashSet<string> seenCaseIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(benchmarkCase.CaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(benchmarkCase.Region);
        ArgumentException.ThrowIfNullOrWhiteSpace(benchmarkCase.ExpectedTitle);
        if (!Enum.IsDefined(benchmarkCase.ExpectedPlatform))
        {
            throw new ArgumentException("Benchmark cases must use PS3, PS4, or PS5.");
        }

        if (!seenCaseIds.Add(benchmarkCase.CaseId))
        {
            throw new ArgumentException($"Duplicate benchmark case ID '{benchmarkCase.CaseId}'.");
        }
    }

    private static double? Percentile(IReadOnlyList<long> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0)
        {
            return null;
        }

        var index = (sortedValues.Count - 1) * percentile;
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);
        if (lower == upper)
        {
            return sortedValues[lower];
        }

        var fraction = index - lower;
        return sortedValues[lower] + ((sortedValues[upper] - sortedValues[lower]) * fraction);
    }
}
