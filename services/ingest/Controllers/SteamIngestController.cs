using GameHub.Ingest.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
    public SteamIngestController(IngestContext db) : base(db) { }

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

    protected override SteamRawItem CreateEntity(string externalId, string? name, string rawJson) => new()
    {
        AppId = int.Parse(externalId), // Convert string back to int for the DB column
        Name = name,
        RawJson = rawJson,
        LastUpdated = DateTime.UtcNow
    };

    protected override void UpdateEntity(SteamRawItem existing, string? name, string rawJson)
    {
        existing.RawJson = rawJson;
        existing.Name = name ?? existing.Name; // Keep old name if new one is null
        existing.LastUpdated = DateTime.UtcNow;
    }

    protected override Expression<Func<SteamRawItem, bool>> FindByExternalId(string id)
        => x => x.AppId == int.Parse(id);
}
