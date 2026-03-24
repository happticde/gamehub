using GameHub.Core.Data;
using GameHub.Core.Services;
using GameHub.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GameHub.Tests;

/// <summary>
/// Unit tests for <see cref="ActivityLogService"/>.
/// Uses in-memory SQLite (same fixture as the other test classes).
/// </summary>
public class ActivityLogServiceTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly CoreContext _db;
    private readonly ActivityLogService _service;

    public ActivityLogServiceTests()
    {
        var (db, connection) = DatabaseFixture.CreateCoreContext();
        _db = db;
        _connection = connection;
        _service = new ActivityLogService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task LogAsync_CreatesEntry()
    {
        // Act
        await _service.LogAsync("SyncStarted", "Manual sync triggered", "extra-details");

        // Assert
        var entry = await _db.ActivityLogs.SingleAsync();
        Assert.Equal("SyncStarted", entry.EventType);
        Assert.Equal("Manual sync triggered", entry.Message);
        Assert.Equal("extra-details", entry.Details);
        Assert.True(entry.Timestamp <= DateTime.UtcNow);
    }

    [Fact]
    public async Task GetRecentAsync_ReturnsLatestEntriesInOrder()
    {
        // Arrange — insert 5 entries with distinct timestamps
        for (int i = 1; i <= 5; i++)
        {
            _db.ActivityLogs.Add(new ActivityLog
            {
                EventType = "Test",
                Message = $"Entry {i}",
                Timestamp = DateTime.UtcNow.AddMinutes(-i) // entry 1 = most recent
            });
        }
        await _db.SaveChangesAsync();

        // Act — request only 3
        var results = await _service.GetRecentAsync(3);

        // Assert — newest first, limited to 3
        Assert.Equal(3, results.Count);
        Assert.Equal("Entry 1", results[0].Message);
        Assert.Equal("Entry 2", results[1].Message);
        Assert.Equal("Entry 3", results[2].Message);
    }

    [Fact]
    public async Task GetRecentAsync_EmptyDatabase_ReturnsEmptyList()
    {
        // Act
        var results = await _service.GetRecentAsync();

        // Assert
        Assert.Empty(results);
    }
}
