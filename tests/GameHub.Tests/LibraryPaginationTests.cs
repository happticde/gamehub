using GameHub.Core.Controllers;
using GameHub.Core.Data;
using GameHub.Core.Services;
using GameHub.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Xunit;

namespace GameHub.Tests;

/// <summary>Regression coverage for bounded library pagination.</summary>
public sealed class LibraryPaginationTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly CoreContext _db;

    public LibraryPaginationTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<CoreContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new CoreContext(options, new DefaultTenantContext());
        _db.Database.EnsureCreated();
        _db.UnifiedGames.AddRange(
            new UnifiedGame { Title = "Alpha", Slug = "alpha" },
            new UnifiedGame { Title = "Bravo", Slug = "bravo" },
            new UnifiedGame { Title = "Charlie", Slug = "charlie" });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task GetLibrary_PageBeyondFilteredResults_ClampsToLastPage()
    {
        var result = Assert.IsType<OkObjectResult>(await CreateController().GetLibrary(
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            page: 99,
            pageSize: 2));

        var page = Assert.IsType<PagedResult<object>>(result.Value);
        Assert.Equal(2, page.Page);
        Assert.Equal(2, page.PageSize);
        Assert.Single(page.Items);
    }

    [Fact]
    public async Task GetLibrary_InvalidPagination_UsesSafeDefaults()
    {
        var result = Assert.IsType<OkObjectResult>(await CreateController().GetLibrary(
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            page: -5,
            pageSize: 0));

        var page = Assert.IsType<PagedResult<object>>(result.Value);
        Assert.Equal(1, page.Page);
        Assert.Equal(50, page.PageSize);
        Assert.Equal(3, page.Items.Count);
    }

    [Theory]
    [InlineData("title", "asc")]
    [InlineData("title", "desc")]
    [InlineData("rating", "asc")]
    [InlineData("rating", "desc")]
    [InlineData("releasedate", "asc")]
    [InlineData("releasedate", "desc")]
    public async Task GetLibrary_EqualSortValues_ReturnsEachGameOnceInIdOrder(string sortBy, string sortOrder)
    {
        var games = await _db.UnifiedGames.OrderBy(game => game.Id).ToListAsync();
        foreach (var game in games)
        {
            game.Title = "Same title";
            game.Rating = 80;
            game.FirstReleaseDate = new DateTime(2026, 1, 1);
        }
        await _db.SaveChangesAsync();
        await _db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IX_Review_Title ON UnifiedGames (TenantId, Title, Id DESC)");
        var returnedIds = new List<int>();

        for (var pageNumber = 1; pageNumber <= games.Count; pageNumber++)
        {
            var result = Assert.IsType<OkObjectResult>(await CreateController().GetLibrary(
                null, null, null, null, null, null, null, sortBy, sortOrder,
                page: pageNumber, pageSize: 1));
            var page = Assert.IsType<PagedResult<object>>(result.Value);
            var item = JsonSerializer.SerializeToElement(Assert.Single(page.Items));
            returnedIds.Add(item.GetProperty("Id").GetInt32());
        }

        Assert.Equal(games.Select(game => game.Id), returnedIds);
    }

    private LibraryController CreateController() => new(
        null!,
        null!,
        null!,
        _db,
        NullLogger<LibraryController>.Instance,
        new SyncSchedulerStatus(),
        new DefaultTenantContext(),
        new ActivityLogService(_db),
        null!);
}
