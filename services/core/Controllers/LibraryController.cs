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
    private readonly SyncSchedulerStatus _syncStatus;
    private readonly ActivityLogService _activityLog;

    public LibraryController(
        UnificationService unifier,
        EnrichmentService enrichment,
        CoreContext db,
        ILogger<LibraryController> logger,
        SyncSchedulerStatus syncStatus,
        ActivityLogService activityLog)
    {
        _unifier = unifier;
        _enrichment = enrichment;
        _db = db;
        _logger = logger;
        _syncStatus = syncStatus;
        _activityLog = activityLog;
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
        await _activityLog.LogAsync("SyncStarted", "Manual sync triggered from admin dashboard");

        var count = await _unifier.RunFullSyncAsync();

        _logger.LogInformation("Library sync completed: {Count} items processed", count);
        await _activityLog.LogAsync("SyncFinished", $"Manual sync completed — {count} items processed");
        return Ok(new { Processed = count, Message = "Sync completed successfully." });
    }

    /// <summary>
    /// GET: api/library/sync/status
    /// Returns the current state of the background sync scheduler:
    /// whether it's enabled, currently running, when the last run completed,
    /// when the next run is scheduled, and the result of the last run.
    /// </summary>
    [HttpGet("sync/status")]
    public IActionResult GetSyncStatus()
    {
        return Ok(new
        {
            _syncStatus.IsEnabled,
            _syncStatus.IsRunning,
            _syncStatus.LastRunAt,
            _syncStatus.NextRunAt,
            _syncStatus.LastResult,
            _syncStatus.ItemsProcessed
        });
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
        [FromQuery] PlayStatus[]? playStatus,
        [FromQuery] string[]? developer,
        [FromQuery] string[]? publisher,
        [FromQuery] string? sortBy,
        [FromQuery] string sortOrder = "asc",
        [FromQuery] bool includeIgnored = false,
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

        if (!includeIgnored)
        {
            query = query.Where(g => !g.IsIgnored);
        }

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

        // Filter by PlayStatus if any are provided
        if (playStatus != null && playStatus.Length > 0)
        {
            query = query.Where(g => playStatus.Contains(g.PlayStatus));
        }

        // Filter by developers (OR logic: game must match at least one selected developer)
        if (developer != null && developer.Any(d => !string.IsNullOrWhiteSpace(d)))
        {
            var lowerDevs = developer.Where(d => !string.IsNullOrWhiteSpace(d))
                                     .Select(d => d.ToLower())
                                     .ToList();
            query = query.Where(g => g.Developers != null &&
                lowerDevs.Any(d => g.Developers.ToLower().Contains(d)));
        }

        // Filter by publishers (OR logic: game must match at least one selected publisher)
        if (publisher != null && publisher.Any(p => !string.IsNullOrWhiteSpace(p)))
        {
            var lowerPubs = publisher.Where(p => !string.IsNullOrWhiteSpace(p))
                                     .Select(p => p.ToLower())
                                     .ToList();
            query = query.Where(g => g.Publishers != null &&
                lowerPubs.Any(p => g.Publishers.ToLower().Contains(p)));
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
            g.PlayStatus,
            g.UserRating,
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
            game.PlayStatus,
            game.UserRating,
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
    /// POST: api/library/enrich?force=false
    /// Triggers enrichment for un-enriched games.
    /// Pass ?force=true to reset ALL games for re-enrichment (does NOT re-enrich immediately;
    /// call this endpoint again without force to start the batch).
    /// Rate-limited to ~3 req/sec.
    /// </summary>
    [HttpPost("enrich")]
    public async Task<IActionResult> EnrichAll([FromQuery] bool force = false)
    {
        if (force)
        {
            var resetCount = await _enrichment.ResetAllEnrichmentsAsync();
            _logger.LogInformation("Force reset: {Count} games marked for re-enrichment", resetCount);
            return Ok(new
            {
                Reset = resetCount,
                Message = $"Reset {resetCount} games. Call POST /api/library/enrich to start batch enrichment."
            });
        }

        var unenrichedCount = await _enrichment.GetUnenrichedCountAsync();
        if (unenrichedCount == 0)
            return Ok(new { Processed = 0, Message = "All games are already enriched." });

        _logger.LogInformation("Starting bulk enrichment of {Count} un-enriched games", unenrichedCount);

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
    /// POST: api/library/enrich/stale
    /// Finds games that were marked as enriched but received no metadata
    /// (CoverUrl and Summary both null) and resets their EnrichedAt.
    /// Call POST /api/library/enrich afterwards to re-process them.
    /// </summary>
    [HttpPost("enrich/stale")]
    public async Task<IActionResult> EnrichStale()
    {
        var resetCount = await _enrichment.ResetStaleEnrichmentsAsync();

        if (resetCount == 0)
            return Ok(new { Reset = 0, Message = "No stale enrichments found — all enriched games have metadata." });

        return Ok(new
        {
            Reset = resetCount,
            Message = $"Reset {resetCount} stale games. Call POST /api/library/enrich to re-enrich them."
        });
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
    /// Optionally filter by gameId and/or status (e.g. "Error", "NotFound", "Success").
    /// </summary>
    [HttpGet("enrichment-history")]
    public async Task<IActionResult> GetEnrichmentHistory(
        [FromQuery] int? gameId,
        [FromQuery] string? status,
        [FromQuery] int limit = 50)
    {
        var query = _db.EnrichmentAuditLogs
            .Include(l => l.UnifiedGame)
            .AsNoTracking();

        if (gameId.HasValue)
            query = query.Where(l => l.UnifiedGameId == gameId.Value);

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(l => l.Status.ToLower() == status.ToLower());

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

    /// <summary>
    /// GET: api/library/enrichment-stats
    /// Returns aggregate counts for the admin dashboard:
    /// total games, enriched, unenriched, error count, not-found count,
    /// and the timestamp of the most recent enrichment.
    /// </summary>
    [HttpGet("enrichment-stats")]
    public async Task<IActionResult> GetEnrichmentStats()
    {
        var totalGames = await _db.UnifiedGames.CountAsync();
        var enrichedCount = await _db.UnifiedGames.CountAsync(g => g.EnrichedAt != null);
        var unenrichedCount = totalGames - enrichedCount;

        // Count distinct error and not-found games from the most recent audit log per game
        var errorCount = await _db.EnrichmentAuditLogs
            .Where(l => l.Status.ToLower() == "error")
            .Select(l => l.UnifiedGameId)
            .Distinct()
            .CountAsync();

        var notFoundCount = await _db.EnrichmentAuditLogs
            .Where(l => l.Status.ToLower() == "notfound")
            .Select(l => l.UnifiedGameId)
            .Distinct()
            .CountAsync();

        var lastEnrichedAt = await _db.UnifiedGames
            .Where(g => g.EnrichedAt != null)
            .OrderByDescending(g => g.EnrichedAt)
            .Select(g => g.EnrichedAt)
            .FirstOrDefaultAsync();

        return Ok(new
        {
            TotalGames = totalGames,
            EnrichedCount = enrichedCount,
            UnenrichedCount = unenrichedCount,
            ErrorCount = errorCount,
            NotFoundCount = notFoundCount,
            LastEnrichedAt = lastEnrichedAt
        });
    }

    /// <summary>
    /// GET: api/library/stats
    /// Returns comprehensive library statistics for the analytics dashboard:
    /// per-platform counts, genre distribution, rating histogram,
    /// top-rated games, recently added games, and summary metrics.
    /// </summary>
    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        // --- Summary counts ---
        var totalGames = await _db.UnifiedGames.CountAsync(g => !g.IsIgnored);
        var enrichedCount = await _db.UnifiedGames.CountAsync(g => g.EnrichedAt != null && !g.IsIgnored);
        var totalPlatformLinks = await _db.LibraryItems.CountAsync(i => i.UnifiedGame != null && !i.UnifiedGame.IsIgnored);

        // Games that appear on more than one platform
        var multiPlatformCount = await _db.UnifiedGames
            .Where(g => g.Items.Count > 1 && !g.IsIgnored)
            .CountAsync();

        // Average rating (only games that have one)
        var averageRating = await _db.UnifiedGames
            .Where(g => g.Rating != null && !g.IsIgnored)
            .AverageAsync(g => (double?)g.Rating) ?? 0;

        // --- Per-platform breakdown ---
        var platformCounts = await _db.LibraryItems
            .Where(i => i.UnifiedGame != null && !i.UnifiedGame.IsIgnored)
            .GroupBy(i => i.Platform)
            .Select(g => new { Platform = g.Key.ToString(), Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToListAsync();

        // --- Genre distribution (parse comma-separated Genres field) ---
        var rawGenres = await _db.UnifiedGames
            .Where(g => g.Genres != null && g.Genres != "" && !g.IsIgnored)
            .Select(g => g.Genres!)
            .ToListAsync();

        var genreCounts = rawGenres
            .SelectMany(csv => csv.Split(", "))
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .GroupBy(g => g.Trim())
            .Select(g => new { Name = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(15)
            .ToList();

        // --- Rating distribution (10-point buckets) ---
        var ratedGames = await _db.UnifiedGames
            .Where(g => g.Rating != null && !g.IsIgnored)
            .Select(g => g.Rating!.Value)
            .ToListAsync();

        var ratingDistribution = Enumerable.Range(0, 10)
            .Select(i => new
            {
                Bucket = $"{i * 10}-{i * 10 + 9}",
                Count = ratedGames.Count(r => r >= i * 10 && r < (i + 1) * 10)
            })
            // Add the 100 bucket (games rated exactly 100 fall into 90-100)
            .Select(b => b.Bucket == "90-99"
                ? new { Bucket = "90-100", Count = ratedGames.Count(r => r >= 90) }
                : b)
            .Where(b => b.Count > 0)
            .ToList();

        // --- Top-rated games (top 10) ---
        var topRated = await _db.UnifiedGames
            .Where(g => g.Rating != null && g.Rating > 0 && !g.IsIgnored)
            .OrderByDescending(g => g.Rating)
            .Take(10)
            .Select(g => new
            {
                g.Id,
                g.Title,
                g.CoverUrl,
                g.Rating,
                g.Genres
            })
            .ToListAsync();

        // --- Play status distribution ---
        var playStatusCounts = await _db.UnifiedGames
            .Where(g => !g.IsIgnored)
            .GroupBy(g => g.PlayStatus)
            .Select(g => new { Status = g.Key.ToString(), Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToListAsync();

        // --- Recently added (last 10 enriched) ---
        var recentlyAdded = await _db.UnifiedGames
            .Include(g => g.Items)
            .Where(g => g.EnrichedAt != null && !g.IsIgnored)
            .OrderByDescending(g => g.EnrichedAt)
            .Take(10)
            .Select(g => new
            {
                g.Id,
                g.Title,
                g.CoverUrl,
                g.EnrichedAt,
                Platforms = g.Items.Select(i => i.Platform.ToString()).ToList()
            })
            .ToListAsync();

        return Ok(new
        {
            TotalGames = totalGames,
            EnrichedCount = enrichedCount,
            TotalPlatformLinks = totalPlatformLinks,
            MultiPlatformCount = multiPlatformCount,
            AverageRating = Math.Round(averageRating, 1),
            PlatformCounts = platformCounts,
            GenreCounts = genreCounts,
            PlayStatusCounts = playStatusCounts,
            RatingDistribution = ratingDistribution,
            TopRated = topRated,
            RecentlyAdded = recentlyAdded
        });
    }

    /// <summary>
    /// POST: api/library/retroactive-ignore
    /// Scans all currently visible (IsIgnored = false) games and flags them as
    /// ignored if their title contains any of the predefined IgnoredKeywords.
    /// Used for retroactive cleanup after the auto-ignore logic was introduced.
    /// </summary>
    [HttpPost("retroactive-ignore")]
    public async Task<IActionResult> RunRetroactiveIgnore()
    {
        _logger.LogInformation("Starting retroactive ignore scan...");

        // Fetch all games that are not currently ignored
        var activeGames = await _db.UnifiedGames
            .Where(g => !g.IsIgnored)
            .ToListAsync();

        int newlyIgnoredCount = 0;
        var ignoredTitles = new List<string>();

        foreach (var game in activeGames)
        {
            var lowerTitle = game.Title.ToLowerInvariant();
            
            // If the title contains any of the ignored keywords, flag it
            if (UnificationService.IgnoredKeywords.Any(k => lowerTitle.Contains(k)))
            {
                game.IsIgnored = true;
                newlyIgnoredCount++;
                ignoredTitles.Add(game.Title);
            }
        }

        if (newlyIgnoredCount > 0)
        {
            await _db.SaveChangesAsync();
            _logger.LogInformation("Retroactive scan complete: Ignored {Count} additional games", newlyIgnoredCount);
        }
        else
        {
            _logger.LogInformation("Retroactive scan complete: No additional games matched the ignore criteria.");
        }

        return Ok(new
        {
            Processed = activeGames.Count,
            NewlyIgnoredCount = newlyIgnoredCount,
            IgnoredTitles = ignoredTitles,
            Message = $"Scanned {activeGames.Count} games and hid {newlyIgnoredCount} matching the ignore criteria."
        });
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
        await _activityLog.LogAsync("Merge", $"Merged '{source.Title}' into '{target.Title}'");

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
        await _activityLog.LogAsync("Split", $"Split '{item.Platform}' entry into new game '{newTitle}'");

        return Ok(new
        {
            Message = $"Split '{item.Platform}' entry into new game '{newTitle}'.",
            NewGameId = newGame.Id,
            OriginalGameId = item.UnifiedGame.Id
        });
    }

    // =========================================================================
    // Personal Tracking Endpoints
    // =========================================================================

    /// <summary>
    /// PATCH: api/library/{id}/personal
    /// Updates the personal play status and user rating for a game.
    /// This also sets `IsPlayStatusManual = true` so future syncs don't override the user's decision.
    /// </summary>
    [HttpPatch("{id}/personal")]
    public async Task<IActionResult> UpdatePersonalTracking(int id, [FromBody] PersonalUpdateRequest request)
    {
        var game = await _db.UnifiedGames.FirstOrDefaultAsync(g => g.Id == id);

        if (game == null)
            return NotFound(new { Message = $"Game with ID {id} not found" });

        game.PlayStatus = request.PlayStatus;
        game.UserRating = request.UserRating;
        if (request.IsIgnored.HasValue) game.IsIgnored = request.IsIgnored.Value;
        game.IsPlayStatusManual = true; // Lock it so syncs don't overwrite

        await _db.SaveChangesAsync();

        _logger.LogInformation("Updated personal tracking for game {Id}: Status={Status}, Rating={Rating}, IsIgnored={IsIgnored}",
            id, request.PlayStatus, request.UserRating?.ToString() ?? "none", game.IsIgnored);

        return Ok(new
        {
            Message = "Personal tracking updated successfully.",
            game.PlayStatus,
            game.UserRating
        });
    }

    // =========================================================================
    // Activity Feed Endpoint
    // =========================================================================

    /// <summary>
    /// GET: /api/activity
    /// Returns the latest 20 activity log entries for the admin dashboard feed.
    /// Uses an absolute route so the URL is /api/activity (not /api/library/activity).
    /// </summary>
    [HttpGet("/api/activity")]
    public async Task<IActionResult> GetActivity()
    {
        var entries = await _activityLog.GetRecentAsync();
        return Ok(entries.Select(a => new
        {
            a.Id,
            a.Timestamp,
            a.EventType,
            a.Message,
            a.Details
        }));
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

/// <summary>Request body for the personal tracking update endpoint.</summary>
public class PersonalUpdateRequest
{
    public PlayStatus PlayStatus { get; set; }
    public int? UserRating { get; set; }
    public bool? IsIgnored { get; set; }
}
