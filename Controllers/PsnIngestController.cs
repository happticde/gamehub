using GameHub.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace GameHub.Controllers;

[ApiController]
[Route("api/ingest/psn")]
public class PsnIngestController : ControllerBase
{
    private readonly GameHubContext _db;

    public PsnIngestController(GameHubContext db)
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
}
