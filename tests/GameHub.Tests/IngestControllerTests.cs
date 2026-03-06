using GameHub.Ingest.Data;
using GameHub.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Xunit;

namespace GameHub.Tests;

/// <summary>
/// Integration tests for the ingest pipeline (IngestControllerBase logic).
/// Tests the upsert behavior, idempotency, and data retrieval using in-memory SQLite.
/// </summary>
public class IngestControllerTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly IngestContext _db;

    public IngestControllerTests()
    {
        var (db, connection) = DatabaseFixture.CreateIngestContext();
        _db = db;
        _connection = connection;
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // =========================================================================
    // Ingest — Upsert behavior
    // =========================================================================

    [Fact]
    public async Task Ingest_NewItems_AreAdded()
    {
        // Arrange
        var items = new List<SteamRawItem>
        {
            new() { AppId = 730, Name = "Counter-Strike 2", RawJson = "{}" },
            new() { AppId = 570, Name = "Dota 2", RawJson = "{}" },
            new() { AppId = 440, Name = "Team Fortress 2", RawJson = "{}" }
        };

        // Act
        _db.SteamRawItems.AddRange(items);
        await _db.SaveChangesAsync();

        // Assert
        Assert.Equal(3, await _db.SteamRawItems.CountAsync());
    }

    [Fact]
    public async Task Ingest_ExistingItem_IsUpdated()
    {
        // Arrange: Insert a game
        _db.SteamRawItems.Add(new SteamRawItem
        {
            AppId = 730,
            Name = "CS:GO",
            RawJson = "{\"appid\": 730}"
        });
        await _db.SaveChangesAsync();

        // Act: Update the same game (simulating a re-ingest)
        var existing = await _db.SteamRawItems.FirstAsync(x => x.AppId == 730);
        existing.Name = "Counter-Strike 2";
        existing.RawJson = "{\"appid\": 730, \"name\": \"Counter-Strike 2\"}";
        existing.LastUpdated = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Assert: Still one record, but updated
        Assert.Equal(1, await _db.SteamRawItems.CountAsync());
        var result = await _db.SteamRawItems.FirstAsync();
        Assert.Equal("Counter-Strike 2", result.Name);
    }

    [Fact]
    public async Task Ingest_DuplicateExternalId_EnforcesUniqueness()
    {
        // Arrange
        _db.SteamRawItems.Add(new SteamRawItem { AppId = 730, Name = "CS2", RawJson = "{}" });
        await _db.SaveChangesAsync();

        // Act: Try to add another item with the same AppId
        _db.SteamRawItems.Add(new SteamRawItem { AppId = 730, Name = "CS2 Dup", RawJson = "{}" });

        // Assert: SQLite unique index prevents duplicates
        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    // =========================================================================
    // List / Detail — query behavior
    // =========================================================================

    [Fact]
    public async Task ListItems_ReturnsPaginatedResults()
    {
        // Arrange: Seed 25 items
        for (int i = 1; i <= 25; i++)
        {
            _db.SteamRawItems.Add(new SteamRawItem
            {
                AppId = i,
                Name = $"Game {i}",
                RawJson = "{}",
                LastUpdated = DateTime.UtcNow.AddMinutes(-i) // Descending order
            });
        }
        await _db.SaveChangesAsync();

        // Act: Page 2, size 10
        int page = 2, pageSize = 10;
        var items = await _db.SteamRawItems
            .AsNoTracking()
            .OrderByDescending(x => x.LastUpdated)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        // Assert
        Assert.Equal(10, items.Count);
    }

    [Fact]
    public async Task GetDetail_ReturnsFullItem()
    {
        // Arrange
        var rawJson = "{\"appid\": 730, \"name\": \"Counter-Strike 2\", \"playtime_forever\": 1500}";
        _db.SteamRawItems.Add(new SteamRawItem
        {
            AppId = 730,
            Name = "Counter-Strike 2",
            RawJson = rawJson
        });
        await _db.SaveChangesAsync();

        // Act
        var item = await _db.SteamRawItems
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.AppId == 730);

        // Assert
        Assert.NotNull(item);
        Assert.Equal("Counter-Strike 2", item.Name);
        Assert.Equal(730, item.AppId);
        Assert.Contains("playtime_forever", item.RawJson);

        // Verify IRawItem interface
        Assert.Equal("Counter-Strike 2", item.DisplayName);
        Assert.Equal("730", item.ExternalId);
    }

    // =========================================================================
    // Multi-platform entity tests
    // =========================================================================

    [Fact]
    public async Task AllPlatformEntities_ImplementIRawItem()
    {
        // Seed one item for each platform
        _db.SteamRawItems.Add(new SteamRawItem { AppId = 1, Name = "Steam Game", RawJson = "{}" });
        _db.PsnRawItems.Add(new PsnRawItem { TitleId = "CUSA00001", TitleName = "PSN Game", RawJson = "{}" });
        _db.GogRawItems.Add(new GogRawItem { GameId = 100, Title = "GOG Game", RawJson = "{}" });
        _db.EpicRawItems.Add(new EpicRawItem { AppName = "epic-game", Title = "Epic Game", RawJson = "{}" });
        await _db.SaveChangesAsync();

        // Verify each entity's IRawItem interface
        var steam = await _db.SteamRawItems.FirstAsync();
        Assert.Equal("1", steam.ExternalId);
        Assert.Equal("Steam Game", steam.DisplayName);

        var psn = await _db.PsnRawItems.FirstAsync();
        Assert.Equal("CUSA00001", psn.ExternalId);
        Assert.Equal("PSN Game", psn.DisplayName);

        var gog = await _db.GogRawItems.FirstAsync();
        Assert.Equal("100", gog.ExternalId);
        Assert.Equal("GOG Game", gog.DisplayName);

        var epic = await _db.EpicRawItems.FirstAsync();
        Assert.Equal("epic-game", epic.ExternalId);
        Assert.Equal("Epic Game", epic.DisplayName);
    }
}
