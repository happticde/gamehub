using GameHub.Ingest.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Linq.Expressions;
using System.Text.Json;

namespace GameHub.Ingest.Controllers;

/// <summary>
/// Steam-specific ingest controller.
/// Inherits all shared logic (batch upsert, pagination, detail view) from IngestControllerBase.
/// Only provides the Steam-specific JSON parsing and entity creation.
///
/// Steam API JSON shape: { "appid": 730, "name": "Counter-Strike 2", ... }
/// </summary>
[ApiController]
[Route("api/ingest/steam")]
public class SteamIngestController : IngestControllerBase<SteamRawItem>
{
    public SteamIngestController(IngestContext db, ILogger<SteamIngestController> logger) : base(db, logger) { }

    protected override string PlatformName => "Steam";
    protected override DbSet<SteamRawItem> DbSet => Db.SteamRawItems;

    /// <summary>Steam uses "appid" (int) as the unique key and "name" for the title.</summary>
    protected override (string? Id, string? Name) ExtractIdAndName(JsonElement json)
    {
        if (!json.TryGetProperty("appid", out var idProp)) return (null, null);
        string id = idProp.GetInt32().ToString();
        string? name = json.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
        return (id, name);
    }

    /// <summary>Steam provides 'playtime_forever' in minutes.</summary>
    protected override int? ExtractPlaytime(JsonElement json)
    {
        if (json.TryGetProperty("playtime_forever", out var playtimeProp) && playtimeProp.ValueKind == JsonValueKind.Number)
        {
            return playtimeProp.GetInt32();
        }
        return null;
    }

    protected override SteamRawItem CreateEntity(string externalId, string? name, int? playtimeMinutes) => new()
    {
        AppId = int.Parse(externalId), // Convert string back to int for the DB column
        Name = name,
        PlaytimeMinutes = playtimeMinutes
    };

    protected override void UpdateEntity(SteamRawItem existing, string? name, int? playtimeMinutes)
    {
        existing.Name = name ?? existing.Name; // Keep old name if new one is null
        existing.PlaytimeMinutes = playtimeMinutes ?? existing.PlaytimeMinutes;
    }

    protected override Task<List<SteamRawItem>> LoadExistingItemsAsync(IReadOnlyCollection<string> externalIds)
    {
        var appIds = externalIds.Select(int.Parse).ToArray();
        return DbSet.Where(item => appIds.Contains(item.AppId)).ToListAsync();
    }

    protected override Expression<Func<SteamRawItem, bool>> FindByExternalId(string id)
        => x => x.AppId == int.Parse(id);
}
