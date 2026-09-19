using GameHub.Shared;
using GameHub.Ingest.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;

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
[Authorize]
public class ExportController : ControllerBase
{
    private readonly IngestContext _db;
    private readonly ILogger<ExportController> _logger;

    public ExportController(IngestContext db, ILogger<ExportController> logger)
    {
        _db = db;
        _logger = logger;
    }

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
        _logger.LogDebug("Export: {Platform} page {Page}, pageSize {PageSize}, updatedAfter {After}",
            platform, page, pageSize, updatedAfter?.ToString("O") ?? "none");

        // Enforce safe bounds
        if (pageSize < 1) pageSize = 200;
        if (pageSize > 500) pageSize = 500;
        if (page < 1) page = 1;

        var normalizedPlatform = platform.ToLowerInvariant();
        if (normalizedPlatform == "epic")
        {
            return await GetEpicPageAsync(page, pageSize, updatedAfter);
        }

        // Route to the correct DbSet based on platform name and project to RawGameDto
        IQueryable<RawGameDto>? query = normalizedPlatform switch
        {
            "steam" => BuildQuery(_db.SteamRawItems, updatedAfter,
                x => new RawGameDto { Id = x.AppId.ToString(), Name = x.Name, LastUpdated = x.LastUpdated, ContentHash = x.ContentHash, PlaytimeMinutes = x.PlaytimeMinutes }),
            "psn" => BuildQuery(_db.PsnRawItems, updatedAfter,
                x => new RawGameDto { Id = x.TitleId, Name = x.TitleName, LastUpdated = x.LastUpdated, ContentHash = x.ContentHash, PlaytimeMinutes = x.PlaytimeMinutes }),
            "gog" => BuildQuery(_db.GogRawItems, updatedAfter,
                x => new RawGameDto { Id = x.GameId.ToString(), Name = x.Title, LastUpdated = x.LastUpdated, ContentHash = x.ContentHash, PlaytimeMinutes = x.PlaytimeMinutes }),
            _ => null
        };

        if (query == null)
        {
            _logger.LogWarning("Export: unknown platform '{Platform}' requested", platform);
            return BadRequest(new { Message = $"Unknown platform: {platform}. Use steam, psn, gog, or epic." });
        }

        // Get total count for pagination metadata
        var totalCount = await query.CountAsync();

        // Fetch the current page
        var items = await query
            // Preserve empty out-of-range pages without overflowing back to the first page.
            .Skip((int)Math.Min(((long)page - 1) * pageSize, int.MaxValue))
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

        // Batch imports can share timestamps; the unique ID makes page boundaries deterministic.
        return query.OrderBy(x => x.LastUpdated).ThenBy(x => x.Id).Select(selector);
    }

    private async Task<IActionResult> GetEpicPageAsync(int page, int pageSize, DateTime? updatedAfter)
    {
        var query = _db.EpicRawItems.AsNoTracking();

        if (updatedAfter.HasValue)
            query = query.Where(x => x.LastUpdated > updatedAfter.Value);

        var totalCount = await query.CountAsync();
        var rows = await query
            .OrderBy(x => x.LastUpdated)
            .ThenBy(x => x.Id)
            .Skip((int)Math.Min(((long)page - 1) * pageSize, int.MaxValue))
            .Take(pageSize)
            .Select(x => new
            {
                x.AppName,
                x.Title,
                x.LastUpdated,
                x.ContentHash,
                x.PlaytimeMinutes,
                x.RawJson
            })
            .ToListAsync();

        return Ok(new PagedResult<RawGameDto>
        {
            Items = rows.Select(x => BuildEpicRawGameDto(
                x.AppName,
                x.Title,
                x.LastUpdated,
                x.ContentHash,
                x.PlaytimeMinutes,
                x.RawJson)).ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        });
    }

    private static RawGameDto BuildEpicRawGameDto(
        string appName,
        string? title,
        DateTime lastUpdated,
        string contentHash,
        int? playtimeMinutes,
        string rawJson)
    {
        var item = new RawGameDto
        {
            Id = appName,
            Name = title,
            LastUpdated = lastUpdated,
            ContentHash = contentHash,
            PlaytimeMinutes = playtimeMinutes
        };

        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return item;
        }

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            var root = document.RootElement;
            item.Summary = ReadString(root, "summary") ?? ReadString(root, "description");
            item.CoverUrl = ReadString(root, "cover_url");
            item.ArtworkUrl = ReadString(root, "key_art_url");
            item.Developers = ReadString(root, "developer");
            item.Publishers = ReadString(root, "publisher");
            item.FirstReleaseDate = ReadDate(root, "release_date");
        }
        catch (JsonException)
        {
            return item;
        }

        return item;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static DateTime? ReadDate(JsonElement element, string propertyName)
    {
        var value = ReadString(element, propertyName);
        if (value is null || !DateTimeOffset.TryParse(value, out var parsed))
        {
            return null;
        }

        return parsed.UtcDateTime;
    }
}
