using GameHub.Ingest.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;
using System.Text.Json;

namespace GameHub.Ingest.Controllers;

/// <summary>
/// Epic Games-specific ingest controller.
/// Inherits all shared logic from IngestControllerBase.
///
/// Epic/Legendary JSON shape: { "app_name": "Fortnite", "app_title": "Fortnite", ... }
/// The CLI uses the "Legendary" tool to fetch the game list from Epic's API.
/// </summary>
[ApiController]
[Route("api/ingest/epic")]
public class EpicIngestController : IngestControllerBase<EpicRawItem>
{
    public EpicIngestController(IngestContext db) : base(db) { }

    protected override string PlatformName => "Epic";
    protected override DbSet<EpicRawItem> DbSet => Db.EpicRawItems;

    /// <summary>
    /// Epic/Legendary uses "app_name" (string) as the unique identifier
    /// and "app_title" for the human-readable game title.
    /// </summary>
    protected override (string? Id, string? Name) ExtractIdAndName(JsonElement json)
    {
        if (!json.TryGetProperty("app_name", out var idProp)) return (null, null);
        string? id = idProp.GetString();
        string? title = json.TryGetProperty("app_title", out var tProp) ? tProp.GetString() : null;
        return (id, title);
    }

    protected override EpicRawItem CreateEntity(string externalId, string? name, string rawJson) => new()
    {
        AppName = externalId, // Already a string — no conversion needed
        Title = name,
        RawJson = rawJson,
        LastUpdated = DateTime.UtcNow
    };

    protected override void UpdateEntity(EpicRawItem existing, string? name, string rawJson)
    {
        existing.RawJson = rawJson;
        existing.Title = name ?? existing.Title;
        existing.LastUpdated = DateTime.UtcNow;
    }

    protected override Expression<Func<EpicRawItem, bool>> FindByExternalId(string id)
        => x => x.AppName == id;
}
