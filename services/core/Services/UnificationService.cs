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
///   3. For each page, generate a slug from the game title and upsert into UnifiedGame + LibraryItem
///   4. After all pages for a platform are processed, update the sync timestamp
///
/// This replaces the old approach that loaded ALL records from ALL platforms into memory at once.
/// Now memory usage is bounded to a single page (~200 items) regardless of library size.
/// </summary>
public class UnificationService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IngestClient _client;
    private readonly ILogger<UnificationService> _logger;

    /// <summary>Number of items to fetch per page from the Ingest service.</summary>
    private const int PageSize = 200;

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

        // Process each platform independently
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
            // Fetch one page from the Ingest service
            var result = await _client.FetchPageAsync(platform, page, PageSize, lastSyncedAt);

            if (result.Items.Count > 0)
            {
                // Step 3: Process this page — upsert into UnifiedGame + LibraryItem
                processed += await ProcessPageAsync(result.Items, platform);
            }

            // Check if there are more pages to fetch
            hasMore = result.HasMore;
            page++;
        }

        // Step 4: Update the sync timestamp so next run only fetches new/updated records
        if (processed > 0)
        {
            await UpdateSyncTimestampAsync(platform);
        }

        return processed;
    }

    /// <summary>
    /// Processes a single page of RawGameDto items:
    ///   - Generates a URL slug from each game title
    ///   - Looks up existing UnifiedGame records by slug
    ///   - Creates new UnifiedGame records for unknown titles
    ///   - Adds/updates LibraryItem links for the platform
    ///
    /// Uses a fresh DbContext scope so each page is saved independently,
    /// keeping memory usage low.
    /// </summary>
    private async Task<int> ProcessPageAsync(List<RawGameDto> items, Platform platform)
    {
        int processed = 0;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreContext>();

        // Filter out items without a valid name (can't generate a slug from null)
        var validItems = items.Where(x => !string.IsNullOrWhiteSpace(x.Name)).ToList();

        // Pre-calculate all slugs for this page to batch-load existing games
        var slugMap = validItems.ToDictionary(
            x => x,
            x => GenerateSlug(x.Name!));

        var distinctSlugs = slugMap.Values.Distinct().ToHashSet();

        // Load all existing UnifiedGames that match any slug in this page (single DB query)
        var existingGames = await db.UnifiedGames
            .Include(g => g.Items)
            .Where(g => distinctSlugs.Contains(g.Slug))
            .ToDictionaryAsync(g => g.Slug);

        foreach (var item in validItems)
        {
            var slug = slugMap[item];

            // Find or create the UnifiedGame
            if (!existingGames.TryGetValue(slug, out var game))
            {
                game = new UnifiedGame { Title = item.Name!, Slug = slug };
                existingGames[slug] = game; // Track locally to avoid duplicates within the page
                db.UnifiedGames.Add(game);
            }

            // Find or create the platform-specific LibraryItem link
            var link = game.Items.FirstOrDefault(i => i.Platform == platform);
            if (link == null)
            {
                // New platform link for this game
                game.Items.Add(new LibraryItem
                {
                    Platform = platform,
                    ExternalId = item.Id,
                    LastSynced = DateTime.UtcNow
                });
            }
            else
            {
                // Existing link — update the external ID and timestamp
                link.ExternalId = item.Id;
                link.LastSynced = DateTime.UtcNow;
            }

            processed++;
        }

        // Save this page's changes
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
            // First sync for this platform — create the tracking record
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

    /// <summary>
    /// Generates a URL-friendly slug from a game title.
    /// Used as the unique key for matching games across platforms.
    ///
    /// Examples:
    ///   "The Witcher 3: Wild Hunt" → "the-witcher-3-wild-hunt"
    ///   "DOOM (2016)" → "doom-2016"
    /// </summary>
    private static string GenerateSlug(string title)
    {
        // Lowercase for case-insensitive matching
        var s = title.ToLowerInvariant();
        // Remove all characters except letters, numbers, spaces, and hyphens
        s = Regex.Replace(s, @"[^a-z0-9\s-]", "");
        // Collapse whitespace into single hyphens and trim leading/trailing hyphens
        s = Regex.Replace(s, @"\s+", "-").Trim('-');
        return s;
    }
}
