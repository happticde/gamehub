using GameHub.Ingest.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace GameHub.Controllers;

[ApiController]
[Route("api/ingest/epic")]
public class EpicIngestController : ControllerBase
{
    private readonly IngestContext _db;

    public EpicIngestController(IngestContext db)
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
            // Legendary JSON uses 'app_name' as the unique ID
            if (!gameJson.TryGetProperty("app_name", out var idProp)) continue;
            string appName = idProp.GetString()!;

            string? title = null;
            if (gameJson.TryGetProperty("app_title", out var tProp)) title = tProp.GetString();

            var existing = await _db.EpicRawItems.FirstOrDefaultAsync(x => x.AppName == appName);
            string jsonString = gameJson.ToString();

            if (existing != null)
            {
                existing.RawJson = jsonString;
                existing.Title = title ?? existing.Title;
                existing.LastUpdated = DateTime.UtcNow;
                updated++;
            }
            else
            {
                _db.EpicRawItems.Add(new EpicRawItem
                {
                    AppName = appName,
                    Title = title,
                    RawJson = jsonString,
                    LastUpdated = DateTime.UtcNow
                });
                added++;
            }
        }

        await _db.SaveChangesAsync();
        return Ok(new { Added = added, Updated = updated });
    }

    // GET: api/ingest/epic?page=1&pageSize=50
    [HttpGet]
    public async Task<IActionResult> ListItems([FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        // Safety cap to prevent fetching 1 million rows
        if (pageSize > 100) pageSize = 100;
        if (page < 1) page = 1;

        var query = _db.EpicRawItems.AsNoTracking();

        var totalCount = await query.CountAsync();

        var items = await query
            .OrderByDescending(x => x.LastUpdated)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new
            {
                x.Id,
                x.AppName,
                x.Title,
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

    // GET: api/ingest/epic/10
    [HttpGet("{gameId}")]
    public async Task<IActionResult> GetItem(string gameId)
    {
        var item = await _db.EpicRawItems
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.AppName == gameId);

        if (item == null)
            return NotFound(new { Message = $"Steam game with ID {gameId} not found" });

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
            item.AppName,
            item.Title,
            item.LastUpdated,
            RawData = parsedMetadata
        });
    }

}
