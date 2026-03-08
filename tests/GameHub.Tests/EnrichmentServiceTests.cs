using GameHub.Core.Data;
using GameHub.Core.Services;
using GameHub.Shared;
using GameHub.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameHub.Tests;

/// <summary>
/// Tests for the EnrichmentService. Uses an in-memory SQLite database
/// and a mock IGameMetadataProvider to test the business logic without
/// making actual HTTP calls to IGDB.
/// </summary>
public class EnrichmentServiceTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly MockProvider _mockProvider;

    public EnrichmentServiceTests()
    {
        var (db, connection) = DatabaseFixture.CreateCoreContext();
        _connection = connection;
        db.Dispose(); // We create fresh contexts via the scope factory

        _mockProvider = new MockProvider();

        var services = new ServiceCollection();
        services.AddDbContext<CoreContext>(o => o.UseSqlite(_connection));
        services.AddSingleton<IGameMetadataProvider>(_mockProvider);
        services.AddScoped<EnrichmentService>(sp => new EnrichmentService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IGameMetadataProvider>(),
            NullLogger<EnrichmentService>.Instance
        ));

        _serviceProvider = services.BuildServiceProvider();
        _scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
    }

    public void Dispose()
    {
        (_serviceProvider as IDisposable)?.Dispose();
        _connection.Dispose();
    }

    // =========================================================================
    // Single Game Enrichment
    // =========================================================================

    [Fact]
    public async Task EnrichGame_WithResult_SetsAllMetadataFields()
    {
        // Arrange
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
        var service = scope.ServiceProvider.GetRequiredService<EnrichmentService>();

        var game = new UnifiedGame { Title = "The Witcher 3", Slug = "the-witcher-3" };
        db.UnifiedGames.Add(game);
        await db.SaveChangesAsync();

        _mockProvider.SearchResult = new GameMetadata(
            ProviderId: 1001,
            CoverUrl: "https://example.com/cover.jpg",
            Summary: "A great RPG.",
            Genres: ["RPG", "Action"],
            FirstReleaseDate: new DateTime(2015, 5, 19, 0, 0, 0, DateTimeKind.Utc),
            Rating: 92.5
        );

        // Act
        var result = await service.EnrichGameAsync(game.Id);

        // Assert
        Assert.True(result);

        var updated = await db.UnifiedGames.FindAsync(game.Id);
        Assert.NotNull(updated);
        Assert.NotNull(updated.EnrichedAt);
        Assert.Equal("https://example.com/cover.jpg", updated.CoverUrl);
        Assert.Equal("A great RPG.", updated.Summary);
        Assert.Equal("RPG, Action", updated.Genres);
        Assert.Equal(92.5, updated.Rating);
        Assert.Equal(1001, updated.MetadataProviderId);
        Assert.Equal("MockProvider", updated.MetadataSource);
    }

    [Fact]
    public async Task EnrichGame_NoResult_SetsEnrichedAtOnly()
    {
        // Arrange
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
        var service = scope.ServiceProvider.GetRequiredService<EnrichmentService>();

        var game = new UnifiedGame { Title = "Unknown Indie Game", Slug = "unknown" };
        db.UnifiedGames.Add(game);
        await db.SaveChangesAsync();

        _mockProvider.SearchResult = null; // Simulate no results from API

        // Act
        var result = await service.EnrichGameAsync(game.Id);

        // Assert
        Assert.True(result); // True because it processed it (even if no metadata found)

        var updated = await db.UnifiedGames.FindAsync(game.Id);
        Assert.NotNull(updated);
        Assert.NotNull(updated.EnrichedAt); // Marked as processed
        Assert.Null(updated.CoverUrl);      // Still null
        Assert.Null(updated.Summary);
    }

    [Fact]
    public async Task EnrichGame_AlreadyEnriched_SkipsUnlessForced()
    {
        // Arrange
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
        var service = scope.ServiceProvider.GetRequiredService<EnrichmentService>();

        var game = new UnifiedGame
        {
            Title = "DOOM",
            Slug = "doom",
            EnrichedAt = DateTime.UtcNow.AddDays(-1),
            Summary = "Old summary"
        };
        db.UnifiedGames.Add(game);
        await db.SaveChangesAsync();

        _mockProvider.SearchResult = new GameMetadata(
            ProviderId: 2002,
            CoverUrl: null,
            Summary: "New summary",
            Genres: [],
            FirstReleaseDate: null,
            Rating: null
        );

        // Act 1: Normal call (should skip)
        var result1 = await service.EnrichGameAsync(game.Id);

        // Assert 1
        Assert.False(result1);
        Assert.Equal("Old summary", (await db.UnifiedGames.FindAsync(game.Id))!.Summary);
        Assert.Equal(0, _mockProvider.SearchCallCount);

        // Act 2: Forced call (should proceed via search since no ProviderId)
        var result2 = await service.EnrichGameAsync(game.Id, force: true);

        // Assert 2
        Assert.True(result2);
        Assert.Equal("New summary", (await db.UnifiedGames.FindAsync(game.Id))!.Summary);
        Assert.Equal(1, _mockProvider.SearchCallCount);
        Assert.Equal(0, _mockProvider.GetByIdCallCount);
    }

    [Fact]
    public async Task EnrichGame_ForceWithProviderId_UsesGetById()
    {
        // Arrange
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
        var service = scope.ServiceProvider.GetRequiredService<EnrichmentService>();

        var game = new UnifiedGame
        {
            Title = "Hades",
            Slug = "hades",
            EnrichedAt = DateTime.UtcNow,
            MetadataProviderId = 3003,
            MetadataSource = "MockProvider"
        };
        db.UnifiedGames.Add(game);
        await db.SaveChangesAsync();

        _mockProvider.GetByIdResult = new GameMetadata(
            ProviderId: 3003,
            CoverUrl: null,
            Summary: "Re-fetched by ID",
            Genres: [],
            FirstReleaseDate: null,
            Rating: 95.0
        );

        // Act (forced)
        var result = await service.EnrichGameAsync(game.Id, force: true);

        // Assert
        Assert.True(result);
        Assert.Equal(0, _mockProvider.SearchCallCount);      // Did NOT search by title
        Assert.Equal(1, _mockProvider.GetByIdCallCount);     // DID fetch by ID directly
        Assert.Equal("Re-fetched by ID", (await db.UnifiedGames.FindAsync(game.Id))!.Summary);
    }

    // =========================================================================
    // Batch Enrichment
    // =========================================================================

    [Fact]
    public async Task EnrichUnenriched_ProcessesBatch_SkipsEnriched()
    {
        // Arrange
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
        var service = scope.ServiceProvider.GetRequiredService<EnrichmentService>();

        db.UnifiedGames.AddRange(
            new UnifiedGame { Title = "G1", Slug = "g1", EnrichedAt = DateTime.UtcNow }, // Already done
            new UnifiedGame { Title = "G2", Slug = "g2", EnrichedAt = null },            // Needs it
            new UnifiedGame { Title = "G3", Slug = "g3", EnrichedAt = null },            // Needs it
            new UnifiedGame { Title = "G4", Slug = "g4", EnrichedAt = DateTime.UtcNow }, // Already done
            new UnifiedGame { Title = "G5", Slug = "g5", EnrichedAt = null }             // Needs it
        );
        await db.SaveChangesAsync();

        _mockProvider.SearchResult = new GameMetadata(
            ProviderId: 99, CoverUrl: null, Summary: "Found", Genres: [], FirstReleaseDate: null, Rating: null);

        // Act
        var processedCount = await service.EnrichUnenrichedAsync(batchSize: 2); // Only take 2

        // Assert
        Assert.Equal(2, processedCount);
        Assert.Equal(2, _mockProvider.SearchCallCount);

        var unenrichedLeft = await db.UnifiedGames.CountAsync(g => g.EnrichedAt == null);
        Assert.Equal(1, unenrichedLeft); // Started with 3, processed 2, 1 left
    }

    // =========================================================================
    // Mock Provider
    // =========================================================================

    private class MockProvider : IGameMetadataProvider
    {
        public string ProviderName => "MockProvider";

        public GameMetadata? SearchResult { get; set; }
        public GameMetadata? GetByIdResult { get; set; }

        public int SearchCallCount { get; private set; }
        public int GetByIdCallCount { get; private set; }

        public Task<GameMetadata?> SearchAsync(string title, CancellationToken ct = default)
        {
            SearchCallCount++;
            return Task.FromResult(SearchResult);
        }

        public Task<GameMetadata?> GetByIdAsync(int providerId, CancellationToken ct = default)
        {
            GetByIdCallCount++;
            return Task.FromResult(GetByIdResult);
        }
    }
}
