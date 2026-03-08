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
    private readonly EnrichmentService _enrichment;
    private readonly CoreContext _db;
    private readonly ILogger<LibraryController> _logger;

    public LibraryController(
        UnificationService unifier,
        EnrichmentService enrichment,
        CoreContext db,
        ILogger<LibraryController> logger)
    {
        _unifier = unifier;
        _enrichment = enrichment;
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
    /// GET: api/library?search=witcher&tag=RPG&page=1&pageSize=50
    /// Returns a paginated list of unified games with their platform tags.
    /// Optionally filter by title search or specific tag.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetLibrary(
        [FromQuery] string? search,
        [FromQuery] string? tag,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        // Enforce safe bounds
        if (pageSize > 500) pageSize = 500;
        if (page < 1) page = 1;

        var query = _db.UnifiedGames
            .Include(g => g.Items)
            .Include(g => g.Tags)
            .AsNoTracking();

        // Filter by search term if provided
        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(g => g.Title.ToLower().Contains(search.ToLower()));
        }

        // Filter by tag if provided
        if (!string.IsNullOrWhiteSpace(tag))
        {
            query = query.Where(g => g.Tags.Any(t => t.Name.ToLower() == tag.ToLower()));
        }

        // Get total count for pagination metadata
        var totalCount = await query.CountAsync();

        // Fetch the current page
        var games = await query
            .OrderBy(g => g.Title)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var result = games.Select(g => new
        {
            g.Id,
            g.Title,
            g.Slug,
            g.CoverUrl,
            g.Summary,
            g.Genres,
            Tags = g.Tags.Select(t => t.Name).ToList(),
            g.FirstReleaseDate,
            g.Rating,
            g.MetadataSource,
            g.EnrichedAt,
            Platforms = g.Items.Select(i => new
            {
                i.Platform,
                i.ExternalId,
                i.LastSynced
            }).ToList()
        });

        // Use the untyped or object-typed PagedResult since we mapped to an anonymous type
        return Ok(new PagedResult<object>
        {
            Items = result.Cast<object>().ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        });
    }

    // =========================================================================
    // Enrichment Endpoints
    // =========================================================================

    /// <summary>
    /// POST: api/library/enrich
    /// Triggers enrichment for all un-enriched games.
    /// Returns the number of games processed.
    /// </summary>
    [HttpPost("enrich")]
    public async Task<IActionResult> EnrichAll()
    {
        _logger.LogInformation("Starting bulk enrichment of un-enriched games");

        var unenrichedCount = await _enrichment.GetUnenrichedCountAsync();
        if (unenrichedCount == 0)
            return Ok(new { Processed = 0, Message = "All games are already enriched." });

        // Process all un-enriched games in batches
        int totalProcessed = 0;
        int batchProcessed;
        do
        {
            batchProcessed = await _enrichment.EnrichUnenrichedAsync(batchSize: 10);
            totalProcessed += batchProcessed;
        } while (batchProcessed > 0);

        _logger.LogInformation("Bulk enrichment completed: {Count} games processed", totalProcessed);
        return Ok(new { Processed = totalProcessed, Message = $"Enriched {totalProcessed} games." });
    }

    /// <summary>
    /// POST: api/library/enrich/{id}?force=false
    /// Enriches (or re-enriches) a specific game by ID.
    /// Pass ?force=true to re-enrich a game that already has metadata.
    /// </summary>
    [HttpPost("enrich/{id}")]
    public async Task<IActionResult> EnrichGame(int id, [FromQuery] bool force = false)
    {
        _logger.LogInformation("Enriching game {Id} (force: {Force})", id, force);

        var success = await _enrichment.EnrichGameAsync(id, force);
        if (!success)
            return NotFound(new { Message = $"Game with ID {id} not found or already enriched (use ?force=true to re-enrich)." });

        // Reload the game to return the updated data
        var game = await _db.UnifiedGames
            .Include(g => g.Tags)
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == id);

        return Ok(new
        {
            Message = $"Game '{game?.Title}' enriched successfully.",
            game?.CoverUrl,
            game?.Summary,
            game?.Genres,
            Tags = game?.Tags.Select(t => t.Name).ToList(),
            game?.FirstReleaseDate,
            game?.Rating,
            game?.MetadataSource,
            game?.EnrichedAt
        });
    }

    /// <summary>
    /// GET: api/library/enrichment-history
    /// Returns the most recent enrichment audit logs.
    /// Optionally filter by a specific gameId.
    /// </summary>
    [HttpGet("enrichment-history")]
    public async Task<IActionResult> GetEnrichmentHistory(
        [FromQuery] int? gameId, 
        [FromQuery] int limit = 50)
    {
        var query = _db.EnrichmentAuditLogs
            .Include(l => l.UnifiedGame)
            .AsNoTracking();

        if (gameId.HasValue)
            query = query.Where(l => l.UnifiedGameId == gameId.Value);

        var logs = await query
            .OrderByDescending(l => l.Timestamp)
            .Take(limit)
            .Select(l => new
            {
                l.Id,
                l.UnifiedGameId,
                GameTitle = l.UnifiedGame != null ? l.UnifiedGame.Title : "Unknown",
                l.Timestamp,
                l.ProviderName,
                l.Status,
                l.ErrorMessage,
                l.ProviderGameId,
                l.MatchedTitle
            })
            .ToListAsync();

        return Ok(logs);
    }

    /// <summary>
    /// GET: api/library/sync-history
    /// Returns the most recent unification sync audit logs.
    /// Optionally filter by a specific platform.
    /// </summary>
    [HttpGet("sync-history")]
    public async Task<IActionResult> GetSyncHistory(
        [FromQuery] Platform? platform,
        [FromQuery] int limit = 50)
    {
        var query = _db.SyncAuditLogs.AsNoTracking();

        if (platform.HasValue)
            query = query.Where(l => l.Platform == platform.Value);

        var logs = await query
            .OrderByDescending(l => l.Timestamp)
            .Take(limit)
            .ToListAsync();

        return Ok(logs);
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

        using var transaction = await _db.Database.BeginTransactionAsync();

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
        await transaction.CommitAsync();

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

        using var transaction = await _db.Database.BeginTransactionAsync();

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
        await transaction.CommitAsync();

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
