using GameHub.Core.Data;
using GameHub.Shared;
using Microsoft.EntityFrameworkCore;

namespace GameHub.Core.Services;

/// <summary>
/// Orchestrates game metadata enrichment using a pluggable <see cref="IGameMetadataProvider"/>.
///
/// This service is provider-agnostic — it never references IGDB directly.
/// To swap providers, change the DI registration in Program.cs from IgdbClient
/// to any other IGameMetadataProvider implementation.
///
/// See ADR-003 for the design rationale.
/// </summary>
public class EnrichmentService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IGameMetadataProvider _provider;
    private readonly ILogger<EnrichmentService> _logger;

    public EnrichmentService(
        IServiceScopeFactory scopeFactory,
        IGameMetadataProvider provider,
        ILogger<EnrichmentService> logger)
    {
        _scopeFactory = scopeFactory;
        _provider = provider;
        _logger = logger;
    }

    /// <summary>
    /// Enriches a single game by ID. If the game already has metadata,
    /// it is skipped unless <paramref name="force"/> is true.
    /// </summary>
    /// <returns>True if the game was enriched, false if skipped or not found.</returns>
    public async Task<bool> EnrichGameAsync(int gameId, bool force = false, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreContext>();

        var game = await db.UnifiedGames.FindAsync([gameId], ct);
        if (game == null)
        {
            _logger.LogWarning("Enrichment skipped: game {Id} not found", gameId);
            return false;
        }

        if (game.EnrichedAt != null && !force)
        {
            _logger.LogDebug("Enrichment skipped: game '{Title}' already enriched at {EnrichedAt}",
                game.Title, game.EnrichedAt);
            return false;
        }

        // If we already have a provider ID (from previous enrichment), use it for a direct lookup
        // Otherwise, search by title
        GameMetadata? metadata;
        if (game.MetadataProviderId.HasValue && game.MetadataSource == _provider.ProviderName)
        {
            metadata = await _provider.GetByIdAsync(game.MetadataProviderId.Value, ct);
        }
        else
        {
            metadata = await _provider.SearchAsync(game.Title, ct);
        }

        ApplyMetadata(game, metadata);
        await db.SaveChangesAsync(ct);

        if (metadata != null)
        {
            _logger.LogInformation("✅ Enriched '{Title}' | Provider ID: {ProviderId} | Rating: {Rating} | Genres: {Genres} | Cover: {HasCover}",
                game.Title, metadata.ProviderId, metadata.Rating?.ToString("F1") ?? "None", game.Genres ?? "None", game.CoverUrl != null ? "Yes" : "No");
        }
        else
        {
            _logger.LogInformation("❌ No metadata found for '{Title}'",
                game.Title);
        }

        return true;
    }

    /// <summary>
    /// Enriches a batch of games that have never been enriched (EnrichedAt is null).
    /// Returns the number of games processed in this batch.
    /// </summary>
    public async Task<int> EnrichUnenrichedAsync(int batchSize, CancellationToken ct = default)
    {
        // Load the batch of un-enriched game IDs
        List<int> gameIds;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
            gameIds = await db.UnifiedGames
                .Where(g => g.EnrichedAt == null)
                .OrderBy(g => g.Id)
                .Take(batchSize)
                .Select(g => g.Id)
                .ToListAsync(ct);
        }

        if (gameIds.Count == 0)
            return 0;

        _logger.LogInformation("Enriching batch of {Count} un-enriched games via {Provider}",
            gameIds.Count, _provider.ProviderName);

        int processed = 0;
        foreach (var id in gameIds)
        {
            if (ct.IsCancellationRequested) break;

            await EnrichGameAsync(id, force: false, ct);
            processed++;
        }

        return processed;
    }

    /// <summary>
    /// Returns the count of games that have not yet been enriched.
    /// </summary>
    public async Task<int> GetUnenrichedCountAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
        return await db.UnifiedGames.CountAsync(g => g.EnrichedAt == null, ct);
    }

    /// <summary>
    /// Applies the metadata from a provider response to a UnifiedGame entity.
    /// If metadata is null (no match found), only EnrichedAt is set to avoid re-trying.
    /// </summary>
    private void ApplyMetadata(UnifiedGame game, GameMetadata? metadata)
    {
        game.EnrichedAt = DateTime.UtcNow;

        if (metadata == null)
            return;

        game.CoverUrl = metadata.CoverUrl;
        game.Summary = metadata.Summary;
        game.Genres = metadata.Genres.Count > 0 ? string.Join(", ", metadata.Genres) : null;
        game.FirstReleaseDate = metadata.FirstReleaseDate;
        game.Rating = metadata.Rating;
        game.MetadataProviderId = metadata.ProviderId;
        game.MetadataSource = _provider.ProviderName;
    }
}
