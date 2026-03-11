using GameHub.Ingest.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Linq.Expressions;
using System.Text.Json;

namespace GameHub.Ingest.Controllers;

/// <summary>
/// GOG-specific ingest controller.
/// Inherits all shared logic from IngestControllerBase.
///
/// GOG API JSON shape: { "id": 1495134320, "title": "The Witcher 3", ... }
/// </summary>
[ApiController]
[Route("api/ingest/gog")]
public class GogIngestController : IngestControllerBase<GogRawItem>
{
    public GogIngestController(IngestContext db, ILogger<GogIngestController> logger) : base(db, logger) { }

    protected override string PlatformName => "GOG";
    protected override DbSet<GogRawItem> DbSet => Db.GogRawItems;

    /// <summary>
    /// GOG uses "id" as the unique key and "title" for the name.
    /// The API returns id as either a number or a string depending on the endpoint,
    /// so we read it as a raw string to handle both.
    /// </summary>
    protected override (string? Id, string? Name) ExtractIdAndName(JsonElement json)
    {
        if (!json.TryGetProperty("id", out var idProp)) return (null, null);
        // Handle both numeric and string id values from GOG API
        string? id = idProp.ValueKind == System.Text.Json.JsonValueKind.Number
            ? idProp.GetInt64().ToString()
            : idProp.GetString();
        string? title = json.TryGetProperty("title", out var tProp) ? tProp.GetString() : null;
        return (id, title);
    }

    /// <summary>GOG provides playtime in minutes under `stats.playtime` (for games with stats).</summary>
    protected override int? ExtractPlaytime(JsonElement json)
    {
        if (json.TryGetProperty("stats", out var statsProp) && statsProp.ValueKind == JsonValueKind.Object)
        {
            if (statsProp.TryGetProperty("playtime", out var playtimeProp) && playtimeProp.ValueKind == JsonValueKind.Number)
            {
                return playtimeProp.GetInt32();
            }
        }
        return null;
    }

    protected override GogRawItem CreateEntity(string externalId, string? name, int? playtimeMinutes, string rawJson) => new()
    {
        GameId = long.Parse(externalId), // Convert string back to long for the DB column
        Title = name,
        PlaytimeMinutes = playtimeMinutes,
        RawJson = rawJson,
        LastUpdated = DateTime.UtcNow
    };

    protected override void UpdateEntity(GogRawItem existing, string? name, int? playtimeMinutes, string rawJson)
    {
        existing.RawJson = rawJson;
        existing.Title = name ?? existing.Title;
        existing.PlaytimeMinutes = playtimeMinutes ?? existing.PlaytimeMinutes;
        existing.LastUpdated = DateTime.UtcNow;
    }

    protected override Expression<Func<GogRawItem, bool>> FindByExternalId(string id)
        => x => x.GameId == long.Parse(id);
}
