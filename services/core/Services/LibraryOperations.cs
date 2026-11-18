using GameHub.Core.Data;
using GameHub.Shared;
using Microsoft.EntityFrameworkCore;

namespace GameHub.Core.Services;

/// <summary>Library operations scoped to the tenant selected by CoreContext.</summary>
public sealed class LibraryOperations(CoreContext db, ILogger logger)
{
    private const int DefaultLibraryPageSize = 50;
    private const int MaxLibraryPageSize = 500;
    private readonly CoreContext _db = db;

    /// <summary>Searches and pages the current tenant's unified library.</summary>
    public async Task<PagedResult<object>> SearchAsync(
        string? search,
        string[]? tags,
        Platform[]? platforms,
        PhysicalGamePlatform[]? physicalPlatforms,
        PlayStatus[]? playStatus,
        string[]? developer,
        string[]? publisher,
        string? sortBy,
        string sortOrder,
        bool includeIgnored,
        int page,
        int pageSize, CancellationToken cancellationToken)
    {
        // Normalize caller-controlled pagination before it reaches Skip/Take. The frontend offers
        // 25/50/100, while the broader API contract retains its existing maximum for MCP clients.
        if (pageSize < 1) pageSize = DefaultLibraryPageSize;
        if (pageSize > MaxLibraryPageSize) pageSize = MaxLibraryPageSize;
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
        var totalCount = await query.CountAsync(cancellationToken);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        if (page > totalPages) page = totalPages;

        // Dynamic sorting — default to title ascending
        var sortedQuery = (sortBy?.ToLowerInvariant(), sortOrder.ToLowerInvariant()) switch
        {
            ("rating", "desc")       => query.OrderByDescending(g => g.Rating ?? -1),
            ("rating", _)            => query.OrderBy(g => g.Rating ?? 999),
            ("releasedate", "desc")  => query.OrderByDescending(g => g.FirstReleaseDate ?? DateTime.MinValue),
            ("releasedate", _)       => query.OrderBy(g => g.FirstReleaseDate ?? DateTime.MaxValue),
            ("title", "desc")        => query.OrderByDescending(g => g.Title),
            _                        => query.OrderBy(g => g.Title), // default: title asc
        };

        // A unique tie-breaker keeps equal titles, ratings, and release dates on stable pages.
        var games = await sortedQuery
            .ThenBy(g => g.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

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

        return new PagedResult<object>
        {
            Items = result.Cast<object>().ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    /// <summary>Selects one matching game ID from the current tenant.</summary>
    public async Task<int?> GetRandomIdAsync(
        string? search,
        string[]? tags,
        Platform[]? platforms,
        PhysicalGamePlatform[]? physicalPlatforms,
        PlayStatus[]? playStatus,
        string[]? developer,
        string[]? publisher,
        bool includeIgnored, CancellationToken cancellationToken)
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
            .OrderBy(g => EF.Functions.Random())
            .Select(g => g.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return randomGameId == 0 ? null : randomGameId;
    }

    /// <summary>Returns the current tenant's full detail payload for a game.</summary>
    public async Task<object?> GetGameAsync(int id, CancellationToken cancellationToken)
    {
        var game = await _db.UnifiedGames
            .Include(g => g.Items)
            .Include(g => g.PhysicalCopies)
            .Include(g => g.Tags)
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == id, cancellationToken);

        if (game == null)
            return null;

        var customTags = await _db.Set<TagAssignment>()
            .AsNoTracking()
            .Where(assignment => assignment.UnifiedGameId == id && assignment.IsCustom)
            .Select(assignment => assignment.Tag!.Name)
            .OrderBy(name => name)
            .ToListAsync(cancellationToken);

        List<string> SplitCsv(string? csv) =>
            string.IsNullOrWhiteSpace(csv) ? new List<string>() : csv.Split(", ").ToList();

        return new
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
    }

    /// <summary>Returns the current tenant's tag names.</summary>
    public Task<List<string>> GetTagsAsync(CancellationToken cancellationToken) =>
        _db.Tags.Select(tag => tag.Name).OrderBy(name => name).ToListAsync(cancellationToken);

    /// <summary>Calculates statistics for visible games owned by the current tenant.</summary>
    public async Task<object> GetStatsAsync(CancellationToken cancellationToken)
    {
        // --- Summary counts ---
        var totalGames = await _db.UnifiedGames.CountAsync(g => !g.IsIgnored, cancellationToken);
        var enrichedCount = await _db.UnifiedGames.CountAsync(g => g.EnrichedAt != null && !g.IsIgnored, cancellationToken);
        var totalPlatformLinks = await _db.LibraryItems.CountAsync(i => i.UnifiedGame != null && !i.UnifiedGame.IsIgnored, cancellationToken);

        // Games that appear on more than one platform
        var multiPlatformCount = await _db.UnifiedGames
            .Where(g => g.Items.Count > 1 && !g.IsIgnored)
            .CountAsync(cancellationToken);

        // Average rating (only games that have one)
        var averageRating = await _db.UnifiedGames
            .Where(g => g.Rating != null && !g.IsIgnored)
            .AverageAsync(g => (double?)g.Rating, cancellationToken) ?? 0;

        // --- Per-platform breakdown ---
        var platformCounts = await _db.LibraryItems
            .Where(i => i.UnifiedGame != null && !i.UnifiedGame.IsIgnored)
            .GroupBy(i => i.Platform)
            .Select(g => new { Platform = g.Key.ToString(), Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToListAsync(cancellationToken);

        // --- Genre distribution (parse comma-separated Genres field) ---
        var rawGenres = await _db.UnifiedGames
            .Where(g => g.Genres != null && g.Genres != "" && !g.IsIgnored)
            .Select(g => g.Genres!)
            .ToListAsync(cancellationToken);

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
            .ToListAsync(cancellationToken);

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
            .ToListAsync(cancellationToken);

        // --- Play status distribution ---
        var playStatusCounts = await _db.UnifiedGames
            .Where(g => !g.IsIgnored)
            .GroupBy(g => g.PlayStatus)
            .Select(g => new { Status = g.Key.ToString(), Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToListAsync(cancellationToken);

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
            .ToListAsync(cancellationToken);

        return new
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
        };
    }

    /// <summary>Applies the existing library tracking update to a game owned by the current tenant.</summary>
    public Task<PersonalTrackingResult?> UpdateTrackingAsync(int id, PlayStatus status, int? rating,
        bool? isIgnored, CancellationToken cancellationToken) =>
        UpdateTrackingAsync(id, status, rating, updateRating: true, isIgnored, cancellationToken);

    internal Task<PersonalTrackingResult?> UpdatePlayStatusAsync(int id, PlayStatus status,
        CancellationToken cancellationToken) =>
        UpdateTrackingAsync(id, status, rating: null, updateRating: false, isIgnored: null, cancellationToken);

    internal Task<PersonalTrackingResult?> UpdateRatingAsync(int id, int? rating, CancellationToken cancellationToken) =>
        UpdateTrackingAsync(id, status: null, rating, updateRating: true, isIgnored: null, cancellationToken);

    private async Task<PersonalTrackingResult?> UpdateTrackingAsync(int id, PlayStatus? status, int? rating,
        bool updateRating, bool? isIgnored, CancellationToken cancellationToken)
    {
        var game = await _db.UnifiedGames.FirstOrDefaultAsync(g => g.Id == id, cancellationToken);
        if (game is null) return null;

        if (status.HasValue) game.PlayStatus = status.Value;
        if (updateRating) game.UserRating = rating;
        if (isIgnored.HasValue) game.IsIgnored = isIgnored.Value;
        game.IsPlayStatusManual = true;
        await _db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Updated personal tracking for game {Id}: Status={Status}, Rating={Rating}, IsIgnored={IsIgnored}",
            id, game.PlayStatus, game.UserRating?.ToString() ?? "none", game.IsIgnored);
        return new("Personal tracking updated successfully.", game.PlayStatus, game.UserRating);
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
}

/// <summary>The personal tracking fields returned after a successful update.</summary>
public sealed record PersonalTrackingResult(string Message, PlayStatus PlayStatus, int? UserRating);
