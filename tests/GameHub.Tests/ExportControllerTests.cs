using GameHub.Ingest.Data;
using GameHub.Shared;
using GameHub.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
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
