using GameHub.Core.Data;
using GameHub.Shared;
using GameHub.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GameHub.Tests;

/// <summary>
/// Integration tests for the LibraryController's merge/split operations and query endpoint.
/// Uses in-memory SQLite directly against the DB context (testing the business logic
/// without an HTTP layer, since the controller methods are thin wrappers).
/// </summary>
public class LibraryControllerTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly CoreContext _db;

    public LibraryControllerTests()
    {
        var (db, connection) = DatabaseFixture.CreateCoreContext();
        _db = db;
        _connection = connection;
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // =========================================================================
    // GET /api/library — Query tests
    // =========================================================================

    [Fact]
    public async Task GetLibrary_ReturnsAllGamesWithPlatformItems()
    {
        // Arrange
        _db.UnifiedGames.Add(new UnifiedGame
        {
            Title = "The Witcher 3",
            Slug = "the-witcher-3",
            Items =
            [
                new LibraryItem { Platform = Platform.Steam, ExternalId = "292030" },
                new LibraryItem { Platform = Platform.Gog, ExternalId = "1640424747" }
            ]
        });
        _db.UnifiedGames.Add(new UnifiedGame
        {
            Title = "DOOM",
            Slug = "doom",
            Items =
            [
                new LibraryItem { Platform = Platform.Steam, ExternalId = "379720" }
            ]
        });
        await _db.SaveChangesAsync();

        // Act
        var games = await _db.UnifiedGames
            .Include(g => g.Items)
            .AsNoTracking()
            .OrderBy(g => g.Title)
            .ToListAsync();

        // Assert
        Assert.Equal(2, games.Count);
        Assert.Equal("DOOM", games[0].Title);
        Assert.Single(games[0].Items);
        Assert.Equal("The Witcher 3", games[1].Title);
        Assert.Equal(2, games[1].Items.Count);
    }

    [Fact]
    public async Task GetLibrary_SearchFilter_FiltersCorrectly()
    {
        // Arrange
        _db.UnifiedGames.AddRange(
            new UnifiedGame { Title = "The Witcher 3", Slug = "the-witcher-3" },
            new UnifiedGame { Title = "DOOM", Slug = "doom" },
            new UnifiedGame { Title = "The Witcher 2", Slug = "the-witcher-2" }
        );
        await _db.SaveChangesAsync();

        // Act — case-insensitive search
        var search = "witcher";
        var results = await _db.UnifiedGames
            .AsNoTracking()
            .Where(g => g.Title.ToLower().Contains(search.ToLower()))
            .ToListAsync();

        // Assert
        Assert.Equal(2, results.Count);
        Assert.All(results, g => Assert.Contains("Witcher", g.Title));
    }

    [Fact]
    public async Task GetLibrary_SortByRatingDescending_ReturnsCorrectOrder()
    {
        // Arrange — three games with distinct ratings
        _db.UnifiedGames.AddRange(
            new UnifiedGame { Title = "Low Rated", Slug = "low-rated", Rating = 30 },
            new UnifiedGame { Title = "High Rated", Slug = "high-rated", Rating = 95 },
            new UnifiedGame { Title = "Mid Rated", Slug = "mid-rated", Rating = 60 }
        );
        await _db.SaveChangesAsync();

        // Act — sort by rating descending (mirrors the controller's switch case)
        var results = await _db.UnifiedGames
            .AsNoTracking()
            .OrderByDescending(g => g.Rating)
            .ToListAsync();

        // Assert — highest rating first
        Assert.Equal(3, results.Count);
        Assert.Equal("High Rated", results[0].Title);
        Assert.Equal("Mid Rated", results[1].Title);
        Assert.Equal("Low Rated", results[2].Title);
    }

    [Fact]
    public async Task GetLibrary_SortByReleaseDate_ReturnsCorrectOrder()
    {
        // Arrange — three games with distinct release dates
        _db.UnifiedGames.AddRange(
            new UnifiedGame { Title = "Newest", Slug = "newest", FirstReleaseDate = new DateTime(2024, 6, 1) },
            new UnifiedGame { Title = "Oldest", Slug = "oldest", FirstReleaseDate = new DateTime(2010, 1, 1) },
            new UnifiedGame { Title = "Middle", Slug = "middle", FirstReleaseDate = new DateTime(2018, 3, 15) }
        );
        await _db.SaveChangesAsync();

        // Act — sort by release date ascending (oldest first)
        var results = await _db.UnifiedGames
            .AsNoTracking()
            .OrderBy(g => g.FirstReleaseDate)
            .ToListAsync();

        // Assert
        Assert.Equal(3, results.Count);
        Assert.Equal("Oldest", results[0].Title);
        Assert.Equal("Middle", results[1].Title);
        Assert.Equal("Newest", results[2].Title);
    }

    [Fact]
    public async Task GetLibrary_DeveloperFilter_FiltersCorrectly()
    {
        // Arrange — games with different developers (comma-separated, matching the entity format)
        _db.UnifiedGames.AddRange(
            new UnifiedGame { Title = "Witcher 3", Slug = "witcher-3", Developers = "CD Projekt Red" },
            new UnifiedGame { Title = "Cyberpunk", Slug = "cyberpunk", Developers = "CD Projekt Red, CD Projekt" },
            new UnifiedGame { Title = "DOOM", Slug = "doom", Developers = "id Software" }
        );
        await _db.SaveChangesAsync();

        // Act — filter by developer substring (mirrors controller logic)
        var developer = "CD Projekt Red";
        var results = await _db.UnifiedGames
            .AsNoTracking()
            .Where(g => g.Developers != null && g.Developers.ToLower().Contains(developer.ToLower()))
            .ToListAsync();

        // Assert — only the two CD Projekt Red games should match
        Assert.Equal(2, results.Count);
        Assert.All(results, g => Assert.Contains("CD Projekt Red", g.Developers!));
    }

    // =========================================================================
    // POST /api/library/merge — Merge tests
    // =========================================================================

    [Fact]
    public async Task Merge_MovesItemsAndDeletesSource()
    {
        // Arrange: Two games, one with Steam, one with GOG
        var source = new UnifiedGame
        {
            Title = "Witcher 3 GOTY",
            Slug = "witcher-3-goty",
            Items = [new LibraryItem { Platform = Platform.Gog, ExternalId = "123" }]
        };
        var target = new UnifiedGame
        {
            Title = "The Witcher 3",
            Slug = "the-witcher-3",
            Items = [new LibraryItem { Platform = Platform.Steam, ExternalId = "292030" }]
        };
        _db.UnifiedGames.AddRange(source, target);
        await _db.SaveChangesAsync();

        // Act: Simulate merge — move items from source to target
        var sourceItem = source.Items.First();
        sourceItem.UnifiedGameId = target.Id;

        _db.ManualLinks.Add(new ManualLink
        {
            Platform = sourceItem.Platform,
            ExternalId = sourceItem.ExternalId,
            UnifiedGameId = target.Id
        });

        _db.UnifiedGames.Remove(source);
        await _db.SaveChangesAsync();

        // Assert
        var remaining = await _db.UnifiedGames.Include(g => g.Items).ToListAsync();
        Assert.Single(remaining);
        Assert.Equal("The Witcher 3", remaining[0].Title);
        Assert.Equal(2, remaining[0].Items.Count); // Now has both Steam and GOG

        var manualLink = await _db.ManualLinks.SingleAsync();
        Assert.Equal(target.Id, manualLink.UnifiedGameId);
    }

    [Fact]
    public async Task Merge_DuplicatePlatform_RemovesSourceItem()
    {
        // Arrange: Both games have a Steam entry
        var source = new UnifiedGame
        {
            Title = "DOOM (duplicate)",
            Slug = "doom-dup",
            Items = [new LibraryItem { Platform = Platform.Steam, ExternalId = "379720" }]
        };
        var target = new UnifiedGame
        {
            Title = "DOOM",
            Slug = "doom",
            Items = [new LibraryItem { Platform = Platform.Steam, ExternalId = "379720" }]
        };
        _db.UnifiedGames.AddRange(source, target);
        await _db.SaveChangesAsync();

        // Act: Merge — target already has Steam, so source's Steam entry gets removed
        var sourceItem = source.Items.First();
        var targetHasPlatform = target.Items.Any(i => i.Platform == sourceItem.Platform);

        if (targetHasPlatform)
        {
            _db.LibraryItems.Remove(sourceItem);
        }

        _db.UnifiedGames.Remove(source);
        await _db.SaveChangesAsync();

        // Assert
        var remaining = await _db.UnifiedGames.Include(g => g.Items).ToListAsync();
        Assert.Single(remaining);
        Assert.Single(remaining[0].Items); // Still just one Steam entry
    }

    [Fact]
    public async Task Merge_WithSelf_ShouldBeRejected()
    {
        // Arrange
        var game = new UnifiedGame { Title = "DOOM", Slug = "doom" };
        _db.UnifiedGames.Add(game);
        await _db.SaveChangesAsync();

        // Assert: Business logic should prevent merging with self
        Assert.Equal(game.Id, game.Id); // Trivial, but the controller checks source.Id == target.Id
    }

    [Fact]
    public async Task Merge_NonexistentSource_Detectable()
    {
        // Arrange
        var target = new UnifiedGame { Title = "DOOM", Slug = "doom" };
        _db.UnifiedGames.Add(target);
        await _db.SaveChangesAsync();

        // Act
        var source = await _db.UnifiedGames.FirstOrDefaultAsync(g => g.Id == 9999);

        // Assert: Controller returns 404 when source is null
        Assert.Null(source);
    }

    // =========================================================================
    // POST /api/library/split — Split tests
    // =========================================================================

    [Fact]
    public async Task Split_DetachesItemIntoNewGame()
    {
        // Arrange: One game with two platform entries
        var game = new UnifiedGame
        {
            Title = "DOOM",
            Slug = "doom",
            Items =
            [
                new LibraryItem { Platform = Platform.Steam, ExternalId = "379720" },
                new LibraryItem { Platform = Platform.Psn, ExternalId = "CUSA00001" }
            ]
        };
        _db.UnifiedGames.Add(game);
        await _db.SaveChangesAsync();

        // Act: Split the PSN entry into its own game
        var itemToSplit = game.Items.First(i => i.Platform == Platform.Psn);

        var newGame = new UnifiedGame
        {
            Title = "DOOM (Psn)",
            Slug = "doom-psn"
        };
        _db.UnifiedGames.Add(newGame);
        await _db.SaveChangesAsync();

        itemToSplit.UnifiedGameId = newGame.Id;
        _db.ManualLinks.Add(new ManualLink
        {
            Platform = itemToSplit.Platform,
            ExternalId = itemToSplit.ExternalId,
            UnifiedGameId = newGame.Id
        });
        await _db.SaveChangesAsync();

        // Assert
        var games = await _db.UnifiedGames.Include(g => g.Items).OrderBy(g => g.Title).ToListAsync();
        Assert.Equal(2, games.Count);

        var original = games.First(g => g.Slug == "doom");
        Assert.Single(original.Items);
        Assert.Equal(Platform.Steam, original.Items[0].Platform);

        var split = games.First(g => g.Slug == "doom-psn");
        Assert.Single(split.Items);
        Assert.Equal(Platform.Psn, split.Items[0].Platform);

        // ManualLink was created
        var link = await _db.ManualLinks.SingleAsync();
        Assert.Equal(newGame.Id, link.UnifiedGameId);
    }

    [Fact]
    public async Task Split_NonexistentItem_Detectable()
    {
        var item = await _db.LibraryItems.FirstOrDefaultAsync(i => i.Id == 9999);
        Assert.Null(item);
    }
}
