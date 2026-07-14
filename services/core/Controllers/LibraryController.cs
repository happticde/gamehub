using GameHub.Core.Data;
using GameHub.Core.Models.ProviderSync;
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
    private const int MaxCustomTagsPerGame = 50;
    private const int MaxCustomTagNameLength = 64;

    private readonly UnificationService _unifier;
    private readonly ProviderSyncStatusService _providerSyncStatus;
    private readonly EnrichmentService _enrichment;
    private readonly CoreContext _db;
    private readonly ILogger<LibraryController> _logger;
    private readonly SyncSchedulerStatus _syncStatus;
    private readonly ITenantContext _tenantContext;
    private readonly ActivityLogService _activityLog;
    private readonly IGameMetadataProvider _metadataProvider;
    private readonly PhysicalGameCaptureService _physicalCapture;
    private readonly PhysicalProductLookupService _physicalProductLookup;

    public LibraryController(
        UnificationService unifier,
        ProviderSyncStatusService providerSyncStatus,
        EnrichmentService enrichment,
        CoreContext db,
        ILogger<LibraryController> logger,
        SyncSchedulerStatus syncStatus,
        ITenantContext tenantContext,
        ActivityLogService activityLog,
        IGameMetadataProvider metadataProvider,
        PhysicalGameCaptureService? physicalCapture = null,
        PhysicalProductLookupService? physicalProductLookup = null)
    {
        _unifier = unifier;
        _providerSyncStatus = providerSyncStatus;
        _enrichment = enrichment;
        _db = db;
        _logger = logger;
        _syncStatus = syncStatus;
        _tenantContext = tenantContext;
        _activityLog = activityLog;
        _metadataProvider = metadataProvider;
        _physicalCapture = physicalCapture ?? new PhysicalGameCaptureService(db);
        _physicalProductLookup = physicalProductLookup ?? CreateDisabledPhysicalProductLookup(_physicalCapture);
    }

    /// <summary>
    /// POST: api/library/sync
    /// Triggers a fast incremental sync from cached Ingest data into Core without contacting any
    /// external provider.
    /// </summary>
    [HttpPost("sync")]
    public Task<IActionResult> Sync(CancellationToken cancellationToken) =>
        RunSyncAsync(SyncExecutionMode.Cached, "Manual cached sync", cancellationToken);

    /// <summary>
    /// POST: api/library/sync/refresh
    /// Refreshes providers whose last complete retrieval is older than the configured TTL, then
    /// synchronizes cached Ingest data into Core.
    /// </summary>
    [HttpPost("sync/refresh")]
    public Task<IActionResult> RefreshAndSync(CancellationToken cancellationToken) =>
        RunSyncAsync(SyncExecutionMode.RefreshIfStale, "Manual refresh-if-stale sync", cancellationToken);

    /// <summary>
    /// POST: api/library/sync/refresh/force
    /// Bypasses provider freshness but retains credentials, tenant policy, authentication, and
    /// active rate-limit cooldowns.
    /// </summary>
    [HttpPost("sync/refresh/force")]
    public Task<IActionResult> ForceRefreshAndSync(CancellationToken cancellationToken) =>
        RunSyncAsync(SyncExecutionMode.ForceRefresh, "Manual forced-refresh sync", cancellationToken);

    private async Task<IActionResult> RunSyncAsync(
        SyncExecutionMode mode,
        string activityDescription,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting library sync in {SyncMode} mode", mode);
        await _activityLog.LogAsync("SyncStarted", $"{activityDescription} triggered from admin dashboard");

        LibrarySyncResult result;
        try
        {
            result = await _unifier.RunSyncAsync(mode, cancellationToken);
        }
        catch (TenantPlatformSyncDisabledException exception)
        {
            _logger.LogWarning(
                "Rejected platform sync for tenant {TenantId} because no supported credential is active",
                exception.TenantId);
            await _activityLog.LogAsync(
                "SyncSkipped",
                "Platform sync requires a supported encrypted platform credential");
            return Conflict(new
            {
                Code = "platform_sync_disabled",
                Message = "Configure a Steam, GOG, PlayStation, or Epic credential before running platform sync."
            });
        }

        _logger.LogInformation(
            "Library sync completed in {SyncMode} mode: {Count} items processed",
            mode,
            result.Processed);
        await _activityLog.LogAsync(
            "SyncFinished",
            $"{activityDescription} completed — {result.Processed} items processed");
        return Ok(result);
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
        return Ok(_syncStatus.GetSnapshot(_tenantContext.TenantId));
    }

    /// <summary>
    /// GET: api/library/sync/providers
    /// Returns the authenticated tenant's secret-free local provider freshness and cooldown state.
    /// </summary>
    [HttpGet("sync/providers")]
    public async Task<ActionResult<ProviderSyncStatusResponse>> GetProviderSyncStatus(
        CancellationToken cancellationToken)
    {
        var status = await _providerSyncStatus.GetStatusAsync(cancellationToken);
        return Ok(status);
    }

    /// <summary>
    /// GET: api/library?search=witcher&amp;tags=RPG&amp;sortBy=rating&amp;sortOrder=desc&amp;page=1
    /// Returns a paginated list of unified games with digital and physical ownership data.
    /// Supports filtering by search text, tags, digital platforms, physical platforms, developer,
    /// and publisher.
    /// Supports sorting by title (default), rating, or releasedate.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetLibrary(
        [FromQuery] string? search,
        [FromQuery] string[]? tags,
        [FromQuery] Platform[]? platforms,
        [FromQuery] PhysicalGamePlatform[]? physicalPlatforms,
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

        var query = BuildLibraryQuery(
            search,
            tags,
            platforms,
            physicalPlatforms,
            playStatus,
            developer,
            publisher,
            includeIgnored);
        
        // Includes are needed for the returned shape
        query = query.Include(g => g.Items)
                     .Include(g => g.PhysicalCopies)
                     .Include(g => g.Tags);

        // Get total count for pagination metadata
        var totalCount = await query.CountAsync();

        // Dynamic sorting — default to title ascending
        query = (sortBy?.ToLower(), sortOrder.ToLower()) switch
        {
            ("rating", "desc")       => query.OrderByDescending(g => g.Rating ?? -1),
            ("rating", _)            => query.OrderBy(g => g.Rating ?? 999),
            ("releasedate", "desc")  => query.OrderByDescending(g => g.FirstReleaseDate ?? DateTime.MinValue),
            ("releasedate", _)       => query.OrderBy(g => g.FirstReleaseDate ?? DateTime.MaxValue),
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
            }).ToList(),
            PhysicalCopies = g.PhysicalCopies
                .OrderBy(copy => copy.Platform)
                .ThenBy(copy => copy.Id)
                .Select(copy => new
                {
                    copy.Id,
                    copy.Platform,
                    copy.NormalizedGtin
                })
                .ToList()
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
    /// GET: api/library/random
    /// Returns a single random game ID that matches the given filters.
    /// Used for the "Surprise Me" functionality.
    /// </summary>
    [HttpGet("random")]
    public async Task<IActionResult> GetRandom(
        [FromQuery] string? search,
        [FromQuery] string[]? tags,
        [FromQuery] Platform[]? platforms,
        [FromQuery] PhysicalGamePlatform[]? physicalPlatforms,
        [FromQuery] PlayStatus[]? playStatus,
        [FromQuery] string[]? developer,
        [FromQuery] string[]? publisher,
        [FromQuery] bool includeIgnored = false)
    {
        var query = BuildLibraryQuery(
            search,
            tags,
            platforms,
            physicalPlatforms,
            playStatus,
            developer,
            publisher,
            includeIgnored);

        var randomGameId = await query
            .OrderBy(g => Guid.NewGuid())
            .Select(g => g.Id)
            .FirstOrDefaultAsync();

        if (randomGameId == 0)
        {
            return NotFound(new { Message = "No games found matching the current filters." });
        }

        return Ok(new { Id = randomGameId });
    }

    private IQueryable<UnifiedGame> BuildLibraryQuery(
        string? search,
        string[]? tags,
        Platform[]? platforms,
        PhysicalGamePlatform[]? physicalPlatforms,
        PlayStatus[]? playStatus,
        string[]? developer,
        string[]? publisher,
        bool includeIgnored)
    {
        var query = _db.UnifiedGames.AsNoTracking();

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

        // Physical ownership is deliberately separate from digital provider platforms.
        if (physicalPlatforms != null && physicalPlatforms.Length > 0)
        {
            query = query.Where(g =>
                g.PhysicalCopies.Any(copy => physicalPlatforms.Contains(copy.Platform)));
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

        return query;
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
            .Include(g => g.PhysicalCopies)
            .Include(g => g.Tags)
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == id);

        if (game == null)
            return NotFound(new { Message = $"Game with ID {id} not found." });

        var customTags = await _db.Set<TagAssignment>()
            .AsNoTracking()
            .Where(assignment => assignment.UnifiedGameId == id && assignment.IsCustom)
            .Select(assignment => assignment.Tag!.Name)
            .OrderBy(name => name)
            .ToListAsync();

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
            CustomTags = customTags,

            game.FirstReleaseDate,
            game.Rating,
            game.PlayStatus,
            game.UserRating,
            game.MetadataSource,
            game.EnrichedAt,
            
            Items = game.Items.Select(i => new
            {
                i.Id,
                i.Platform,
                i.ExternalId,
                i.LastSynced
            }).ToList(),
            PhysicalCopies = game.PhysicalCopies
                .OrderBy(copy => copy.Platform)
                .ThenBy(copy => copy.Id)
                .Select(copy => new
                {
                    copy.Id,
                    copy.Platform,
                    copy.NormalizedGtin
                })
                .ToList()
        };

        return Ok(result);
    }

    /// <summary>
    /// GET: api/library/{id}/physical-copies
    /// Lists the authenticated tenant's physical copies assigned to one unified game.
    /// </summary>
    [HttpGet("{id}/physical-copies")]
    public async Task<IActionResult> GetPhysicalCopies(
        int id,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Listing physical copies for game {GameId}", id);

        if (!await _db.UnifiedGames.AsNoTracking().AnyAsync(game => game.Id == id, cancellationToken))
        {
            return NotFound(new { Message = $"Game with ID {id} not found." });
        }

        var copies = await _db.PhysicalGameCopies
            .AsNoTracking()
            .Where(copy => copy.UnifiedGameId == id)
            .OrderBy(copy => copy.Platform)
            .ThenBy(copy => copy.Id)
            .Select(copy => new
            {
                copy.Id,
                copy.Platform,
                copy.NormalizedGtin
            })
            .ToListAsync(cancellationToken);

        return Ok(copies);
    }

    /// <summary>
    /// POST: api/library/{id}/physical-copies
    /// Adds one independently identified physical copy to the authenticated tenant's game.
    /// </summary>
    [HttpPost("{id}/physical-copies")]
    public async Task<IActionResult> AddPhysicalCopy(
        int id,
        [FromBody] AddPhysicalGameCopyRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(request.Platform))
        {
            return BadRequest(new { Message = $"Physical platform '{request.Platform}' is not supported." });
        }

        string? normalizedGtin = null;
        if (!string.IsNullOrWhiteSpace(request.Gtin))
        {
            var normalization = GtinNormalizer.Normalize(request.Gtin);
            if (!normalization.IsValid)
            {
                return BadRequest(new { Code = "invalid_gtin", Message = normalization.Error });
            }

            normalizedGtin = normalization.NormalizedGtin;
        }

        if (!await _db.UnifiedGames.AnyAsync(game => game.Id == id, cancellationToken))
        {
            return NotFound(new { Message = $"Game with ID {id} not found." });
        }

        var copy = new PhysicalGameCopy
        {
            UnifiedGameId = id,
            Platform = request.Platform,
            NormalizedGtin = normalizedGtin
        };
        _db.PhysicalGameCopies.Add(copy);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Added physical copy {PhysicalCopyId} on {PhysicalPlatform} to game {GameId}",
            copy.Id,
            copy.Platform,
            id);

        return StatusCode(StatusCodes.Status201Created, new
        {
            copy.Id,
            copy.Platform,
            copy.NormalizedGtin
        });
    }

    /// <summary>
    /// GET: api/library/physical-copies/gtin-lookup
    /// Returns every tenant-local game candidate previously confirmed for one normalized GTIN.
    /// </summary>
    [HttpGet("physical-copies/gtin-lookup")]
    public async Task<IActionResult> LookupPhysicalGtin(
        [FromQuery] string? gtin,
        CancellationToken cancellationToken = default)
    {
        var normalization = GtinNormalizer.Normalize(gtin);
        if (!normalization.IsValid)
        {
            return BadRequest(new { Code = "invalid_gtin", Message = normalization.Error });
        }

        var lookup = await _physicalProductLookup.LookupAsync(
            normalization.NormalizedGtin!,
            cancellationToken);
        _logger.LogInformation(
            "Physical GTIN lookup returned {CandidateCount} tenant-local candidates and {SuggestionCount} external suggestions",
            lookup.LocalCandidates.Count,
            lookup.ExternalSuggestions.Count);

        return Ok(new
        {
            normalization.NormalizedGtin,
            Candidates = lookup.LocalCandidates,
            ExternalSuggestions = lookup.ExternalSuggestions
        });
    }

    private static PhysicalProductLookupService CreateDisabledPhysicalProductLookup(
        PhysicalGameCaptureService physicalCapture)
    {
        var settings = PhysicalProductCatalogSettings.FromConfiguration(
            new ConfigurationBuilder().Build());
        var client = new PhysicalProductCatalogClient(
            new DisabledPhysicalProductCatalogProvider(),
            settings,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PhysicalProductCatalogClient>.Instance);
        return new PhysicalProductLookupService(physicalCapture, client);
    }

    /// <summary>
    /// POST: api/library/physical-copies/capture
    /// Atomically adds a confirmed physical copy to an existing game or a new manual game.
    /// </summary>
    [HttpPost("physical-copies/capture")]
    public async Task<IActionResult> CapturePhysicalGame(
        [FromBody] CapturePhysicalGameRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!request.Confirmed)
        {
            return BadRequest(new
            {
                Code = "confirmation_required",
                Message = "Confirm the game, physical platform, and optional GTIN before saving."
            });
        }

        if (!Enum.IsDefined(request.Platform))
        {
            return BadRequest(new { Message = $"Physical platform '{request.Platform}' is not supported." });
        }

        var hasExistingGame = request.ExistingGameId.HasValue;
        var hasManualGame = !string.IsNullOrWhiteSpace(request.ManualGameTitle);
        if (hasExistingGame == hasManualGame)
        {
            return BadRequest(new
            {
                Code = "invalid_capture_target",
                Message = "Choose exactly one existing game or explicitly provide a new manual game title."
            });
        }

        string? normalizedGtin = null;
        if (!string.IsNullOrWhiteSpace(request.Gtin))
        {
            var normalization = GtinNormalizer.Normalize(request.Gtin);
            if (!normalization.IsValid)
            {
                return BadRequest(new { Code = "invalid_gtin", Message = normalization.Error });
            }

            normalizedGtin = normalization.NormalizedGtin;
        }

        PhysicalCaptureResult? result;
        try
        {
            result = hasExistingGame
                ? await _physicalCapture.CaptureExistingAsync(
                    request.ExistingGameId!.Value,
                    request.Platform,
                    normalizedGtin,
                    cancellationToken)
                : await _physicalCapture.CaptureManualAsync(
                    request.ManualGameTitle!,
                    request.Platform,
                    normalizedGtin,
                    cancellationToken);
        }
        catch (PhysicalCaptureValidationException exception)
        {
            return BadRequest(new { Code = "invalid_manual_game", Message = exception.Message });
        }

        if (result is null)
        {
            return NotFound(new { Message = $"Game with ID {request.ExistingGameId} not found." });
        }

        _logger.LogInformation(
            "Captured physical copy {PhysicalCopyId} on {PhysicalPlatform} for game {GameId}; manual game created: {CreatedManualGame}",
            result.Copy.Id,
            result.Copy.Platform,
            result.GameId,
            result.CreatedManualGame);

        return StatusCode(StatusCodes.Status201Created, new
        {
            result.GameId,
            result.Title,
            result.Slug,
            result.CreatedManualGame,
            Copy = new
            {
                result.Copy.Id,
                result.Copy.Platform,
                result.Copy.NormalizedGtin
            }
        });
    }

    /// <summary>
    /// DELETE: api/library/{id}/physical-copies/{copyId}
    /// Removes one concrete physical copy owned by the authenticated tenant.
    /// </summary>
    [HttpDelete("{id}/physical-copies/{copyId}")]
    public async Task<IActionResult> RemovePhysicalCopy(
        int id,
        int copyId,
        CancellationToken cancellationToken = default)
    {
        var copy = await _db.PhysicalGameCopies.FirstOrDefaultAsync(
            candidate => candidate.Id == copyId && candidate.UnifiedGameId == id,
            cancellationToken);
        if (copy is null)
        {
            return NotFound(new { Message = $"Physical copy with ID {copyId} was not found for game {id}." });
        }

        _db.PhysicalGameCopies.Remove(copy);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Removed physical copy {PhysicalCopyId} from game {GameId}",
            copyId,
            id);
        return NoContent();
    }

    /// <summary>
    /// GET: api/library/tags
    /// Returns the authenticated tenant's saved tag names for library filters.
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
    /// GET: api/library/tag-suggestions
    /// Returns categorized tag suggestions owned by the authenticated tenant. Custom definitions
    /// are ordered before provider-created system definitions.
    /// </summary>
    [HttpGet("tag-suggestions")]
    public async Task<IActionResult> GetTagSuggestions(CancellationToken cancellationToken = default)
    {
        var suggestions = await _db.Tags
            .AsNoTracking()
            .OrderByDescending(tag => tag.IsCustomDefinition)
            .ThenBy(tag => tag.Name)
            .Select(tag => new
            {
                tag.Id,
                tag.Name,
                IsCustom = tag.IsCustomDefinition,
                UsageCount = tag.Games.Count
            })
            .ToListAsync(cancellationToken);

        return Ok(suggestions);
    }

    /// <summary>
    /// DELETE: api/library/tags/{tagId}
    /// Deletes one custom tag definition owned by the authenticated tenant and its assignments.
    /// Provider-created system definitions cannot be deleted through this endpoint.
    /// </summary>
    [HttpDelete("tags/{tagId:int}")]
    public async Task<IActionResult> DeleteCustomTag(
        int tagId,
        CancellationToken cancellationToken = default)
    {
        var tag = await _db.Tags
            .FirstOrDefaultAsync(candidate => candidate.Id == tagId, cancellationToken);
        if (tag is null)
        {
            return NotFound(new { Message = $"Tag with ID {tagId} not found." });
        }

        if (!tag.IsCustomDefinition)
        {
            return BadRequest(new
            {
                Code = "system_tag_cannot_be_deleted",
                Message = "Provider-created system tags cannot be deleted."
            });
        }

        var removedAssignments = await _db.Set<TagAssignment>()
            .CountAsync(assignment => assignment.TagId == tagId, cancellationToken);
        _db.Tags.Remove(tag);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Deleted custom tag {TagId} and {AssignmentCount} assignments",
            tagId,
            removedAssignments);

        return Ok(new
        {
            tag.Id,
            tag.Name,
            RemovedAssignments = removedAssignments
        });
    }

    /// <summary>
    /// PUT: api/library/{id}/tags
    /// Replaces the authenticated tenant's custom tags for one game. Tag definitions remain saved
    /// after removal so they can be suggested again, while automatic genre assignments are kept.
    /// </summary>
    [HttpPut("{id}/tags")]
    public async Task<IActionResult> UpdateCustomTags(
        int id,
        [FromBody] CustomTagUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Tags is null)
        {
            return BadRequest(new { Message = "The tags collection is required." });
        }

        var normalizedNames = new List<string>();
        var requestedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawName in request.Tags)
        {
            var normalizedName = NormalizeCustomTagName(rawName);
            if (normalizedName.Length == 0)
            {
                return BadRequest(new { Message = "Tag names cannot be empty." });
            }

            if (normalizedName.Length > MaxCustomTagNameLength)
            {
                return BadRequest(new
                {
                    Message = $"Tag names cannot exceed {MaxCustomTagNameLength} characters."
                });
            }

            if (normalizedName.Any(char.IsControl))
            {
                return BadRequest(new { Message = "Tag names cannot contain control characters." });
            }

            if (requestedNames.Add(normalizedName))
            {
                normalizedNames.Add(normalizedName);
            }
        }

        if (normalizedNames.Count > MaxCustomTagsPerGame)
        {
            return BadRequest(new
            {
                Message = $"A game cannot have more than {MaxCustomTagsPerGame} custom tags."
            });
        }

        var game = await _db.UnifiedGames
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (game is null)
        {
            return NotFound(new { Message = $"Game with ID {id} not found." });
        }

        var assignments = await _db.Set<TagAssignment>()
            .Include(assignment => assignment.Tag)
            .Where(assignment => assignment.UnifiedGameId == id)
            .ToListAsync(cancellationToken);
        var assignmentsByName = new Dictionary<string, TagAssignment>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in assignments.Where(assignment => assignment.Tag is not null))
        {
            assignmentsByName.TryAdd(assignment.Tag!.Name, assignment);
        }

        var tenantTagsByName = new Dictionary<string, Tag>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in await _db.Tags.ToListAsync(cancellationToken))
        {
            tenantTagsByName.TryAdd(tag.Name, tag);
        }

        var automaticGenreNames = SplitTagNames(game.Genres);
        foreach (var assignment in assignments.Where(assignment => assignment.IsCustom))
        {
            if (assignment.Tag is not null && requestedNames.Contains(assignment.Tag.Name))
            {
                continue;
            }

            if (assignment.Tag is not null && automaticGenreNames.Contains(assignment.Tag.Name))
            {
                assignment.IsCustom = false;
            }
            else
            {
                _db.Set<TagAssignment>().Remove(assignment);
            }
        }

        var savedNames = new List<string>();
        foreach (var requestedName in normalizedNames)
        {
            if (assignmentsByName.TryGetValue(requestedName, out var existingAssignment))
            {
                existingAssignment.IsCustom = true;
                savedNames.Add(existingAssignment.Tag!.Name);
                continue;
            }

            if (!tenantTagsByName.TryGetValue(requestedName, out var tag))
            {
                tag = new Tag
                {
                    Name = requestedName,
                    IsCustomDefinition = true
                };
                _db.Tags.Add(tag);
                tenantTagsByName.Add(requestedName, tag);
            }

            _db.Set<TagAssignment>().Add(new TagAssignment
            {
                UnifiedGame = game,
                Tag = tag,
                IsCustom = true
            });
            savedNames.Add(tag.Name);
        }

        await _db.SaveChangesAsync(cancellationToken);
        savedNames.Sort(StringComparer.OrdinalIgnoreCase);

        _logger.LogInformation(
            "Updated {CustomTagCount} custom tags for game {GameId}",
            savedNames.Count,
            id);

        return Ok(new { CustomTags = savedNames });
    }

    private static string NormalizeCustomTagName(string? name) =>
        string.Join(' ', (name ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static HashSet<string> SplitTagNames(string? names) =>
        string.IsNullOrWhiteSpace(names)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

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
    /// GET: api/library/enrichment/search?title=...
    /// Searches the configured metadata provider for potential matches.
    /// Used for the manual enrichment override feature.
    /// </summary>
    [HttpGet("enrichment/search")]
    public async Task<IActionResult> SearchEnrichment([FromQuery] string title, [FromQuery] int limit = 10)
    {
        if (string.IsNullOrWhiteSpace(title))
            return BadRequest(new { Message = "Title is required" });

        var results = await _metadataProvider.SearchManyAsync(title, limit);
        return Ok(results);
    }

    /// <summary>
    /// POST: api/library/{id}/enrichment/override
    /// Body: { "providerId": 1234 }
    /// Manually overrides the metadata provider ID for a game and re-enriches it.
    /// </summary>
    [HttpPost("{id}/enrichment/override")]
    public async Task<IActionResult> OverrideEnrichment(int id, [FromBody] OverrideRequest request)
    {
        var game = await _db.UnifiedGames.FirstOrDefaultAsync(g => g.Id == id);
        if (game == null)
            return NotFound(new { Message = $"Game with ID {id} not found." });

        if (!request.ProviderId.HasValue)
            return BadRequest(new { Message = "ProviderId is required." });

        game.MetadataProviderId = request.ProviderId.Value;
        game.MetadataSource = _metadataProvider.ProviderName;
        await _db.SaveChangesAsync();

        // Re-enrich immediately using the forcefully updated provider ID
        var success = await _enrichment.EnrichGameAsync(id, force: true);
        if (!success)
            return StatusCode(500, new { Message = "Failed to enrich game with the new provider ID." });

        var updatedGame = await _db.UnifiedGames
            .Include(g => g.Tags)
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == id);

        return Ok(new
        {
            Message = $"Game '{updatedGame?.Title}' manually matched and enriched.",
            updatedGame?.CoverUrl,
            updatedGame?.Summary,
            updatedGame?.Genres,
            Tags = updatedGame?.Tags.Select(t => t.Name).ToList(),
            updatedGame?.FirstReleaseDate,
            updatedGame?.Rating,
            updatedGame?.MetadataSource,
            updatedGame?.EnrichedAt
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
    ///   2. Moves every physical copy from the source to the target without deduplication
    ///   3. Creates ManualLinks for each moved digital item (so future syncs respect the override)
    ///   4. Deletes the now-empty source game
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
            .Include(g => g.PhysicalCopies)
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

        // Physical copies are independent owned objects. Multiple copies on the same console are
        // valid, so merge always moves every source copy and never deduplicates them.
        foreach (var physicalCopy in source.PhysicalCopies.ToList())
        {
            physicalCopy.UnifiedGameId = target.Id;
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

        var originalGameId = item.UnifiedGame.Id;

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
            OriginalGameId = originalGameId
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

/// <summary>Request body for adding one physical game copy.</summary>
public sealed class AddPhysicalGameCopyRequest
{
    /// <summary>Supported physical console platform. Tenant ownership comes from authentication.</summary>
    public PhysicalGamePlatform Platform { get; set; }

    /// <summary>Optional typed or scanned GTIN. It is normalized and validated server-side.</summary>
    public string? Gtin { get; set; }
}

/// <summary>Confirmed request for the global physical-game capture flow.</summary>
public sealed class CapturePhysicalGameRequest
{
    /// <summary>Existing tenant-owned game ID, mutually exclusive with <see cref="ManualGameTitle"/>.</summary>
    public int? ExistingGameId { get; set; }

    /// <summary>Explicit title for a new manual game, mutually exclusive with <see cref="ExistingGameId"/>.</summary>
    public string? ManualGameTitle { get; set; }

    /// <summary>Supported physical PlayStation platform.</summary>
    public PhysicalGamePlatform Platform { get; set; }

    /// <summary>Optional typed or scanned GTIN. It is normalized and validated server-side.</summary>
    public string? Gtin { get; set; }

    /// <summary>Must be true only after the user has reviewed the final capture summary.</summary>
    public bool Confirmed { get; set; }
}

/// <summary>Request body for the personal tracking update endpoint.</summary>
public class PersonalUpdateRequest
{
    public PlayStatus PlayStatus { get; set; }
    public int? UserRating { get; set; }
    public bool? IsIgnored { get; set; }
}

/// <summary>Request body for replacing one game's custom tags.</summary>
public class CustomTagUpdateRequest
{
    /// <summary>Complete desired set of custom tag names for the game.</summary>
    public IReadOnlyList<string>? Tags { get; set; }
}

/// <summary>Request body for the manual enrichment override endpoint.</summary>
public class OverrideRequest
{
    public int? ProviderId { get; set; }
}
