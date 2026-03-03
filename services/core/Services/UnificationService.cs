using GameHub.Core.Data;
using GameHub.Shared;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace GameHub.Core.Services;

/// <summary>
/// Orchestrates the sync process from the Ingest service into the Core unified library.
///
/// How it works:
///   1. For each platform (Steam, PSN, GOG, Epic), check when we last synced
///   2. Fetch only records updated since last sync, one page at a time
///   3. For each page, check ManualLinks first, then fall back to slug-based matching
///   4. After all pages for a platform are processed, update the sync timestamp
///
/// Game matching priority:
///   1. ManualLink — if a manual override exists for (Platform, ExternalId), use it
///   2. Slug match — generate a normalized slug from the title and match by slug
///   3. Create new — if no match found, create a new UnifiedGame
/// </summary>
public class UnificationService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IngestClient _client;
    private readonly ILogger<UnificationService> _logger;

    /// <summary>Number of items to fetch per page from the Ingest service.</summary>
    private const int PageSize = 200;

    /// <summary>
    /// Common edition/variant suffixes that storefronts append to game titles.
    /// These are stripped during slug generation so that different editions
    /// of the same game produce the same slug.
    ///
    /// Order matters — longer phrases should come first to avoid partial matches.
    /// </summary>
    private static readonly string[] EditionSuffixes =
    [
        "game of the year edition",
        "game of the year",
        "definitive edition",
        "complete edition",
        "ultimate edition",
        "premium edition",
        "digital deluxe edition",
        "deluxe edition",
        "enhanced edition",
        "legendary edition",
        "special edition",
        "gold edition",
        "standard edition",
        "directors cut",
        "director's cut",
        "remastered",
        "remake",
        "goty",
    ];

    public UnificationService(IServiceScopeFactory scopeFactory, IngestClient client, ILogger<UnificationService> logger)
    {
        _scopeFactory = scopeFactory;
        _client = client;
        _logger = logger;
    }

    /// <summary>
    /// Runs a full incremental sync across all platforms.
    /// Returns the total number of individual game-platform records processed.
    /// </summary>
    public async Task<int> RunFullSyncAsync()
    {
        int totalProcessed = 0;

        foreach (var platform in Enum.GetValues<Platform>())
        {
            var count = await SyncPlatformAsync(platform);
            totalProcessed += count;
            _logger.LogInformation("Synced {Count} items from {Platform}", count, platform);
        }

        return totalProcessed;
    }

    /// <summary>
    /// Syncs a single platform by:
    ///   1. Looking up when we last synced this platform (from SyncMetadata table)
    ///   2. Fetching pages of data updated since then
    ///   3. Upserting each page into the unified library
    ///   4. Updating the sync timestamp on success
    /// </summary>
    private async Task<int> SyncPlatformAsync(Platform platform)
    {
        int processed = 0;

        // Step 1: Get the last sync timestamp for this platform
        DateTime? lastSyncedAt;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
            var meta = await db.SyncMetadata.FirstOrDefaultAsync(m => m.Platform == platform);
            lastSyncedAt = meta?.LastSyncedAt;
        }

        _logger.LogInformation("Starting sync for {Platform}, last synced at: {LastSynced}",
            platform, lastSyncedAt?.ToString("O") ?? "never");

        // Step 2: Fetch pages until there are no more
        int page = 1;
        bool hasMore = true;

        while (hasMore)
        {
            var result = await _client.FetchPageAsync(platform, page, PageSize, lastSyncedAt);

            if (result.Items.Count > 0)
            {
                // Step 3: Process this page
                processed += await ProcessPageAsync(result.Items, platform);
            }

            hasMore = result.HasMore;
            page++;
        }

        // Step 4: Update the sync timestamp
        if (processed > 0)
        {
            await UpdateSyncTimestampAsync(platform);
        }

        return processed;
    }

    /// <summary>
    /// Processes a single page of RawGameDto items.
    ///
    /// For each item, the matching priority is:
    ///   1. Check ManualLinks — if a manual override exists, link directly to that game
    ///   2. Slug match — generate a normalized slug and find/create a UnifiedGame
    ///
    /// Uses a fresh DbContext scope so each page is saved independently.
    /// </summary>
    private async Task<int> ProcessPageAsync(List<RawGameDto> items, Platform platform)
    {
        int processed = 0;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreContext>();

        // Filter out items without a valid name
        var validItems = items.Where(x => !string.IsNullOrWhiteSpace(x.Name)).ToList();

        // Pre-load all ManualLinks for this platform + these external IDs (single DB query)
        var externalIds = validItems.Select(x => x.Id).ToHashSet();
        var manualLinks = await db.ManualLinks
            .Where(ml => ml.Platform == platform && externalIds.Contains(ml.ExternalId))
            .ToDictionaryAsync(ml => ml.ExternalId);

        // Pre-calculate slugs and batch-load existing UnifiedGames
        var slugMap = validItems.ToDictionary(x => x, x => GenerateSlug(x.Name!));
        var distinctSlugs = slugMap.Values.Distinct().ToHashSet();
        var existingGames = await db.UnifiedGames
            .Include(g => g.Items)
            .Where(g => distinctSlugs.Contains(g.Slug))
            .ToDictionaryAsync(g => g.Slug);

        // Also pre-load any games referenced by ManualLinks (they may have different slugs)
        var manualGameIds = manualLinks.Values.Select(ml => ml.UnifiedGameId).Distinct().ToHashSet();
        var manualGames = await db.UnifiedGames
            .Include(g => g.Items)
            .Where(g => manualGameIds.Contains(g.Id))
            .ToDictionaryAsync(g => g.Id);

        foreach (var item in validItems)
        {
            UnifiedGame game;

            // Priority 1: Check for a manual override
            if (manualLinks.TryGetValue(item.Id, out var link) && manualGames.TryGetValue(link.UnifiedGameId, out var manualGame))
            {
                // Manual override found — use this specific game regardless of title/slug
                game = manualGame;
            }
            else
            {
                // Priority 2: Fall back to slug-based matching
                var slug = slugMap[item];

                if (!existingGames.TryGetValue(slug, out game!))
                {
                    // No match found — create a new UnifiedGame
                    game = new UnifiedGame { Title = item.Name!, Slug = slug };
                    existingGames[slug] = game;
                    db.UnifiedGames.Add(game);
                }
            }

            // Add or update the platform-specific LibraryItem link
            var platformLink = game.Items.FirstOrDefault(i => i.Platform == platform);
            if (platformLink == null)
            {
                game.Items.Add(new LibraryItem
                {
                    Platform = platform,
                    ExternalId = item.Id,
                    LastSynced = DateTime.UtcNow
                });
            }
            else
            {
                platformLink.ExternalId = item.Id;
                platformLink.LastSynced = DateTime.UtcNow;
            }

            processed++;
        }

        await db.SaveChangesAsync();
        return processed;
    }

    /// <summary>
    /// Updates the SyncMetadata record for a platform with the current timestamp.
    /// Creates the record if this is the first sync for this platform.
    /// </summary>
    private async Task UpdateSyncTimestampAsync(Platform platform)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreContext>();

        var meta = await db.SyncMetadata.FirstOrDefaultAsync(m => m.Platform == platform);
        if (meta == null)
        {
            db.SyncMetadata.Add(new SyncMetadata
            {
                Platform = platform,
                LastSyncedAt = DateTime.UtcNow
            });
        }
        else
        {
            meta.LastSyncedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();
    }

    // =========================================================================
    // Slug Generation
    // =========================================================================

    /// <summary>
    /// Generates a URL-friendly slug from a game title, with normalization
    /// to handle common title variations across storefronts.
    ///
    /// Pipeline:
    ///   1. NormalizeTitle — strip edition suffixes, symbols, etc.
    ///   2. Lowercase, remove special characters, collapse whitespace to hyphens
    ///
    /// Examples:
    ///   "The Witcher 3: Wild Hunt"                        → "the-witcher-3-wild-hunt"
    ///   "The Witcher 3: Wild Hunt - GOTY Edition"         → "the-witcher-3-wild-hunt"
    ///   "The Witcher 3: Wild Hunt - Game of the Year"     → "the-witcher-3-wild-hunt"
    ///   "DOOM® (2016)"                                    → "doom-2016"
    ///   "Assassin's Creed™ Valhalla - Deluxe Edition"     → "assassins-creed-valhalla"
    /// </summary>
    internal static string GenerateSlug(string title)
    {
        // Step 1: Normalize the title by stripping common variations
        var normalized = NormalizeTitle(title);

        // Step 2: Lowercase for case-insensitive matching
        var s = normalized.ToLowerInvariant();

        // Step 3: Remove all characters except letters, numbers, spaces, and hyphens
        s = Regex.Replace(s, @"[^a-z0-9\s-]", "");

        // Step 4: Collapse whitespace into single hyphens and trim
        s = Regex.Replace(s, @"[\s-]+", "-").Trim('-');

        return s;
    }

    /// <summary>
    /// Normalizes a game title by removing common noise that differs between storefronts:
    ///   - Trademark/copyright symbols (™, ®, ©)
    ///   - Edition suffixes ("Game of the Year Edition", "Deluxe Edition", etc.)
    ///   - Trailing separators left after suffix removal ("- ", " : ")
    ///
    /// This is the key improvement over the old slug generation — titles like
    /// "The Witcher 3: Wild Hunt - GOTY Edition" and "The Witcher 3: Wild Hunt"
    /// will now produce the same normalized output.
    /// </summary>
    internal static string NormalizeTitle(string title)
    {
        // Remove trademark and copyright symbols
        var result = title
            .Replace("™", "")
            .Replace("®", "")
            .Replace("©", "");

        // Remove common edition suffixes (case-insensitive)
        var lower = result.ToLowerInvariant();
        foreach (var suffix in EditionSuffixes)
        {
            var idx = lower.IndexOf(suffix, StringComparison.Ordinal);
            if (idx >= 0)
            {
                // Remove the suffix and everything after it
                result = result[..idx];
                lower = result.ToLowerInvariant();
            }
        }

        // Clean up trailing separators that may be left after suffix removal
        // e.g. "The Witcher 3: Wild Hunt - " → "The Witcher 3: Wild Hunt"
        result = Regex.Replace(result, @"[\s]*[-–—:]+[\s]*$", "");

        return result.Trim();
    }
}
