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
            game.EnrichedAt = DateTime.UtcNow;
            if (metadata is not null)
            {
                await GameMetadataApplicator.ApplyAsync(db, game, metadata, _provider.ProviderName, ct);
            }
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
    /// Verifies an explicit provider ID before atomically replacing one tenant-owned game's
    /// provider metadata. Failed lookups never alter the game's existing metadata or tags.
    /// </summary>
    public async Task<DirectEnrichmentResult> ApplyProviderMatchAsync(
        int gameId,
        int providerId,
        CancellationToken ct = default)
    {
        if (providerId <= 0)
        {
            return new DirectEnrichmentResult(DirectEnrichmentStatus.InvalidProviderId);
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
        var game = await db.UnifiedGames
            .Include(candidate => candidate.Tags)
            .FirstOrDefaultAsync(candidate => candidate.Id == gameId, ct);
        if (game is null)
        {
            return new DirectEnrichmentResult(DirectEnrichmentStatus.GameNotFound);
        }

        GameMetadata? metadata;
        try
        {
            metadata = await _provider.GetByIdAsync(providerId, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Direct metadata lookup for game {GameId} through {Provider} is temporarily unavailable",
                gameId,
                _provider.ProviderName);
            await RecordDirectMatchAuditAsync(
                db,
                game.Id,
                providerId,
                "Error",
                "Metadata provider lookup failed.",
                matchedTitle: null,
                ct);
            return new DirectEnrichmentResult(DirectEnrichmentStatus.ProviderUnavailable);
        }

        if (metadata is null)
        {
            await RecordDirectMatchAuditAsync(
                db,
                game.Id,
                providerId,
                "NotFound",
                errorMessage: null,
                matchedTitle: null,
                ct);
            return new DirectEnrichmentResult(DirectEnrichmentStatus.ProviderGameNotFound);
        }

        // A provider-specific ID is the user's linkage authority. A malformed response that
        // identifies a different game must therefore fail closed instead of silently linking it.
        if (metadata.ProviderId != providerId)
        {
            _logger.LogWarning(
                "Direct metadata lookup for game {GameId} through {Provider} returned an unexpected identity",
                gameId,
                _provider.ProviderName);
            await RecordDirectMatchAuditAsync(
                db,
                game.Id,
                providerId,
                "Error",
                "Metadata provider returned an unexpected game identity.",
                matchedTitle: null,
                ct);
            return new DirectEnrichmentResult(DirectEnrichmentStatus.ProviderUnavailable);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await GameMetadataApplicator.ApplyAsync(db, game, metadata, _provider.ProviderName, ct);
        db.EnrichmentAuditLogs.Add(new EnrichmentAuditLog
        {
            UnifiedGameId = game.Id,
            ProviderName = _provider.ProviderName,
            Status = "Success",
            ProviderGameId = metadata.ProviderId,
            MatchedTitle = metadata.MatchedTitle
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        _logger.LogInformation(
            "Applied direct {Provider} metadata match {ProviderId} to game {GameId}",
            _provider.ProviderName,
            providerId,
            gameId);
        return new DirectEnrichmentResult(DirectEnrichmentStatus.Success);
    }

    private async Task RecordDirectMatchAuditAsync(
        CoreContext db,
        int gameId,
        int providerId,
        string status,
        string? errorMessage,
        string? matchedTitle,
        CancellationToken ct)
    {
        db.EnrichmentAuditLogs.Add(new EnrichmentAuditLog
        {
            UnifiedGameId = gameId,
            ProviderName = _provider.ProviderName,
            Status = status,
            ErrorMessage = errorMessage,
            ProviderGameId = providerId,
            MatchedTitle = matchedTitle
        });
        await db.SaveChangesAsync(ct);
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
                .Where(g => g.EnrichedAt == null && !g.IsIgnored)
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

            // Rate-limit: ~3 req/sec to stay safely under IGDB's 4 req/sec limit
            if (processed < gameIds.Count)
                await Task.Delay(300, ct);
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
        return await db.UnifiedGames.CountAsync(g => g.EnrichedAt == null && !g.IsIgnored, ct);
    }

    /// <summary>
    /// Finds games that have been marked as enriched but have no actual metadata
    /// (CoverUrl and Summary are both null). Resets their EnrichedAt to null
    /// so the normal batch enrichment pipeline picks them up again.
    /// Returns the number of games reset.
    /// </summary>
    public async Task<int> ResetStaleEnrichmentsAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreContext>();

        var staleGames = await db.UnifiedGames
            .Where(g => g.EnrichedAt != null && g.CoverUrl == null && g.Summary == null)
            .ToListAsync(ct);

        if (staleGames.Count == 0)
            return 0;

        foreach (var game in staleGames)
        {
            game.EnrichedAt = null;
        }

        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Reset {Count} stale enrichments for re-processing", staleGames.Count);
        return staleGames.Count;
    }

    /// <summary>
    /// Resets EnrichedAt to null for ALL games, so the batch pipeline
    /// re-enriches everything. Useful during development after schema changes.
    /// Returns the number of games reset.
    /// </summary>
    public async Task<int> ResetAllEnrichmentsAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreContext>();

        var enrichedGames = await db.UnifiedGames
            .Where(g => g.EnrichedAt != null)
            .ToListAsync(ct);

        if (enrichedGames.Count == 0)
            return 0;

        foreach (var game in enrichedGames)
        {
            game.EnrichedAt = null;
        }

        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Reset ALL {Count} enrichments for full re-processing", enrichedGames.Count);
        return enrichedGames.Count;
    }

}

/// <summary>Outcomes from a verified direct metadata-provider match.</summary>
public enum DirectEnrichmentStatus
{
    Success,
    InvalidProviderId,
    GameNotFound,
    ProviderGameNotFound,
    ProviderUnavailable
}

/// <summary>Result of applying an explicit provider ID to one tenant-owned game.</summary>
public sealed record DirectEnrichmentResult(DirectEnrichmentStatus Status);
