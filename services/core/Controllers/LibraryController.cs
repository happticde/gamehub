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
    /// GET: api/library?search=witcher&amp;tags=RPG&amp;sortBy=rating&amp;sortOrder=desc&amp;page=1
    /// Returns a paginated list of unified games with their platform tags.
    /// Supports filtering by search text, tags, platforms, developer, and publisher.
    /// Supports sorting by title (default), rating, or releasedate.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetLibrary(
        [FromQuery] string? search,
        [FromQuery] string[]? tags,
        [FromQuery] Platform[]? platforms,
        [FromQuery] string? developer,
        [FromQuery] string? publisher,
        [FromQuery] string? sortBy,
        [FromQuery] string sortOrder = "asc",
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

        // Filter by tags if any are provided
        if (tags != null && tags.Any(t => !string.IsNullOrWhiteSpace(t)))
        {
            var lowerTags = tags.Where(t => !string.IsNullOrWhiteSpace(t))
                                .Select(t => t.ToLower())
                                .ToList();
            
            query = query.Where(g => g.Tags.Any(t => lowerTags.Contains(t.Name.ToLower())));
        }

        // Filter by platforms if any are provided
        if (platforms != null && platforms.Length > 0)
        {
            query = query.Where(g => g.Items.Any(i => platforms.Contains(i.Platform)));
        }

        // Filter by developer (substring match on the comma-separated field)
        if (!string.IsNullOrWhiteSpace(developer))
        {
            query = query.Where(g => g.Developers != null && g.Developers.ToLower().Contains(developer.ToLower()));
        }

        // Filter by publisher (substring match on the comma-separated field)
        if (!string.IsNullOrWhiteSpace(publisher))
        {
            query = query.Where(g => g.Publishers != null && g.Publishers.ToLower().Contains(publisher.ToLower()));
        }

        // Get total count for pagination metadata
        var totalCount = await query.CountAsync();

        // Dynamic sorting — default to title ascending
        query = (sortBy?.ToLower(), sortOrder.ToLower()) switch
        {
            ("rating", "desc")       => query.OrderByDescending(g => g.Rating),
            ("rating", _)            => query.OrderBy(g => g.Rating),
            ("releasedate", "desc")  => query.OrderByDescending(g => g.FirstReleaseDate),
            ("releasedate", _)       => query.OrderBy(g => g.FirstReleaseDate),
            ("title", "desc")        => query.OrderByDescending(g => g.Title),
            _                        => query.OrderBy(g => g.Title), // default: title asc
        };

        // Fetch the current page
        var games = await query
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
            g.Developers,
            g.Publishers,
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

    /// <summary>
    /// GET: api/library/{id}
    /// Returns the full immersive payload for a single unified game, including external links
    /// and immersive media arrays (artworks, screenshots) mapped from the comma-separated strings.
    /// </summary>
    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id)
    {
        var game = await _db.UnifiedGames
            .Include(g => g.Items)
            .Include(g => g.Tags)
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == id);

        if (game == null)
            return NotFound(new { Message = $"Game with ID {id} not found." });

        List<string> SplitCsv(string? csv) =>
            string.IsNullOrWhiteSpace(csv) ? new List<string>() : csv.Split(", ").ToList();

        var result = new
        {
            game.Id,
            game.Title,
            game.Slug,
            game.CoverUrl,
            game.Summary,
            
            // Expanded Metadata Fields parsed back into arrays
            Genres = SplitCsv(game.Genres),
            Platforms = SplitCsv(game.Platforms),
            Artworks = SplitCsv(game.Artworks),
            Screenshots = SplitCsv(game.Screenshots),
            Developers = SplitCsv(game.Developers),
            Publishers = SplitCsv(game.Publishers),
            Tags = game.Tags.Select(t => t.Name).ToList(),

            game.FirstReleaseDate,
            game.Rating,
            game.MetadataSource,
            game.EnrichedAt,
            
            Items = game.Items.Select(i => new
            {
                i.Platform,
                i.ExternalId,
                i.LastSynced
            }).ToList()
        };

        return Ok(result);
    }

    /// <summary>
    /// GET: api/library/tags
    /// Returns a flat list of all unique tag (genre) names currently in the database.
    /// Used by frontend dropdown filters.
    /// </summary>
    [HttpGet("tags")]
    public async Task<IActionResult> GetTags()
    {
        var tags = await _db.Tags
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToListAsync();

        return Ok(tags);
    }

    /// <summary>
    /// GET: api/library/developers
    /// Returns a deduplicated, sorted list of all developer names across all games.
    /// Parses the comma-separated Developers field.
    /// </summary>
    [HttpGet("developers")]
    public async Task<IActionResult> GetDevelopers()
    {
        var rawValues = await _db.UnifiedGames
            .Where(g => g.Developers != null && g.Developers != "")
            .Select(g => g.Developers!)
            .ToListAsync();

        var developers = rawValues
            .SelectMany(csv => csv.Split(", "))
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => d.Trim())
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        return Ok(developers);
    }

    /// <summary>
    /// GET: api/library/publishers
    /// Returns a deduplicated, sorted list of all publisher names across all games.
    /// Parses the comma-separated Publishers field.
    /// </summary>
    [HttpGet("publishers")]
    public async Task<IActionResult> GetPublishers()
    {
        var rawValues = await _db.UnifiedGames
            .Where(g => g.Publishers != null && g.Publishers != "")
            .Select(g => g.Publishers!)
            .ToListAsync();

        var publishers = rawValues
            .SelectMany(csv => csv.Split(", "))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct()
            .OrderBy(p => p)
            .ToList();

        return Ok(publishers);
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
