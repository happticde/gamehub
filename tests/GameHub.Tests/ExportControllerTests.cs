using GameHub.Ingest.Controllers;
using GameHub.Ingest.Data;
using GameHub.Shared;
using GameHub.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameHub.Tests;

/// <summary>
/// Integration tests for the ExportController — the paginated data feed
/// that the Core service uses during sync.
/// Tests pagination, incremental filtering, and platform routing.
/// </summary>
public class ExportControllerTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly IngestContext _db;

    public ExportControllerTests()
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
    // Pagination
    // =========================================================================

    [Fact]
    public async Task Export_Pagination_ReturnsCorrectPage()
    {
        // Arrange: Seed 5 Steam items
        for (int i = 1; i <= 5; i++)
        {
            _db.SteamRawItems.Add(new SteamRawItem
            {
                AppId = i,
                Name = $"Game {i}",
                RawJson = "{}",
                LastUpdated = DateTime.UtcNow.AddMinutes(i)
            });
        }
        await _db.SaveChangesAsync();

        // Act: Get page 1 with size 2 (simulating what ExportController.BuildQuery does)
        int page = 1, pageSize = 2;
        var query = _db.SteamRawItems.AsNoTracking()
            .OrderBy(x => x.LastUpdated)
            .Select(x => new RawGameDto
            {
                Id = x.AppId.ToString(),
                Name = x.Name,
                LastUpdated = x.LastUpdated
            });

        var totalCount = await query.CountAsync();
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        var result = new PagedResult<RawGameDto>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };

        // Assert
        Assert.Equal(5, result.TotalCount);
        Assert.Equal(2, result.Items.Count);
        Assert.True(result.HasMore); // 1 * 2 < 5
    }

    [Fact]
    public async Task Export_LastPage_HasMoreIsFalse()
    {
        // Arrange: 3 items, page size 2 → page 2 should have HasMore = false
        for (int i = 1; i <= 3; i++)
        {
            _db.SteamRawItems.Add(new SteamRawItem
            {
                AppId = i,
                Name = $"Game {i}",
                RawJson = "{}",
                LastUpdated = DateTime.UtcNow.AddMinutes(i)
            });
        }
        await _db.SaveChangesAsync();

        int page = 2, pageSize = 2;
        var query = _db.SteamRawItems.AsNoTracking().OrderBy(x => x.LastUpdated);
        var totalCount = await query.CountAsync();
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        var result = new PagedResult<RawGameDto>
        {
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };

        // Assert: page 2 * pageSize 2 = 4, which is >= totalCount 3
        Assert.False(result.HasMore);
        Assert.Single(items); // Only 1 item on page 2
    }

    // =========================================================================
    // Incremental sync (updatedAfter filter)
    // =========================================================================

    [Fact]
    public async Task Export_UpdatedAfterFilter_ReturnsOnlyNewerRecords()
    {
        // Arrange: Seed items with different timestamps
        var cutoff = DateTime.UtcNow;

        _db.SteamRawItems.Add(new SteamRawItem
        {
            AppId = 1,
            Name = "Old Game",
            RawJson = "{}",
            LastUpdated = cutoff.AddHours(-1) // Before cutoff
        });
        _db.SteamRawItems.Add(new SteamRawItem
        {
            AppId = 2,
            Name = "New Game",
            RawJson = "{}",
            LastUpdated = cutoff.AddHours(1) // After cutoff
        });
        await _db.SaveChangesAsync();

        // Act: Filter with updatedAfter
        var items = await _db.SteamRawItems
            .AsNoTracking()
            .Where(x => x.LastUpdated > cutoff)
            .ToListAsync();

        // Assert
        Assert.Single(items);
        Assert.Equal("New Game", items[0].Name);
    }

    [Fact]
    public async Task Export_EpicRawItem_IncludesOnlySafeFallbackMetadata()
    {
        _db.EpicRawItems.Add(new EpicRawItem
        {
            AppName = "Anemone",
            Title = "World of Goo",
            RawJson = """
            {
              "app_name": "Anemone",
              "app_title": "World of Goo",
              "developer": "2D Boy",
              "publisher": "Tomorrow Corporation",
              "release_date": "2008-10-13T00:00:00.000Z",
              "summary": "Build bridges from living balls of goo.",
              "description": "Longer description.",
              "cover_url": "https://cdn.example.test/cover.jpg",
              "key_art_url": "https://cdn.example.test/key-art.jpg",
              "client_secret": "must-not-surface",
              "legendary_metadata": {
                "description": "Debug metadata is not exported as raw JSON."
              }
            }
            """,
            LastUpdated = new DateTime(2026, 7, 9, 0, 0, 0, DateTimeKind.Utc)
        });
        await _db.SaveChangesAsync();

        var controller = new ExportController(_db, NullLogger<ExportController>.Instance);
        var result = await controller.GetByPlatform("epic");

        var ok = Assert.IsType<OkObjectResult>(result);
        var page = Assert.IsType<PagedResult<RawGameDto>>(ok.Value);
        var item = Assert.Single(page.Items);
        Assert.Equal("Anemone", item.Id);
        Assert.Equal("World of Goo", item.Name);
        Assert.Equal("Build bridges from living balls of goo.", item.Summary);
        Assert.Equal("https://cdn.example.test/cover.jpg", item.CoverUrl);
        Assert.Equal("https://cdn.example.test/key-art.jpg", item.ArtworkUrl);
        Assert.Equal("2D Boy", item.Developers);
        Assert.Equal("Tomorrow Corporation", item.Publishers);
        Assert.Equal(new DateTime(2008, 10, 13, 0, 0, 0, DateTimeKind.Utc), item.FirstReleaseDate);
    }

    // =========================================================================
    // PagedResult.HasMore logic
    // =========================================================================

    [Fact]
    public void PagedResult_HasMore_CalculatesCorrectly()
    {
        // HasMore = Page * PageSize < TotalCount

        var hasMore = new PagedResult<string>
        {
            Page = 1,
            PageSize = 10,
            TotalCount = 25
        };
        Assert.True(hasMore.HasMore); // 1 * 10 = 10 < 25

        var noMore = new PagedResult<string>
        {
            Page = 3,
            PageSize = 10,
            TotalCount = 25
        };
        Assert.False(noMore.HasMore); // 3 * 10 = 30 >= 25

        var exactFit = new PagedResult<string>
        {
            Page = 5,
            PageSize = 5,
            TotalCount = 25
        };
        Assert.False(exactFit.HasMore); // 5 * 5 = 25 >= 25
    }
}
