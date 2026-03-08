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

        var game = await db.UnifiedGames
            .Include(g => g.Tags)
            .FirstOrDefaultAsync(g => g.Id == gameId, ct);

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

        // Determine whether to use direct ID lookup or search
        bool isReEnrichment = game.MetadataProviderId.HasValue && game.MetadataSource == _provider.ProviderName;

        GameMetadata? metadata = null;
        string auditStatus = "Success";
        string? errorMessage = null;

        try
        {
            if (isReEnrichment)
                metadata = await _provider.GetByIdAsync(game.MetadataProviderId!.Value, ct);
            else
                metadata = await _provider.SearchAsync(game.Title, ct);

            if (metadata == null)
                auditStatus = "NotFound";
        }
        catch (Exception ex)
        {
            auditStatus = "Error";
            errorMessage = ex.Message;
            _logger.LogError(ex, "Failed to enrich '{Title}' via {Provider}", game.Title, _provider.ProviderName);
        }

        // Apply metadata (or just update EnrichedAt if NotFound)
        // Skip updating EnrichedAt on Error so the background service retries it later
        if (auditStatus != "Error")
        {
            await ApplyMetadataAsync(db, game, metadata);
        }

        // Record the audit log
        var auditLog = new EnrichmentAuditLog
        {
            UnifiedGameId = game.Id,
            ProviderName = _provider.ProviderName,
            Status = auditStatus,
            ErrorMessage = errorMessage,
            ProviderGameId = metadata?.ProviderId,
            MatchedTitle = metadata?.MatchedTitle
        };
        db.EnrichmentAuditLogs.Add(auditLog);

        await db.SaveChangesAsync(ct);

        if (auditStatus == "Success")
        {
            var tagNames = string.Join(", ", game.Tags.Select(t => t.Name));
            _logger.LogInformation("✅ Enriched '{Title}' | Provider ID: {ProviderId} | Rating: {Rating} | Genres: {Genres} | Tags: {Tags} | Cover: {HasCover}",
                game.Title, metadata!.ProviderId, metadata.Rating?.ToString("F1") ?? "None", game.Genres ?? "None", string.IsNullOrEmpty(tagNames) ? "None" : tagNames, game.CoverUrl != null ? "Yes" : "No");
        }
        else if (auditStatus == "NotFound")
        {
            _logger.LogInformation("❌ No metadata found for '{Title}'", game.Title);
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
    private async Task ApplyMetadataAsync(CoreContext db, UnifiedGame game, GameMetadata? metadata)
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

        // Map genres to Tag entities
        game.Tags.Clear();
        foreach (var genre in metadata.Genres)
        {
            if (string.IsNullOrWhiteSpace(genre)) continue;

            // Trim and normalize spacing
            var normalizedGenre = genre.Trim();

            // Find existing tag or create a new one
            var tag = await db.Tags.FirstOrDefaultAsync(t => t.Name.ToLower() == normalizedGenre.ToLower());
            if (tag == null)
            {
                tag = new Tag { Name = normalizedGenre };
                db.Tags.Add(tag);
                // Optionally save here if you want to ensure the ID is generated immediately, 
                // but adding it to the tracked game.Tags collection is usually sufficient.
            }
            
            game.Tags.Add(tag);
        }
    }
}
