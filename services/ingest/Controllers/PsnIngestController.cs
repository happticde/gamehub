using GameHub.Ingest.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace GameHub.Controllers;

[ApiController]
[Route("api/ingest/psn")]
public class PsnIngestController : ControllerBase
{
    private readonly IngestContext _db;

    public PsnIngestController(IngestContext db)
    {
        _db = db;
    }

    [HttpPost]
    public async Task<IActionResult> IngestBatch([FromBody] List<JsonElement> rawGames)
    {
        int added = 0;
        int updated = 0;

        foreach (var gameJson in rawGames)
        {
            // 1. Extract the unique ID safely
            // PSN usually returns 'titleId' in the library stats
            string? titleId = null;
            if (gameJson.TryGetProperty("titleId", out var idProp))
                titleId = idProp.GetString();

            // Fallback for different API endpoints if needed
            if (string.IsNullOrEmpty(titleId) && gameJson.TryGetProperty("npTitleId", out var npProp))
                titleId = npProp.GetString();

            if (string.IsNullOrEmpty(titleId)) continue; // Skip bad data

            string? name = null;
            if (gameJson.TryGetProperty("name", out var nameProp)) name = nameProp.GetString();

            // 2. IDEMPOTENCY CHECK
            var existing = await _db.PsnRawItems.FirstOrDefaultAsync(x => x.TitleId == titleId);

            string jsonString = gameJson.ToString();

            if (existing != null)
            {
                // Update existing record
                existing.RawJson = jsonString;
                existing.TitleName = name ?? existing.TitleName;
                existing.LastUpdated = DateTime.UtcNow;
                updated++;
            }
            else
            {
                // Create new record
                _db.PsnRawItems.Add(new PsnRawItem
                {
                    TitleId = titleId,
                    TitleName = name,
                    RawJson = jsonString,
                    LastUpdated = DateTime.UtcNow
                });
                added++;
            }
        }

        await _db.SaveChangesAsync();
        return Ok(new { Added = added, Updated = updated, TotalProcessed = rawGames.Count });
    }

    // GET: api/ingest/psn?page=1&pageSize=50
    [HttpGet]
    public async Task<IActionResult> ListItems([FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        // Safety cap to prevent fetching 1 million rows
        if (pageSize > 100) pageSize = 100;
        if (page < 1) page = 1;

        var query = _db.PsnRawItems.AsNoTracking();

        var totalCount = await query.CountAsync();

        var items = await query
            .OrderByDescending(x => x.LastUpdated)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new
            {
                x.Id,
                x.TitleId,
                x.TitleName,
                // Do NOT return the massive RawJson blob in the list view
                x.LastUpdated
            })
            .ToListAsync();

        return Ok(new
        {
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            Items = items
        });
    }

    // GET: api/ingest/psn/CUSA00001_00
    [HttpGet("{titleId}")]
    public async Task<IActionResult> GetItem(string titleId)
    {
        var item = await _db.PsnRawItems
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TitleId == titleId);

        if (item == null)
            return NotFound(new { Message = $"Game with ID {titleId} not found" });

        // For the detail view, we return the parsed JSON object so the frontend can use it easily
        // We deserialize the string back to an object so the API response is clean JSON, not a stringified string.
        object? parsedMetadata = null;
        try
        {
            if (!string.IsNullOrEmpty(item.RawJson))
                parsedMetadata = JsonSerializer.Deserialize<object>(item.RawJson);
        }
        catch
        {
            parsedMetadata = new { Error = "Invalid JSON stored" };
        }

        return Ok(new
        {
            item.Id,
            item.TitleId,
            item.TitleName,
            item.LastUpdated,
            RawData = parsedMetadata
        });
    }
}
