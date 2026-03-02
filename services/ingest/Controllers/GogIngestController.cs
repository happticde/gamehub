using GameHub.Ingest.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
    public GogIngestController(IngestContext db) : base(db) { }

    protected override string PlatformName => "GOG";
    protected override DbSet<GogRawItem> DbSet => Db.GogRawItems;

    /// <summary>GOG uses "id" (long int) as the unique key and "title" for the name.</summary>
    protected override (string? Id, string? Name) ExtractIdAndName(JsonElement json)
    {
        if (!json.TryGetProperty("id", out var idProp)) return (null, null);
        string id = idProp.GetInt64().ToString();
        string? title = json.TryGetProperty("title", out var tProp) ? tProp.GetString() : null;
        return (id, title);
    }

    protected override GogRawItem CreateEntity(string externalId, string? name, string rawJson) => new()
    {
        GameId = long.Parse(externalId), // Convert string back to long for the DB column
        Title = name,
        RawJson = rawJson,
        LastUpdated = DateTime.UtcNow
    };

    protected override void UpdateEntity(GogRawItem existing, string? name, string rawJson)
    {
        existing.RawJson = rawJson;
        existing.Title = name ?? existing.Title;
        existing.LastUpdated = DateTime.UtcNow;
    }

    protected override Expression<Func<GogRawItem, bool>> FindByExternalId(string id)
        => x => x.GameId == long.Parse(id);
}
