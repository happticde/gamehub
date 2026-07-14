using System.Reflection;
using System.Text.Json;
using GameHub.Core.Controllers;
using GameHub.Core.Data;
using GameHub.Core.Services;
using GameHub.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameHub.Tests;

/// <summary>Coverage for the Phase 2B neutral catalogue boundary and local-first fallback.</summary>
public sealed class PhysicalProductCatalogTests : IDisposable
{
    private const string ValidGtin = "00012345000058";
    private static readonly Guid TenantA = TenantDefaults.DefaultTenantId;
    private static readonly Guid TenantB = Guid.Parse("00000000-0000-0000-0000-000000000002");

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<CoreContext> _options;

    public PhysicalProductCatalogTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<CoreContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = CreateContext(TenantA);
        db.Database.EnsureCreated();
        db.Tenants.Add(new Tenant
        {
            Id = TenantB,
            Name = "Tenant B",
            CreatedAtUtc = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc)
        });
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public void Settings_DefaultToDisabledAndRejectUnbenchmarkedProvider()
    {
        var disabled = PhysicalProductCatalogSettings.FromConfiguration(
            new ConfigurationBuilder().Build());
        Assert.Equal(PhysicalProductCatalogSettings.DisabledProvider, disabled.Provider);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), disabled.Timeout);

        var unsupported = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ProductCatalog:Provider"] = "Unbenchmarked"
            })
            .Build();
        Assert.Throws<InvalidOperationException>(() =>
            PhysicalProductCatalogSettings.FromConfiguration(unsupported));
    }

    [Fact]
    public async Task Lookup_LocalConflictsTakePriorityAndNeverCallProvider()
    {
        await using var db = CreateContext(TenantA);
        db.UnifiedGames.AddRange(
            CreateGame("First local", "first-local", PhysicalGamePlatform.PS4),
            CreateGame("Second local", "second-local", PhysicalGamePlatform.PS5));
        await db.SaveChangesAsync();

        var provider = FakeProvider.Hit("External must not run", PhysicalGamePlatform.PS3);
        var lookup = CreateLookup(db, provider);
        var result = await lookup.LookupAsync(ValidGtin);

        Assert.Equal(2, result.LocalCandidates.Count);
        Assert.Empty(result.ExternalSuggestions);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task Lookup_UnknownGtinReturnsExternalHintWithoutSelectingOrWriting()
    {
        await using var db = CreateContext(TenantA);
        var provider = FakeProvider.Hit("Suggested title", PhysicalGamePlatform.PS5);
        var controller = CreateController(db, CreateLookup(db, provider));

        var response = Assert.IsType<OkObjectResult>(
            await controller.LookupPhysicalGtin("012345000058"));
        var local = GetProperty<IReadOnlyList<PhysicalGtinCandidate>>(response.Value!, "Candidates");
        var external = GetProperty<IReadOnlyList<PhysicalProductCatalogSuggestion>>(
            response.Value!,
            "ExternalSuggestions");

        Assert.Empty(local);
        var suggestion = Assert.Single(external);
        Assert.Equal("Suggested title", suggestion.Title);
        Assert.Equal(PhysicalGamePlatform.PS5, suggestion.Platform);
        Assert.Empty(db.UnifiedGames);
        Assert.Empty(db.PhysicalGameCopies);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task Lookup_InvalidGtinNeverCallsProvider()
    {
        await using var db = CreateContext(TenantA);
        var provider = FakeProvider.Hit("Should not run", PhysicalGamePlatform.PS4);
        var controller = CreateController(db, CreateLookup(db, provider));

        var response = Assert.IsType<BadRequestObjectResult>(
            await controller.LookupPhysicalGtin("not-a-gtin"));

        Assert.Equal("invalid_gtin", GetProperty<string>(response.Value!, "Code"));
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task Lookup_OtherTenantMappingDoesNotLeakAndDoesNotSuppressProvider()
    {
        await using (var tenantB = CreateContext(TenantB))
        {
            tenantB.UnifiedGames.Add(CreateGame(
                "Tenant B private title",
                "tenant-b-private-catalogue",
                PhysicalGamePlatform.PS3));
            await tenantB.SaveChangesAsync();
        }

        await using var tenantA = CreateContext(TenantA);
        var provider = FakeProvider.Hit("Public provider hint", PhysicalGamePlatform.PS4);
        var result = await CreateLookup(tenantA, provider).LookupAsync(ValidGtin);

        Assert.Empty(result.LocalCandidates);
        Assert.Equal("Public provider hint", Assert.Single(result.ExternalSuggestions).Title);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task Client_DisabledAndNoResultFallBackSilently()
    {
        var disabled = await CreateClient(new FakeProvider(false, (_, _) =>
            Task.FromResult(new PhysicalProductCatalogResponse([]))))
            .LookupAsync(ValidGtin);
        var noResultProvider = new FakeProvider(true, (_, _) =>
            Task.FromResult(new PhysicalProductCatalogResponse([])));
        var noResult = await CreateClient(noResultProvider).LookupAsync(ValidGtin);

        Assert.Equal(PhysicalProductCatalogLookupStatus.Disabled, disabled.Status);
        Assert.Equal(PhysicalProductCatalogLookupStatus.NoResult, noResult.Status);
        Assert.Empty(disabled.Suggestions);
        Assert.Empty(noResult.Suggestions);
    }

    [Theory]
    [InlineData(PhysicalProductCatalogFailureKind.RateLimited, PhysicalProductCatalogLookupStatus.RateLimited)]
    [InlineData(PhysicalProductCatalogFailureKind.Unavailable, PhysicalProductCatalogLookupStatus.Unavailable)]
    [InlineData(PhysicalProductCatalogFailureKind.MalformedResponse, PhysicalProductCatalogLookupStatus.MalformedResponse)]
    public async Task Client_ClassifiedProviderFailuresReturnOnlySafeEmptyOutcomes(
        PhysicalProductCatalogFailureKind failure,
        PhysicalProductCatalogLookupStatus expected)
    {
        const string secretDiagnostic = "api_key=super-secret raw-provider-error";
        var logger = new CollectingLogger<PhysicalProductCatalogClient>();
        var provider = new FakeProvider(true, (_, _) =>
            throw new PhysicalProductCatalogException(failure, secretDiagnostic));
        var result = await CreateClient(provider, logger: logger).LookupAsync(ValidGtin);

        Assert.Equal(expected, result.Status);
        Assert.Empty(result.Suggestions);
        Assert.DoesNotContain(secretDiagnostic, string.Join('\n', logger.Messages), StringComparison.Ordinal);
        Assert.DoesNotContain(ValidGtin, string.Join('\n', logger.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Client_TimeoutAndMalformedPayloadReturnSafeEmptyOutcomes()
    {
        var timeoutProvider = new FakeProvider(true, async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new PhysicalProductCatalogResponse([]);
        });
        var malformedProvider = FakeProvider.Hit(" ", PhysicalGamePlatform.PS5);

        var timeout = await CreateClient(timeoutProvider, timeoutMilliseconds: 250)
            .LookupAsync(ValidGtin);
        var malformed = await CreateClient(malformedProvider).LookupAsync(ValidGtin);

        Assert.Equal(PhysicalProductCatalogLookupStatus.Timeout, timeout.Status);
        Assert.Equal(PhysicalProductCatalogLookupStatus.MalformedResponse, malformed.Status);
        Assert.Empty(timeout.Suggestions);
        Assert.Empty(malformed.Suggestions);
    }

    [Fact]
    public async Task BrowserResponse_OmitsProviderErrorsCredentialsAndProviderStatus()
    {
        const string secretDiagnostic = "Bearer credential-value raw upstream response";
        await using var db = CreateContext(TenantA);
        var provider = new FakeProvider(true, (_, _) =>
            throw new InvalidOperationException(secretDiagnostic));
        var controller = CreateController(db, CreateLookup(db, provider));

        var response = Assert.IsType<OkObjectResult>(await controller.LookupPhysicalGtin(ValidGtin));
        var json = JsonSerializer.Serialize(response.Value);

        Assert.DoesNotContain(secretDiagnostic, json, StringComparison.Ordinal);
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("providerStatus", json, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(GetProperty<IReadOnlyList<PhysicalProductCatalogSuggestion>>(
            response.Value!,
            "ExternalSuggestions"));
    }

    [Fact]
    public async Task Benchmark_UsesNeutralAdapterAndOmitsRawGtinsFromReport()
    {
        var provider = FakeProvider.Hit("Benchmark title", PhysicalGamePlatform.PS4);
        var runner = new PhysicalProductCatalogBenchmarkRunner(provider, CreateClient(provider));
        var report = await runner.RunAsync(new PhysicalProductCatalogBenchmarkDataset(
            "operator-curated",
            [
                new(
                    "eu-ps4-001",
                    ValidGtin,
                    "EU",
                    "Benchmark title",
                    PhysicalGamePlatform.PS4)
            ]));

        Assert.Equal(1, report.ExpectedOutcomeMatches);
        Assert.Equal(1, report.TitleMatches);
        Assert.Equal(1, report.PlatformMatches);
        Assert.Equal(100, report.HitRatePercent);
        Assert.Equal(100, report.ExpectedOutcomeMatchRatePercent);
        Assert.Equal(PhysicalProductCatalogLookupStatus.Hit, Assert.Single(report.Cases).Status);
        Assert.DoesNotContain(ValidGtin, JsonSerializer.Serialize(report), StringComparison.Ordinal);
    }

    private CoreContext CreateContext(Guid tenantId) => new(_options, new FixedTenantContext(tenantId));

    private static UnifiedGame CreateGame(
        string title,
        string slug,
        PhysicalGamePlatform platform) =>
        new()
        {
            Title = title,
            Slug = slug,
            PhysicalCopies =
            [
                new PhysicalGameCopy
                {
                    Platform = platform,
                    NormalizedGtin = ValidGtin
                }
            ]
        };

    private static PhysicalProductLookupService CreateLookup(
        CoreContext db,
        IPhysicalProductCatalogProvider provider) =>
        new(new PhysicalGameCaptureService(db), CreateClient(provider));

    private static PhysicalProductCatalogClient CreateClient(
        IPhysicalProductCatalogProvider provider,
        int timeoutMilliseconds = 1500,
        ILogger<PhysicalProductCatalogClient>? logger = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ProductCatalog:Provider"] = "None",
                ["ProductCatalog:TimeoutMilliseconds"] = timeoutMilliseconds.ToString()
            })
            .Build();
        return new(
            provider,
            PhysicalProductCatalogSettings.FromConfiguration(configuration),
            logger ?? NullLogger<PhysicalProductCatalogClient>.Instance);
    }

    private static LibraryController CreateController(
        CoreContext db,
        PhysicalProductLookupService lookup) =>
        new(
            null!,
            null!,
            null!,
            db,
            NullLogger<LibraryController>.Instance,
            new SyncSchedulerStatus(),
            new FixedTenantContext(TenantA),
            new ActivityLogService(db),
            null!,
            null,
            lookup);

    private static T GetProperty<T>(object value, string propertyName)
    {
        var property = value.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        Assert.NotNull(property);
        return Assert.IsAssignableFrom<T>(property!.GetValue(value));
    }

    private sealed class FixedTenantContext(Guid tenantId) : ITenantContext
    {
        public Guid TenantId { get; } = tenantId;
    }

    private sealed class FakeProvider(
        bool isConfigured,
        Func<string, CancellationToken, Task<PhysicalProductCatalogResponse>> lookup)
        : IPhysicalProductCatalogProvider
    {
        public string Name => "Fake";

        public bool IsConfigured { get; } = isConfigured;

        public int CallCount { get; private set; }

        public async Task<PhysicalProductCatalogResponse> LookupAsync(
            string normalizedGtin,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return await lookup(normalizedGtin, cancellationToken);
        }

        public static FakeProvider Hit(string title, PhysicalGamePlatform platform) =>
            new(true, (_, _) => Task.FromResult(new PhysicalProductCatalogResponse(
                [new PhysicalProductCatalogSuggestion(title, platform)])));
    }

    private sealed class CollectingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
