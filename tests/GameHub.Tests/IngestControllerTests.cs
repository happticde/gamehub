using GameHub.Ingest.Data;
using GameHub.Ingest.Controllers;
using GameHub.Ingest.Services;
using GameHub.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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

    [Fact]
    public async Task IngestBatch_DuplicateExternalIdInSameBatch_UpdatesPendingInsert()
    {
        // Arrange: Epic can return multiple library records for the same appName in one response.
        var controller = new EpicIngestController(
            _db,
            NullLogger<EpicIngestController>.Instance);
        using var first = JsonDocument.Parse(
            "{\"app_name\":\"Anemone\",\"app_title\":\"World of Goo\"}");
        using var second = JsonDocument.Parse(
            "{\"app_name\":\"Anemone\",\"app_title\":\"World of Goo Updated\"}");

        // Act
        var result = await controller.IngestBatch([
            first.RootElement.Clone(),
            second.RootElement.Clone()
        ]);

        // Assert: The second record updates the pending insert instead of violating the unique index.
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, GetAnonymousInt(ok.Value, "Added"));
        Assert.Equal(1, GetAnonymousInt(ok.Value, "Updated"));
        Assert.Equal(1, await _db.EpicRawItems.CountAsync());
        var stored = await _db.EpicRawItems.SingleAsync();
        Assert.Equal("Anemone", stored.AppName);
        Assert.Equal("World of Goo Updated", stored.Title);
    }

    [Fact]
    public async Task IngestBatch_EquivalentJsonPropertyOrder_DoesNotMarkItemChanged()
    {
        var controller = new SteamIngestController(
            _db,
            NullLogger<SteamIngestController>.Instance);
        using var first = JsonDocument.Parse(
            "{\"appid\":730,\"name\":\"Counter-Strike 2\",\"metadata\":{\"b\":2,\"a\":1}}");
        using var reordered = JsonDocument.Parse(
            "{\"metadata\":{\"a\":1,\"b\":2},\"name\":\"Counter-Strike 2\",\"appid\":730}");

        await controller.IngestBatch([first.RootElement.Clone()]);
        var initiallyStored = await _db.SteamRawItems.AsNoTracking().SingleAsync();
        var originalTimestamp = initiallyStored.LastUpdated;
        var originalRawJson = initiallyStored.RawJson;

        var result = await controller.IngestBatch([reordered.RootElement.Clone()]);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(0, GetAnonymousInt(ok.Value, "Updated"));
        Assert.Equal(1, GetAnonymousInt(ok.Value, "Unchanged"));
        var stored = await _db.SteamRawItems.AsNoTracking().SingleAsync();
        Assert.Equal(originalTimestamp, stored.LastUpdated);
        Assert.Equal(originalRawJson, stored.RawJson);
        Assert.Equal(RawContentHasher.Compute(first.RootElement), stored.ContentHash);
        Assert.Equal(
            RawContentHasher.Compute(first.RootElement),
            RawContentHasher.Compute(reordered.RootElement));
    }

    [Fact]
    public async Task IngestBatch_OneChangedItem_UpdatesOnlyThatRecord()
    {
        var controller = new SteamIngestController(
            _db,
            NullLogger<SteamIngestController>.Instance);
        using var first = JsonDocument.Parse("{\"appid\":1,\"name\":\"Game One\"}");
        using var second = JsonDocument.Parse("{\"appid\":2,\"name\":\"Game Two\"}");
        await controller.IngestBatch([first.RootElement.Clone(), second.RootElement.Clone()]);
        var originalTimestamps = await _db.SteamRawItems
            .AsNoTracking()
            .ToDictionaryAsync(item => item.AppId, item => item.LastUpdated);

        using var changed = JsonDocument.Parse("{\"appid\":2,\"name\":\"Game Two Updated\"}");
        var result = await controller.IngestBatch([first.RootElement.Clone(), changed.RootElement.Clone()]);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, GetAnonymousInt(ok.Value, "Updated"));
        Assert.Equal(1, GetAnonymousInt(ok.Value, "Unchanged"));
        var stored = await _db.SteamRawItems.AsNoTracking().OrderBy(item => item.AppId).ToListAsync();
        Assert.Equal(originalTimestamps[1], stored[0].LastUpdated);
        Assert.True(stored[1].LastUpdated >= originalTimestamps[2]);
        Assert.Equal("Game Two Updated", stored[1].Name);
    }

    [Fact]
    public async Task IngestBatch_AllPlatformBulkLookups_PersistContentHashes()
    {
        var steam = new SteamIngestController(_db, NullLogger<SteamIngestController>.Instance);
        var psn = new PsnIngestController(_db, NullLogger<PsnIngestController>.Instance);
        var gog = new GogIngestController(_db, NullLogger<GogIngestController>.Instance);
        var epic = new EpicIngestController(_db, NullLogger<EpicIngestController>.Instance);
        using var steamJson = JsonDocument.Parse("{\"appid\":1,\"name\":\"Steam Game\"}");
        using var psnJson = JsonDocument.Parse("{\"titleId\":\"CUSA00001\",\"name\":\"PSN Game\"}");
        using var gogJson = JsonDocument.Parse("{\"id\":2,\"title\":\"GOG Game\"}");
        using var epicJson = JsonDocument.Parse("{\"app_name\":\"EpicGame\",\"app_title\":\"Epic Game\"}");

        await steam.IngestBatch([steamJson.RootElement.Clone()]);
        await psn.IngestBatch([psnJson.RootElement.Clone()]);
        await gog.IngestBatch([gogJson.RootElement.Clone()]);
        await epic.IngestBatch([epicJson.RootElement.Clone()]);

        Assert.Equal(64, (await _db.SteamRawItems.SingleAsync()).ContentHash.Length);
        Assert.Equal(64, (await _db.PsnRawItems.SingleAsync()).ContentHash.Length);
        Assert.Equal(64, (await _db.GogRawItems.SingleAsync()).ContentHash.Length);
        Assert.Equal(64, (await _db.EpicRawItems.SingleAsync()).ContentHash.Length);
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

    private static int GetAnonymousInt(object? value, string propertyName)
    {
        Assert.NotNull(value);
        var property = value.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        return Assert.IsType<int>(property.GetValue(value));
    }
}
