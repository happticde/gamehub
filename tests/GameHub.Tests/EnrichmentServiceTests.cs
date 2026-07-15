using GameHub.Core.Data;
using GameHub.Core.Controllers;
using GameHub.Core.Services;
using GameHub.Shared;
using GameHub.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;
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
        services.AddScoped<ITenantContext, DefaultTenantContext>();
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
            Rating: 92.5,
            MatchedTitle: "Mock Title"
        );

        // Act
        var result = await service.EnrichGameAsync(game.Id);

        // Assert
        Assert.True(result);

        db.ChangeTracker.Clear();
        var updated = await db.UnifiedGames.Include(g => g.Tags).FirstOrDefaultAsync(g => g.Id == game.Id);
        Assert.NotNull(updated);
        Assert.NotNull(updated.EnrichedAt);
        Assert.Equal("https://example.com/cover.jpg", updated.CoverUrl);
        Assert.Equal("A great RPG.", updated.Summary);
        Assert.Equal("RPG, Action", updated.Genres);
        Assert.Equal(2, updated.Tags.Count);
        Assert.Contains(updated.Tags, t => t.Name == "RPG");
        Assert.Contains(updated.Tags, t => t.Name == "Action");
        Assert.Equal(92.5, updated.Rating);
        Assert.Equal(1001, updated.MetadataProviderId);
        Assert.Equal("MockProvider", updated.MetadataSource);

        var auditLog = await db.EnrichmentAuditLogs.SingleOrDefaultAsync(l => l.UnifiedGameId == game.Id);
        Assert.NotNull(auditLog);
        Assert.Equal("Success", auditLog.Status);
        Assert.Equal("MockProvider", auditLog.ProviderName);
        Assert.Equal(1001, auditLog.ProviderGameId);
        Assert.Equal("Mock Title", auditLog.MatchedTitle);
    }

    [Fact]
    public async Task EnrichGame_UpdatedGenres_PreservesCustomTagsAndReplacesAutomaticTags()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
        var service = scope.ServiceProvider.GetRequiredService<EnrichmentService>();

        var game = new UnifiedGame { Title = "Tagged game", Slug = "tagged-game" };
        var staleGenre = new Tag { Name = "Old genre" };
        var customTag = new Tag { Name = "Cozy backlog" };
        db.AddRange(game, staleGenre, customTag);
        await db.SaveChangesAsync();
        db.Set<TagAssignment>().AddRange(
            new TagAssignment
            {
                UnifiedGameId = game.Id,
                TagId = staleGenre.Id,
                IsCustom = false
            },
            new TagAssignment
            {
                UnifiedGameId = game.Id,
                TagId = customTag.Id,
                IsCustom = true
            });
        await db.SaveChangesAsync();

        _mockProvider.SearchResult = new GameMetadata(
            ProviderId: 42,
            CoverUrl: null,
            Summary: null,
            Genres: ["Action"],
            FirstReleaseDate: null,
            Rating: null,
            MatchedTitle: "Tagged game");

        Assert.True(await service.EnrichGameAsync(game.Id));

        db.ChangeTracker.Clear();
        var assignments = await db.Set<TagAssignment>()
            .Include(assignment => assignment.Tag)
            .OrderBy(assignment => assignment.Tag!.Name)
            .ToListAsync();
        Assert.Collection(
            assignments,
            assignment =>
            {
                Assert.Equal("Action", assignment.Tag!.Name);
                Assert.False(assignment.IsCustom);
            },
            assignment =>
            {
                Assert.Equal("Cozy backlog", assignment.Tag!.Name);
                Assert.True(assignment.IsCustom);
            });
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

        db.ChangeTracker.Clear();
        var updated = await db.UnifiedGames.Include(g => g.Tags).FirstOrDefaultAsync(g => g.Id == game.Id);
        Assert.NotNull(updated);
        Assert.NotNull(updated.EnrichedAt); // Marked as processed
        Assert.Null(updated.CoverUrl);      // Still null
        Assert.Null(updated.Summary);

        var auditLog = await db.EnrichmentAuditLogs.SingleOrDefaultAsync(l => l.UnifiedGameId == game.Id);
        Assert.NotNull(auditLog);
        Assert.Equal("NotFound", auditLog.Status);
        Assert.Equal("MockProvider", auditLog.ProviderName);
        Assert.Null(auditLog.ProviderGameId);
    }

    [Fact]
    public async Task EnrichGame_ProviderThrows_SkipsSettingEnrichedAt()
    {
        // Arrange
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
        var service = scope.ServiceProvider.GetRequiredService<EnrichmentService>();

        var game = new UnifiedGame { Title = "Buggy Game", Slug = "buggy-game" };
        db.UnifiedGames.Add(game);
        await db.SaveChangesAsync();

        _mockProvider.ThrowsException = true; // Simulate API failure

        // Act
        var result = await service.EnrichGameAsync(game.Id);

        // Assert
        Assert.True(result); // Service catches and returns true to move to next item
        db.ChangeTracker.Clear();
        var updated = await db.UnifiedGames.FirstOrDefaultAsync(g => g.Id == game.Id);
        Assert.NotNull(updated);
        Assert.Null(updated.EnrichedAt); // Should not mark it as enriched

        var auditLog = await db.EnrichmentAuditLogs.SingleOrDefaultAsync(l => l.UnifiedGameId == game.Id);
        Assert.NotNull(auditLog);
        Assert.Equal("Error", auditLog.Status);
        
        // Reset the mock provider for other tests if they share it, though mostly they use fresh scopes
        _mockProvider.ThrowsException = false;
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
        db.ChangeTracker.Clear();
        Assert.Equal("Old summary", (await db.UnifiedGames.FirstOrDefaultAsync(g => g.Id == game.Id))!.Summary);
        Assert.Equal(0, _mockProvider.SearchCallCount);

        // Act 2: Forced call (should proceed via search since no ProviderId)
        var result2 = await service.EnrichGameAsync(game.Id, force: true);

        // Assert 2
        Assert.True(result2);
        db.ChangeTracker.Clear();
        Assert.Equal("New summary", (await db.UnifiedGames.FirstOrDefaultAsync(g => g.Id == game.Id))!.Summary);
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
        db.ChangeTracker.Clear();
        Assert.Equal("Re-fetched by ID", (await db.UnifiedGames.FirstOrDefaultAsync(g => g.Id == game.Id))!.Summary);
    }

    // =========================================================================
    // Direct provider-ID matching
    // =========================================================================

    [Fact]
    public async Task ApplyProviderMatch_ExactResult_AtomicallyReplacesMetadataAndPreservesCustomTags()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
        var service = scope.ServiceProvider.GetRequiredService<EnrichmentService>();

        var game = new UnifiedGame
        {
            Title = "Incorrect title",
            Slug = "incorrect-title",
            Summary = "Old summary",
            MetadataProviderId = 100,
            MetadataSource = "MockProvider",
            EnrichedAt = DateTime.UtcNow.AddDays(-1)
        };
        var oldGenre = new Tag { Name = "Old genre" };
        var customTag = new Tag { Name = "Keep me", IsCustomDefinition = true };
        db.AddRange(game, oldGenre, customTag);
        await db.SaveChangesAsync();
        db.Set<TagAssignment>().AddRange(
            new TagAssignment
            {
                UnifiedGameId = game.Id,
                TagId = oldGenre.Id,
                IsCustom = false
            },
            new TagAssignment
            {
                UnifiedGameId = game.Id,
                TagId = customTag.Id,
                IsCustom = true
            });
        await db.SaveChangesAsync();

        _mockProvider.GetByIdResult = new GameMetadata(
            ProviderId: 4242,
            CoverUrl: "https://example.test/direct-cover.jpg",
            Summary: "Verified summary",
            Genres: ["Action"],
            FirstReleaseDate: new DateTime(2024, 2, 3, 0, 0, 0, DateTimeKind.Utc),
            Rating: 88.5,
            MatchedTitle: "Verified game");

        var result = await service.ApplyProviderMatchAsync(game.Id, 4242);

        Assert.Equal(DirectEnrichmentStatus.Success, result.Status);
        Assert.Equal(1, _mockProvider.GetByIdCallCount);
        db.ChangeTracker.Clear();
        var updated = await db.UnifiedGames.SingleAsync(candidate => candidate.Id == game.Id);
        Assert.Equal(4242, updated.MetadataProviderId);
        Assert.Equal("MockProvider", updated.MetadataSource);
        Assert.Equal("Verified summary", updated.Summary);
        Assert.Equal("Action", updated.Genres);
        Assert.NotNull(updated.EnrichedAt);

        var assignments = await db.Set<TagAssignment>()
            .Include(assignment => assignment.Tag)
            .OrderBy(assignment => assignment.Tag!.Name)
            .ToListAsync();
        Assert.Collection(
            assignments,
            assignment =>
            {
                Assert.Equal("Action", assignment.Tag!.Name);
                Assert.False(assignment.IsCustom);
            },
            assignment =>
            {
                Assert.Equal("Keep me", assignment.Tag!.Name);
                Assert.True(assignment.IsCustom);
            });

        var audit = await db.EnrichmentAuditLogs.SingleAsync();
        Assert.Equal("Success", audit.Status);
        Assert.Equal(4242, audit.ProviderGameId);
        Assert.Equal("Verified game", audit.MatchedTitle);
    }

    [Fact]
    public async Task ApplyProviderMatch_MissingProviderGame_PreservesExistingMetadata()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
        var service = scope.ServiceProvider.GetRequiredService<EnrichmentService>();
        var enrichedAt = DateTime.UtcNow.AddDays(-2);
        var game = new UnifiedGame
        {
            Title = "Existing game",
            Slug = "existing-game",
            Summary = "Existing summary",
            MetadataProviderId = 101,
            MetadataSource = "MockProvider",
            EnrichedAt = enrichedAt
        };
        db.UnifiedGames.Add(game);
        await db.SaveChangesAsync();

        var result = await service.ApplyProviderMatchAsync(game.Id, 9999);

        Assert.Equal(DirectEnrichmentStatus.ProviderGameNotFound, result.Status);
        db.ChangeTracker.Clear();
        var unchanged = await db.UnifiedGames.SingleAsync(candidate => candidate.Id == game.Id);
        Assert.Equal("Existing summary", unchanged.Summary);
        Assert.Equal(101, unchanged.MetadataProviderId);
        Assert.Equal(enrichedAt, unchanged.EnrichedAt);
        var audit = await db.EnrichmentAuditLogs.SingleAsync();
        Assert.Equal("NotFound", audit.Status);
        Assert.Equal(9999, audit.ProviderGameId);
    }

    [Fact]
    public async Task ApplyProviderMatch_ProviderFailure_PreservesGameAndStoresOnlySafeAuditMessage()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
        var service = scope.ServiceProvider.GetRequiredService<EnrichmentService>();
        var game = new UnifiedGame
        {
            Title = "Existing game",
            Slug = "provider-failure",
            Summary = "Private existing summary",
            MetadataProviderId = 202,
            MetadataSource = "MockProvider"
        };
        db.UnifiedGames.Add(game);
        await db.SaveChangesAsync();
        _mockProvider.ThrowsException = true;

        var result = await service.ApplyProviderMatchAsync(game.Id, 303);

        Assert.Equal(DirectEnrichmentStatus.ProviderUnavailable, result.Status);
        db.ChangeTracker.Clear();
        var unchanged = await db.UnifiedGames.SingleAsync(candidate => candidate.Id == game.Id);
        Assert.Equal("Private existing summary", unchanged.Summary);
        Assert.Equal(202, unchanged.MetadataProviderId);
        var audit = await db.EnrichmentAuditLogs.SingleAsync();
        Assert.Equal("Error", audit.Status);
        Assert.Equal("Metadata provider lookup failed.", audit.ErrorMessage);
        Assert.DoesNotContain("Simulated provider error", audit.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyProviderMatch_ForeignTenantGame_ReturnsNotFoundWithoutCallingProvider()
    {
        var tenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var options = new DbContextOptionsBuilder<CoreContext>()
            .UseSqlite(_connection)
            .Options;
        int foreignGameId;
        await using (var foreignDb = new CoreContext(options, new FixedTenantContext(tenantB)))
        {
            foreignDb.Tenants.Add(new Tenant
            {
                Id = tenantB,
                Name = "Tenant B",
                CreatedAtUtc = DateTime.UtcNow
            });
            var foreignGame = new UnifiedGame { Title = "Tenant B game", Slug = "tenant-b-game" };
            foreignDb.UnifiedGames.Add(foreignGame);
            await foreignDb.SaveChangesAsync();
            foreignGameId = foreignGame.Id;
        }

        using var scope = _scopeFactory.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<EnrichmentService>();
        var result = await service.ApplyProviderMatchAsync(foreignGameId, 404);

        Assert.Equal(DirectEnrichmentStatus.GameNotFound, result.Status);
        Assert.Equal(0, _mockProvider.GetByIdCallCount);
        await using var verifyForeign = new CoreContext(options, new FixedTenantContext(tenantB));
        Assert.Null((await verifyForeign.UnifiedGames.SingleAsync()).MetadataProviderId);
        Assert.Empty(verifyForeign.EnrichmentAuditLogs);
    }

    [Fact]
    public async Task OverrideEnrichment_MissingProviderGame_ReturnsNotFoundInsteadOfFalseSuccess()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
        var service = scope.ServiceProvider.GetRequiredService<EnrichmentService>();
        var game = new UnifiedGame
        {
            Title = "Existing game",
            Slug = "controller-not-found",
            Summary = "Existing summary",
            MetadataProviderId = 505,
            MetadataSource = "MockProvider"
        };
        db.UnifiedGames.Add(game);
        await db.SaveChangesAsync();
        var controller = new LibraryController(
            null!,
            null!,
            service,
            db,
            NullLogger<LibraryController>.Instance,
            new SyncSchedulerStatus(),
            new DefaultTenantContext(),
            new ActivityLogService(db),
            _mockProvider);

        var result = await controller.OverrideEnrichment(
            game.Id,
            new OverrideRequest { ProviderId = 606 });

        Assert.IsType<NotFoundObjectResult>(result);
        db.ChangeTracker.Clear();
        var unchanged = await db.UnifiedGames.SingleAsync(candidate => candidate.Id == game.Id);
        Assert.Equal("Existing summary", unchanged.Summary);
        Assert.Equal(505, unchanged.MetadataProviderId);
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
        public bool ThrowsException { get; set; }

        public int SearchCallCount { get; private set; }
        public int GetByIdCallCount { get; private set; }

        public Task<GameMetadata?> SearchAsync(string title, CancellationToken ct = default)
        {
            SearchCallCount++;
            if (ThrowsException) throw new Exception("Simulated provider error");
            return Task.FromResult(SearchResult);
        }

        public Task<GameMetadata?> GetByIdAsync(int providerId, CancellationToken ct = default)
        {
            GetByIdCallCount++;
            if (ThrowsException) throw new Exception("Simulated provider error");
            return Task.FromResult(GetByIdResult);
        }

        public Task<List<GameMetadata>> SearchManyAsync(string title, int limit = 10, CancellationToken ct = default)
        {
            SearchCallCount++;
            if (ThrowsException) throw new Exception("Simulated provider error");
            return Task.FromResult(SearchResult != null ? new List<GameMetadata> { SearchResult } : new List<GameMetadata>());
        }
    }

    private sealed class FixedTenantContext(Guid tenantId) : ITenantContext
    {
        public Guid TenantId { get; } = tenantId;
    }
}
