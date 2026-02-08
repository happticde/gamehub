using GameHub.Shared;
using GameHub.Ingest.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GameHub.Ingest.Controllers;

[ApiController]
[Route("api/export")]
public class ExportController : ControllerBase
{
    private readonly IngestContext _db;

    public ExportController(IngestContext db) { _db = db; }

    [HttpGet("steam")]
    public async Task<ActionResult<IEnumerable<RawGameDto>>> GetSteam()
    {
        return Ok(await _db.SteamRawItems
        .Select(x => new RawGameDto { Id = x.AppId.ToString(), Name = x.Name })
        .ToListAsync());
    }

    [HttpGet("psn")]
    public async Task<ActionResult<IEnumerable<RawGameDto>>> GetPsn()
    {
        return Ok(await _db.PsnRawItems
        .Select(x => new RawGameDto { Id = x.TitleId, Name = x.TitleName })
        .ToListAsync());
    }

    [HttpGet("gog")]
    public async Task<ActionResult<IEnumerable<RawGameDto>>> GetGog()
    {
        return Ok(await _db.GogRawItems
        .Select(x => new RawGameDto { Id = x.GameId.ToString(), Name = x.Title })
        .ToListAsync());
    }

    [HttpGet("epic")]
    public async Task<ActionResult<IEnumerable<RawGameDto>>> GetEpic()
    {
        return Ok(await _db.EpicRawItems
        .Select(x => new RawGameDto { Id = x.AppName, Name = x.Title })
        .ToListAsync());
    }
}
