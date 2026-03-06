using GameHub.Core.Data;
using GameHub.Core.Services;
using GameHub.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GameHub.Core.Controllers;

/// <summary>
/// The main API for the unified game library.
/// Provides endpoints for:
///   - Syncing data from the Ingest service
///   - Querying the unified library
///   - Manually merging/splitting games across platforms
/// </summary>
[ApiController]
[Route("api/library")]
[Authorize]
public class LibraryController : ControllerBase
{
    private readonly UnificationService _unifier;
    private readonly CoreContext _db;
    private readonly ILogger<LibraryController> _logger;

    public LibraryController(UnificationService unifier, CoreContext db, ILogger<LibraryController> logger)
    {
        _unifier = unifier;
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// POST: api/library/sync
    /// Triggers an incremental sync from Ingest → Core.
    /// Fetches new/updated records from all platforms and merges them into the unified library.
    /// </summary>
    [HttpPost("sync")]
    public async Task<IActionResult> Sync()
    {
        _logger.LogInformation("Starting full library sync");
        var count = await _unifier.RunFullSyncAsync();
        _logger.LogInformation("Library sync completed: {Count} items processed", count);
        return Ok(new { Processed = count, Message = "Sync completed successfully." });
    }

    /// <summary>
    /// GET: api/library?search=witcher
    /// Returns all unified games with their platform tags.
    /// Optionally filter by title search.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetLibrary([FromQuery] string? search)
    {
        var query = _db.UnifiedGames
            .Include(g => g.Items)
            .AsNoTracking();

        // Filter by search term if provided
        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(g => g.Title.ToLower().Contains(search.ToLower()));
        }

        var games = await query
            .OrderBy(g => g.Title)
            .ToListAsync();

        var result = games.Select(g => new
        {
            g.Id,
            g.Title,
            g.Slug,
            Platforms = g.Items.Select(i => new
            {
                i.Platform,
                i.ExternalId,
                i.LastSynced
            }).ToList()
        });

        return Ok(result);
    }

    // =========================================================================
    // Manual Merge / Split Endpoints
    // =========================================================================

    /// <summary>
    /// POST: api/library/merge
    /// Body: { "sourceGameId": 42, "targetGameId": 7 }
    ///
    /// Merges two UnifiedGame records into one:
    ///   1. Moves all LibraryItems from the source game to the target game
    ///   2. Creates ManualLinks for each moved item (so future syncs respect the override)
    ///   3. Deletes the now-empty source game
    ///
    /// Use this when the same game appears as two separate entries due to
    /// different titles across storefronts (e.g. "GOTY Edition" vs base game).
    /// </summary>
    [HttpPost("merge")]
    public async Task<IActionResult> MergeGames([FromBody] MergeRequest request)
    {
        // Load both games with their LibraryItems
        var source = await _db.UnifiedGames
            .Include(g => g.Items)
            .FirstOrDefaultAsync(g => g.Id == request.SourceGameId);

        var target = await _db.UnifiedGames
            .Include(g => g.Items)
            .FirstOrDefaultAsync(g => g.Id == request.TargetGameId);

        if (source == null)
        {
            _logger.LogWarning("Merge failed: source game {Id} not found", request.SourceGameId);
            return NotFound(new { Message = $"Source game with ID {request.SourceGameId} not found" });
        }
        if (target == null)
        {
            _logger.LogWarning("Merge failed: target game {Id} not found", request.TargetGameId);
            return NotFound(new { Message = $"Target game with ID {request.TargetGameId} not found" });
        }
        if (source.Id == target.Id)
            return BadRequest(new { Message = "Cannot merge a game with itself" });

        _logger.LogInformation("Merging '{Source}' (ID: {SrcId}) into '{Target}' (ID: {TgtId})",
            source.Title, source.Id, target.Title, target.Id);

        // Move each LibraryItem from source to target
        foreach (var item in source.Items.ToList())
        {
            // Check if the target already has an entry for this platform
            var existingLink = target.Items.FirstOrDefault(i => i.Platform == item.Platform);
            if (existingLink != null)
            {
                // Target already has this platform — keep the target's entry, remove the source's
                _db.LibraryItems.Remove(item);
            }
            else
            {
                // Move the item to the target game
                item.UnifiedGameId = target.Id;
            }

            // Create a ManualLink so future syncs respect this decision
            var manualLink = await _db.ManualLinks
                .FirstOrDefaultAsync(ml => ml.Platform == item.Platform && ml.ExternalId == item.ExternalId);

            if (manualLink == null)
            {
                _db.ManualLinks.Add(new ManualLink
                {
                    Platform = item.Platform,
                    ExternalId = item.ExternalId,
                    UnifiedGameId = target.Id
                });
            }
            else
            {
                manualLink.UnifiedGameId = target.Id;
            }
        }

        // Delete the now-empty source game
        _db.UnifiedGames.Remove(source);

        await _db.SaveChangesAsync();

        _logger.LogInformation("Merge completed: '{Source}' → '{Target}'", source.Title, target.Title);

        return Ok(new
        {
            Message = $"Merged '{source.Title}' into '{target.Title}'.",
            TargetGameId = target.Id
        });
    }

    /// <summary>
    /// POST: api/library/split
    /// Body: { "libraryItemId": 15 }
    ///
    /// Detaches a single LibraryItem from its current UnifiedGame and creates
    /// a new UnifiedGame for it. Also creates a ManualLink to prevent future
    /// syncs from merging it back.
    ///
    /// Use this when two different games have been incorrectly merged because
    /// they have similar/identical titles (e.g. "DOOM" 1993 vs "DOOM" 2016).
    /// </summary>
    [HttpPost("split")]
    public async Task<IActionResult> SplitGame([FromBody] SplitRequest request)
    {
        // Load the LibraryItem and its parent game
        var item = await _db.LibraryItems
            .Include(i => i.UnifiedGame)
            .FirstOrDefaultAsync(i => i.Id == request.LibraryItemId);

        if (item == null)
        {
            _logger.LogWarning("Split failed: library item {Id} not found", request.LibraryItemId);
            return NotFound(new { Message = $"Library item with ID {request.LibraryItemId} not found" });
        }

        if (item.UnifiedGame == null)
            return BadRequest(new { Message = "Library item is not linked to any game" });

        _logger.LogInformation("Splitting {Platform} item '{ExternalId}' from game '{Game}'",
            item.Platform, item.ExternalId, item.UnifiedGame.Title);

        // Create a new UnifiedGame for the detached item
        // Use the original title but append the platform name to differentiate
        var newTitle = $"{item.UnifiedGame.Title} ({item.Platform})";
        var newSlug = UnificationService.GenerateSlug(newTitle);

        var newGame = new UnifiedGame
        {
            Title = newTitle,
            Slug = newSlug
        };

        _db.UnifiedGames.Add(newGame);
        await _db.SaveChangesAsync(); // Save to get the new game's ID

        // Move the item to the new game
        item.UnifiedGameId = newGame.Id;

        // Create a ManualLink so future syncs don't merge it back
        var manualLink = await _db.ManualLinks
            .FirstOrDefaultAsync(ml => ml.Platform == item.Platform && ml.ExternalId == item.ExternalId);

        if (manualLink == null)
        {
            _db.ManualLinks.Add(new ManualLink
            {
                Platform = item.Platform,
                ExternalId = item.ExternalId,
                UnifiedGameId = newGame.Id
            });
        }
        else
        {
            manualLink.UnifiedGameId = newGame.Id;
        }

        await _db.SaveChangesAsync();

        _logger.LogInformation("Split completed: new game '{Title}' (ID: {Id})", newTitle, newGame.Id);

        return Ok(new
        {
            Message = $"Split '{item.Platform}' entry into new game '{newTitle}'.",
            NewGameId = newGame.Id,
            OriginalGameId = item.UnifiedGame.Id
        });
    }
}

// =========================================================================
// Request DTOs
// =========================================================================

/// <summary>Request body for the merge endpoint.</summary>
public class MergeRequest
{
    /// <summary>ID of the game to merge FROM (will be deleted).</summary>
    public int SourceGameId { get; set; }

    /// <summary>ID of the game to merge INTO (will keep all items).</summary>
    public int TargetGameId { get; set; }
}

/// <summary>Request body for the split endpoint.</summary>
public class SplitRequest
{
    /// <summary>ID of the LibraryItem to detach into its own UnifiedGame.</summary>
    public int LibraryItemId { get; set; }
}
