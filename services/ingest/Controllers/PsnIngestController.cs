using GameHub.Ingest.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Linq.Expressions;
using System.Text.Json;

namespace GameHub.Ingest.Controllers;

/// <summary>
/// PSN-specific ingest controller.
/// Inherits all shared logic from IngestControllerBase.
///
/// PSN API JSON shape: { "titleId": "CUSA00001_00", "name": "Bloodborne", ... }
/// Some API endpoints use "npTitleId" instead, so we check both as a fallback.
/// </summary>
[ApiController]
[Route("api/ingest/psn")]
public class PsnIngestController : IngestControllerBase<PsnRawItem>
{
    public PsnIngestController(IngestContext db, ILogger<PsnIngestController> logger) : base(db, logger) { }

    protected override string PlatformName => "PSN";
    protected override DbSet<PsnRawItem> DbSet => Db.PsnRawItems;

    /// <summary>
    /// PSN uses "titleId" (string) as the unique key, with a fallback to "npTitleId"
    /// since different Sony API endpoints use different property names.
    /// </summary>
    protected override (string? Id, string? Name) ExtractIdAndName(JsonElement json)
    {
        string? titleId = json.TryGetProperty("titleId", out var idProp) ? idProp.GetString() : null;

        // Fallback: some PSN API endpoints return "npTitleId" instead of "titleId"
        if (string.IsNullOrEmpty(titleId) && json.TryGetProperty("npTitleId", out var npProp))
            titleId = npProp.GetString();

        string? name = json.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
        return (titleId, name);
    }

    protected override PsnRawItem CreateEntity(string externalId, string? name, string rawJson) => new()
    {
        TitleId = externalId,
        TitleName = name,
        RawJson = rawJson,
        LastUpdated = DateTime.UtcNow
    };

    protected override void UpdateEntity(PsnRawItem existing, string? name, string rawJson)
    {
        existing.RawJson = rawJson;
        existing.TitleName = name ?? existing.TitleName;
        existing.LastUpdated = DateTime.UtcNow;
    }

    protected override Expression<Func<PsnRawItem, bool>> FindByExternalId(string id)
        => x => x.TitleId == id;
}
