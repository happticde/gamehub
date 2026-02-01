using GameHub.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace GameHub.Controllers;

[ApiController]
[Route("api/ingest/steam")]
public class SteamIngestController : ControllerBase
{
    private readonly GameHubContext _db;

    public SteamIngestController(GameHubContext db)
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
            // Steam returns 'appid' (int)
            if (!gameJson.TryGetProperty("appid", out var appIdProp)) continue;
            int appId = appIdProp.GetInt32();

            string? name = null;
            if (gameJson.TryGetProperty("name", out var nameProp)) name = nameProp.GetString();

            var existing = await _db.SteamRawItems.FirstOrDefaultAsync(x => x.AppId == appId);
            string jsonString = gameJson.ToString();

            if (existing != null)
            {
                existing.RawJson = jsonString;
                existing.Name = name ?? existing.Name;
                existing.LastUpdated = DateTime.UtcNow;
                updated++;
            }
            else
            {
                _db.SteamRawItems.Add(new SteamRawItem
                {
                    AppId = appId,
                    Name = name,
                    RawJson = jsonString,
                    LastUpdated = DateTime.UtcNow
                });
                added++;
            }
        }

        await _db.SaveChangesAsync();
        return Ok(new { Added = added, Updated = updated });
    }

    // GET: api/ingest/steam?page=1&pageSize=50
    [HttpGet]
    public async Task<IActionResult> ListItems([FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        // Safety cap to prevent fetching 1 million rows
        if (pageSize > 100) pageSize = 100;
        if (page < 1) page = 1;

        var query = _db.SteamRawItems.AsNoTracking();

        var totalCount = await query.CountAsync();

        var items = await query
            .OrderByDescending(x => x.LastUpdated)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new
            {
                x.Id,
                x.AppId,
                x.Name,
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

    // GET: api/ingest/steam/CUSA00001_00
    [HttpGet("{appId}")]
    public async Task<IActionResult> GetItem(int appId)
    {
        var item = await _db.SteamRawItems
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.AppId == appId);

        if (item == null)
            return NotFound(new { Message = $"Steam game with ID {appId} not found" });

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
            item.AppId,
            item.Name,
            item.LastUpdated,
            RawData = parsedMetadata
        });
    }
}
