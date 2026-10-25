using GameHub.Ingest.Controllers;
using GameHub.Ingest.Data;
using GameHub.Shared;
using GameHub.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
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
    public async Task Export_AllStandardPlatforms_IncludeContentHash()
    {
        _db.SteamRawItems.Add(new SteamRawItem
        {
            AppId = 1,
            Name = "Steam Game",
            RawJson = "{}",
            ContentHash = "steam-hash"
        });
        _db.PsnRawItems.Add(new PsnRawItem
        {
            TitleId = "CUSA00001",
            TitleName = "PSN Game",
            RawJson = "{}",
            ContentHash = "psn-hash"
        });
        _db.GogRawItems.Add(new GogRawItem
        {
            GameId = 2,
            Title = "GOG Game",
            RawJson = "{}",
            ContentHash = "gog-hash"
        });
        await _db.SaveChangesAsync();
        var controller = new ExportController(_db, NullLogger<ExportController>.Instance);

        var steam = Assert.IsType<PagedResult<RawGameDto>>(
            Assert.IsType<OkObjectResult>(await controller.GetByPlatform("steam")).Value);
        var psn = Assert.IsType<PagedResult<RawGameDto>>(
            Assert.IsType<OkObjectResult>(await controller.GetByPlatform("psn")).Value);
        var gog = Assert.IsType<PagedResult<RawGameDto>>(
            Assert.IsType<OkObjectResult>(await controller.GetByPlatform("gog")).Value);

        Assert.Equal("steam-hash", Assert.Single(steam.Items).ContentHash);
        Assert.Equal("psn-hash", Assert.Single(psn.Items).ContentHash);
        Assert.Equal("gog-hash", Assert.Single(gog.Items).ContentHash);
    }

    [Fact]
    public async Task Export_UnknownPlatformContainingNewline_IsNotLoggedAndResponseIsPreserved()
    {
        const string untrustedPlatform = "unknown-platform-3ce1\r\nCRITICAL forged log entry";
        var logger = new RecordingLogger<ExportController>();
        var controller = new ExportController(_db, logger);

        var invalid = Assert.IsType<BadRequestObjectResult>(
            await controller.GetByPlatform(untrustedPlatform));
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(invalid.Value));

        Assert.Equal(
            $"Unknown platform: {untrustedPlatform}. Use steam, psn, gog, or epic.",
            body.RootElement.GetProperty("Message").GetString());
        var warning = Assert.Single(logger.Records);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal("Export request rejected for an unsupported platform", warning.Message);
        Assert.DoesNotContain(untrustedPlatform, warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("forged log entry", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(logger.Records, entry =>
            entry.Properties.Any(property =>
                property.Value?.ToString()?.Contains("forged log entry", StringComparison.Ordinal) == true));

        var valid = Assert.IsType<OkObjectResult>(await controller.GetByPlatform("StEaM"));
        Assert.IsType<PagedResult<RawGameDto>>(valid.Value);
        var debug = Assert.Single(logger.Records, entry => entry.Level == LogLevel.Debug);
        Assert.Contains("Export: steam", debug.Message, StringComparison.Ordinal);
        Assert.Contains(debug.Properties, property => property.Key == "Platform" && property.Value as string == "steam");
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
            ContentHash = "epic-content-hash",
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
        Assert.Equal("epic-content-hash", item.ContentHash);
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

    [Theory]
    [InlineData("steam")]
    [InlineData("psn")]
    [InlineData("gog")]
    [InlineData("epic")]
    public async Task Export_InvalidPagination_UsesBoundedSizeAndDoesNotWrapHugePages(string platform)
    {
        SeedPlatformsWithEqualTimestamps();
        await _db.SaveChangesAsync();
        var controller = new ExportController(_db, NullLogger<ExportController>.Instance);

        foreach (var invalidSize in new[] { 0, -1, int.MinValue })
        {
            var page = Assert.IsType<PagedResult<RawGameDto>>(
                Assert.IsType<OkObjectResult>(await controller.GetByPlatform(platform, -1, invalidSize)).Value);
            Assert.Equal(1, page.Page);
            Assert.Equal(200, page.PageSize);
            Assert.Equal(3, page.Items.Count);
            Assert.False(page.HasMore);
        }

        var beyondEnd = Assert.IsType<PagedResult<RawGameDto>>(
            Assert.IsType<OkObjectResult>(await controller.GetByPlatform(platform, int.MaxValue, int.MaxValue)).Value);
        Assert.Equal(int.MaxValue, beyondEnd.Page);
        Assert.Equal(500, beyondEnd.PageSize);
        Assert.Equal(3, beyondEnd.TotalCount);
        Assert.Empty(beyondEnd.Items);
        Assert.False(beyondEnd.HasMore);
    }

    [Theory]
    [InlineData("steam")]
    [InlineData("psn")]
    [InlineData("gog")]
    [InlineData("epic")]
    public async Task Export_EqualTimestamps_ReturnsEachItemOnceInIdOrder(string platform)
    {
        SeedPlatformsWithEqualTimestamps();
        await _db.SaveChangesAsync();
        // Deliberately offer a different tie order through an index. Results must stay stable
        // regardless of which query plan the database chooses for identical timestamps.
        await _db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IX_Review_Steam ON SteamRawItems (TenantId, LastUpdated, Id DESC)");
        await _db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IX_Review_Psn ON PsnRawItems (TenantId, LastUpdated, Id DESC)");
        await _db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IX_Review_Gog ON GogRawItems (TenantId, LastUpdated, Id DESC)");
        await _db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IX_Review_Epic ON EpicRawItems (TenantId, LastUpdated, Id DESC)");
        var controller = new ExportController(_db, NullLogger<ExportController>.Instance);
        var ids = new List<string>();

        for (var pageNumber = 1; pageNumber <= 3; pageNumber++)
        {
            var page = Assert.IsType<PagedResult<RawGameDto>>(
                Assert.IsType<OkObjectResult>(await controller.GetByPlatform(platform, pageNumber, 1)).Value);
            ids.Add(Assert.Single(page.Items).Id);
            Assert.Equal(pageNumber < 3, page.HasMore);
        }

        Assert.Equal(new[] { "10", "20", "30" }, ids);
    }

    [Fact]
    public void PagedResult_LargePage_DoesNotOverflowHasMore()
    {
        var page = new PagedResult<string> { Page = int.MaxValue, PageSize = 500, TotalCount = 3 };
        Assert.False(page.HasMore);
    }

    private void SeedPlatformsWithEqualTimestamps()
    {
        var timestamp = new DateTime(2026, 9, 18, 0, 0, 0, DateTimeKind.Utc);
        foreach (var id in new[] { 30, 10, 20 })
        {
            _db.SteamRawItems.Add(new SteamRawItem
            {
                Id = id, AppId = id, Name = $"Game {id}", RawJson = "{}", LastUpdated = timestamp
            });
            _db.PsnRawItems.Add(new PsnRawItem
            {
                Id = id, TitleId = id.ToString(), TitleName = $"Game {id}", RawJson = "{}", LastUpdated = timestamp
            });
            _db.GogRawItems.Add(new GogRawItem
            {
                Id = id, GameId = id, Title = $"Game {id}", RawJson = "{}", LastUpdated = timestamp
            });
            _db.EpicRawItems.Add(new EpicRawItem
            {
                Id = id, AppName = id.ToString(), Title = $"Game {id}", RawJson = "{}", LastUpdated = timestamp
            });
        }
    }

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
