using GameHub.Shared;
using GameHub.Ingest.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GameHub.Ingest.Controllers;

/// <summary>
/// Exposes paginated, filterable endpoints for the Core service to pull
/// raw game data during sync. Supports incremental sync via the "updatedAfter"
/// parameter — only records modified after that timestamp are returned.
///
/// This replaces the old per-platform endpoints that loaded entire tables at once.
/// Now a single generic endpoint handles all platforms with pagination.
/// </summary>
[ApiController]
[Route("api/export")]
public class ExportController : ControllerBase
{
    private readonly IngestContext _db;

    public ExportController(IngestContext db) { _db = db; }

    /// <summary>
    /// GET: api/export/{platform}?page=1&amp;pageSize=200&amp;updatedAfter=2024-01-01T00:00:00Z
    ///
    /// Returns a paginated list of RawGameDto for the given platform.
    ///
    /// Parameters:
    ///   - platform: "steam", "psn", "gog", or "epic" (case-insensitive)
    ///   - page: Page number (1-indexed, default 1)
    ///   - pageSize: Items per page (default 200, max 500)
    ///   - updatedAfter: Optional timestamp — if provided, only returns records
    ///                   updated after this time (for incremental sync)
    /// </summary>
    [HttpGet("{platform}")]
    public async Task<IActionResult> GetByPlatform(
        string platform,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 200,
        [FromQuery] DateTime? updatedAfter = null)
    {
        // Enforce safe bounds
        if (pageSize > 500) pageSize = 500;
        if (page < 1) page = 1;

        // Route to the correct DbSet based on platform name and project to RawGameDto
        IQueryable<RawGameDto>? query = platform.ToLowerInvariant() switch
        {
            "steam" => BuildQuery(_db.SteamRawItems, updatedAfter,
                x => new RawGameDto { Id = x.AppId.ToString(), Name = x.Name, LastUpdated = x.LastUpdated }),
            "psn" => BuildQuery(_db.PsnRawItems, updatedAfter,
                x => new RawGameDto { Id = x.TitleId, Name = x.TitleName, LastUpdated = x.LastUpdated }),
            "gog" => BuildQuery(_db.GogRawItems, updatedAfter,
                x => new RawGameDto { Id = x.GameId.ToString(), Name = x.Title, LastUpdated = x.LastUpdated }),
            "epic" => BuildQuery(_db.EpicRawItems, updatedAfter,
                x => new RawGameDto { Id = x.AppName, Name = x.Title, LastUpdated = x.LastUpdated }),
            _ => null
        };

        if (query == null)
            return BadRequest(new { Message = $"Unknown platform: {platform}. Use steam, psn, gog, or epic." });

        // Get total count for pagination metadata
        var totalCount = await query.CountAsync();

        // Fetch the current page
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return Ok(new PagedResult<RawGameDto>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        });
    }

    /// <summary>
    /// Builds a queryable for a specific entity type, applying:
    ///   1. Optional "updatedAfter" filter for incremental sync
    ///   2. Consistent ordering by LastUpdated (oldest first, for stable pagination)
    ///   3. Projection to RawGameDto
    /// </summary>
    private static IQueryable<RawGameDto> BuildQuery<TEntity>(
        DbSet<TEntity> dbSet,
        DateTime? updatedAfter,
        System.Linq.Expressions.Expression<Func<TEntity, RawGameDto>> selector)
        where TEntity : class, IRawItem
    {
        IQueryable<TEntity> query = dbSet.AsNoTracking();

        // If updatedAfter is provided, only return records modified after that timestamp
        if (updatedAfter.HasValue)
            query = query.Where(x => x.LastUpdated > updatedAfter.Value);

        // Order by LastUpdated ascending for stable, deterministic pagination
        return query.OrderBy(x => x.LastUpdated).Select(selector);
    }
}
