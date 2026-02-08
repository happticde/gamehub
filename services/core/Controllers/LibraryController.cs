using GameHub.Core.Data;
using GameHub.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GameHub.Core.Controllers;

[ApiController]
[Route("api/library")]
public class LibraryController : ControllerBase
{
    private readonly UnificationService _unifier;
    private readonly CoreContext _db;

    public LibraryController(UnificationService unifier, CoreContext db)
    {
        _unifier = unifier;
        _db = db;
    }

    // POST: api/library/sync
    // Triggers the merge process from Ingest -> Core
    [HttpPost("sync")]
    public async Task<IActionResult> Sync()
    {
        // Run the unification logic defined in UnificationService
        var count = await _unifier.RunFullSyncAsync();
        return Ok(new { Processed = count, Message = "Sync completed successfully." });
    }

    // GET: api/library
    // The main endpoint for your future Frontend to display games
    [HttpGet]
    public async Task<IActionResult> GetLibrary([FromQuery] string? search)
    {
        // 1. Start query
        var query = _db.UnifiedGames
            .Include(g => g.Items) // Eager load the platforms (Steam, PSN, etc.)
            .AsNoTracking();       // Faster for read-only

        // 2. Filter if search param exists
        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(g => g.Title.ToLower().Contains(search.ToLower()));
        }

        // 3. Execute and map to simple JSON
        var games = await query
            .OrderBy(g => g.Title)
            .ToListAsync();

        var result = games.Select(g => new
        {
            g.Id,
            g.Title,
            g.Slug,
            // Return a simple list of strings like ["Steam", "Gog"]
            Platforms = g.Items.Select(i => i.Platform.ToString()).ToList()
        });

        return Ok(result);
    }
}
