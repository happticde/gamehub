using GameHub.Core.Data;
using GameHub.Shared;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;

namespace GameHub.Core.Services;

public class UnificationService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IngestClient _client;
    private readonly ILogger<UnificationService> _logger;

    public UnificationService(IServiceScopeFactory scopeFactory, IngestClient client, ILogger<UnificationService> logger)
    {
        _scopeFactory = scopeFactory;
        _client = client;
        _logger = logger;
    }

    public async Task<int> RunFullSyncAsync()
    {
        var epic = await _client.FetchEpicAsync();
        var gog = await _client.FetchGogAsync();
        var psn = await _client.FetchPsnAsync();
        var steam = await _client.FetchSteamAsync();

        // Combine them to know what we are dealing with
        var allRaw = new List<(string Title, Platform Plat, string Id)>();

        // Filter '.Where(x => x.Name != null)' BEFORE selecting into the tuple
        // The '!' tells the compiler "I know Name is not null here"        
        allRaw.AddRange(epic
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Select(x => (x.Name!, Platform.Epic, x.Id)));
        allRaw.AddRange(gog
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Select(x => (x.Name!, Platform.Gog, x.Id)));
        allRaw.AddRange(psn
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Select(x => (x.Name!, Platform.Psn, x.Id)));
        allRaw.AddRange(steam
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Select(x => (x.Name!, Platform.Steam, x.Id)));

        // Filter valid titles
        allRaw = allRaw.Where(x => !string.IsNullOrWhiteSpace(x.Title)).ToList();

        // Pre-calculate slugs
        var incomingSlugs = allRaw.Select(x => GenerateSlug(x.Title)).Distinct().ToHashSet();

        int processed = 0;
        int batchSize = 500;

        for (int i = 0; i < incomingSlugs.Count; i += batchSize)
        {
            using (var scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<CoreContext>();

                var slugBatch = incomingSlugs.Skip(i).Take(batchSize).ToHashSet();

                // Load EXISTING games for this batch (Tracked)
                var existingGames = await db.UnifiedGames
                    .Include(g => g.Items)
                    .Where(g => slugBatch.Contains(g.Slug))
                    .ToDictionaryAsync(g => g.Slug);

                // Find raw items belonging to this batch
                var rawBatch = allRaw.Where(r => slugBatch.Contains(GenerateSlug(r.Title)));

                foreach (var (title, platform, extId) in rawBatch)
                {
                    var slug = GenerateSlug(title);

                    if (!existingGames.TryGetValue(slug, out var game))
                    {
                        game = new UnifiedGame { Title = title, Slug = slug };
                        existingGames[slug] = game; // Track locally
                        db.UnifiedGames.Add(game);
                    }

                    var link = game.Items.FirstOrDefault(i => i.Platform == platform);
                    if (link == null)
                    {
                        game.Items.Add(new LibraryItem { Platform = platform, ExternalId = extId });
                    }
                    else
                    {
                        link.LastSynced = DateTime.UtcNow;
                        link.ExternalId = extId;
                    }
                    processed++;
                }

                // Save every batch! This releases locks and clears memory pressure (partially).
                await db.SaveChangesAsync();
            }
        }
        return processed;
    }
    private static string GenerateSlug(string title)
    {
        // Lowercase
        var s = title.ToLowerInvariant();
        // Remove invalid chars (keep a-z, 0-9, space, hyphen)
        s = Regex.Replace(s, @"[^a-z0-9\s-]", "");
        // Collapse spaces to hyphens
        s = Regex.Replace(s, @"\s+", "-").Trim('-');
        return s;
    }
}
