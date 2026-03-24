using GameHub.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace GameHub.Core.Services;

/// <summary>
/// Centralized helper for recording system events into the ActivityLog table.
/// Registered as scoped because it depends on <see cref="CoreContext"/>.
/// </summary>
public class ActivityLogService
{
    private readonly CoreContext _db;

    public ActivityLogService(CoreContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Writes a single activity event to the log.
    /// </summary>
    /// <param name="eventType">Category such as "SyncStarted", "SyncFinished", "Merge", "Split".</param>
    /// <param name="message">Human-readable description of what happened.</param>
    /// <param name="details">Optional extra context (JSON, error text, etc.).</param>
    public async Task LogAsync(string eventType, string message, string? details = null)
    {
        _db.ActivityLogs.Add(new ActivityLog
        {
            EventType = eventType,
            Message = message,
            Details = details
        });
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Returns the most recent activity log entries, newest first.
    /// </summary>
    /// <param name="count">Maximum number of entries to return (default 20).</param>
    public async Task<List<ActivityLog>> GetRecentAsync(int count = 20)
    {
        return await _db.ActivityLogs
            .AsNoTracking()
            .OrderByDescending(a => a.Timestamp)
            .Take(count)
            .ToListAsync();
    }
}
