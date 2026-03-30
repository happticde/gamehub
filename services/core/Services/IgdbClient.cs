using GameHub.Shared;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;

namespace GameHub.Core.Services;

/// <summary>
/// IGDB API v4 client implementing <see cref="IGameMetadataProvider"/>.
///
/// Authentication flow:
///   1. POST to Twitch OAuth2 with client credentials → access token
///   2. Cache token until it expires
///   3. All IGDB requests include Client-ID header + Bearer token
///
/// IGDB uses a POST-based query language (similar to GraphQL).
/// See https://api-docs.igdb.com/ for full documentation.
///
/// Rate limits: ~4 requests/second for non-commercial use.
/// </summary>
public class IgdbClient : IGameMetadataProvider
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<IgdbClient> _logger;
    private readonly IMemoryCache _cache;

    private string? _accessToken;
    private DateTime _tokenExpiresAt = DateTime.MinValue;

    private const string TwitchTokenUrl = "https://id.twitch.tv/oauth2/token";
    private const string IgdbBaseUrl = "https://api.igdb.com/v4";

    /// <summary>Fields to request from IGDB for game enrichment.</summary>
    private const string GameFields = "name,cover.url,summary,genres.name,first_release_date,aggregated_rating,artworks.url,screenshots.url,platforms.name,involved_companies.company.name,involved_companies.developer,involved_companies.publisher";

    public string ProviderName => "IGDB";

    public IgdbClient(HttpClient http, IConfiguration config, ILogger<IgdbClient> logger, IMemoryCache cache)
    {
        _http = http;
        _config = config;
        _logger = logger;
        _cache = cache;
    }

    /// <summary>
    /// Patterns in game titles that indicate non-game items (tools, servers, soundtracks).
    /// These are skipped entirely during enrichment since IGDB won't have entries for them.
    /// Matched case-insensitively against the full title.
    /// </summary>
    private static readonly string[] NonGamePatterns =
    [
        "dedicated server",
        "soundtrack",
        "resource archiver",
        "pre-game editor",
        "modding tool",
        "goodie pack",
    ];

    /// <summary>
    /// Patterns that indicate non-game items when they appear as the LAST word(s) in a title.
    /// These are more aggressive filters only matched at the end to avoid false positives
    /// (e.g. "Tomb Raider Level Editor" is a tool, but "Editor's Choice" is not).
    /// </summary>
    private static readonly string[] NonGameEndPatterns =
    [
        "editor",
        "sdk",
        "tool",
        "content",
    ];

    /// <summary>
    /// Suffixes that indicate test/beta variants — stripped before searching
    /// but the game itself may still be findable on IGDB.
    /// </summary>
    private static readonly string[] TestVariantSuffixes =
    [
        "tech beta",
        "beta",
        "experimental",
        "legacy mode",
        "ptr",
        "test branch",
        "early access",
    ];

    /// <summary>
    /// Edition suffixes stripped before searching IGDB (same list as UnificationService).
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
        "standard edition",
        "gold edition",
        "directors cut",
        "director's cut",
        "remastered",
        "remake",
        "classic",
        "goty",
    ];

    /// <inheritdoc />
    public async Task<GameMetadata?> SearchAsync(string title, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var lower = title.ToLowerInvariant().Trim();

        // Skip non-game items entirely (servers, soundtracks, tools, editors)
        if (NonGamePatterns.Any(p => lower.Contains(p)))
        {
            _logger.LogDebug("Skipping non-game item: '{Title}'", title);
            return null;
        }

        // Check end-of-title patterns ("editor", "tool", "content", "sdk")
        if (NonGameEndPatterns.Any(p => lower.EndsWith(p)))
        {
            _logger.LogDebug("Skipping non-game item (end pattern): '{Title}'", title);
            return null;
        }

        // Generate candidates for fallback search
        var candidates = GetSearchCandidates(title, _logger);

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || candidate.Length < 2)
                continue;

            _logger.LogDebug("Searching IGDB for candidate: '{Candidate}'", candidate);
            var result = await SearchIgdbBestMatchAsync(candidate, ct);
            if (result != null)
                return result;

            // Optional last-resort fallback: Strip everything after the first separator (- or :)
            // "Warhammer 40,000: Mechanicus" → "Warhammer 40,000"
            var separatorIdx = candidate.IndexOfAny(['-', ':']);
            if (separatorIdx > 3) // Ensure there's a meaningful base name (at least 3 chars)
            {
                var baseName = candidate[..separatorIdx].Trim();
                _logger.LogDebug("Searching IGDB for base name fallback: '{BaseName}'", baseName);
                result = await SearchIgdbBestMatchAsync(baseName, ct);
                if (result != null)
                    return result;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<List<GameMetadata>> SearchManyAsync(string title, int limit = 10, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            return [];

        string query;
        var cleaned = title.Trim();
        
        // If the query is purely numeric, perform a direct GET by ID instead of a text search
        if (int.TryParse(cleaned, out int directId))
        {
            query = $"where id = {directId}; fields {GameFields}; limit 1;";
        }
        else
        {
            var escaped = cleaned.Replace("\"", "\\\"");
            query = $"search \"{escaped}\"; fields {GameFields}; limit {limit};";
        }

        var results = await QueryGamesAsync(query, ct);

        return results.Select(MapToMetadata).ToList();
    }

    /// <summary>
    /// Searches IGDB with limit 5, then picks the result whose name is the
    /// closest match to the search title. This avoids cases where IGDB's
    /// default top result is a different game with a similar name.
    /// </summary>
    private async Task<GameMetadata?> SearchIgdbBestMatchAsync(string title, CancellationToken ct)
    {
        var escaped = title.Replace("\"", "\\\"");
        var query = $"search \"{escaped}\"; fields {GameFields}; limit 5;";

        var results = await QueryGamesAsync(query, ct);
        if (results.Count == 0)
            return null;

        // Pick the result with the closest name match
        var searchLower = title.ToLowerInvariant();
        var best = results
            .OrderByDescending(r => CalculateTitleSimilarity(searchLower, r.Name?.ToLowerInvariant() ?? ""))
            .First();

        return MapToMetadata(best);
    }

    /// <summary>
    /// Calculates a simple word-overlap similarity score between two titles.
    /// Returns a value between 0.0 (no shared words) and 1.0 (identical words).
    /// Also gives a bonus for exact substring containment.
    /// </summary>
    private static double CalculateTitleSimilarity(string search, string candidate)
    {
        if (search == candidate) return 1.0;

        var searchWords = search.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var candidateWords = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

        if (searchWords.Count == 0 || candidateWords.Count == 0) return 0;

        var intersection = searchWords.Intersect(candidateWords).Count();
        var union = searchWords.Union(candidateWords).Count();

        double jaccard = (double)intersection / union;

        // Bonus if one contains the other as a substring
        if (search.Contains(candidate) || candidate.Contains(search))
            jaccard += 0.3;

        return jaccard;
    }

    /// <summary>
    /// Generates a list of title candidates for IGDB search, from most specific to most general.
    /// Used as sequential fallbacks to improve matching stability.
    /// </summary>
    private static List<string> GetSearchCandidates(string title, ILogger? logger = null)
    {
        var candidates = new List<string> { title.Trim() };

        // 1. Strip trademark symbols
        var current = title.Replace("™", "").Replace("®", "").Replace("©", "").Trim();
        if (!candidates.Contains(current)) candidates.Add(current);

        // 2. Remove parenthesized content: "(Test branch)", "(1992)", "(Early Access)"
        current = System.Text.RegularExpressions.Regex.Replace(current, @"\([^)]*\)", "").Trim();
        if (!candidates.Contains(current)) candidates.Add(current);

        // 3. Remove edition and beta suffixes
        var lower = current.ToLowerInvariant();
        bool changed = false;
        foreach (var suffix in EditionSuffixes.Concat(TestVariantSuffixes))
        {
            var idx = lower.IndexOf(suffix, StringComparison.Ordinal);
            if (idx >= 0)
            {
                current = current[..idx].Trim();
                lower = current.ToLowerInvariant();
                changed = true;
            }
        }
        if (changed && !candidates.Contains(current)) candidates.Add(current);

        // 4. Clean trailing separators and collapse spaces
        current = System.Text.RegularExpressions.Regex.Replace(current, @"[\s]*[-–—:]+[\s]*$", "");
        current = System.Text.RegularExpressions.Regex.Replace(current, @"\s+", " ").Trim();
        if (!candidates.Contains(current)) candidates.Add(current);

        // 5. Final aggressive sanitization (CamelCase split)
        var aggressive = System.Text.RegularExpressions.Regex.Replace(current, @"([a-z])([A-Z])", "$1 $2");
        aggressive = System.Text.RegularExpressions.Regex.Replace(aggressive, @"([a-zA-Z])(\d)", "$1 $2");
        aggressive = System.Text.RegularExpressions.Regex.Replace(aggressive, @"(\d)([a-zA-Z])", "$1 $2");
        aggressive = System.Text.RegularExpressions.Regex.Replace(aggressive, @"\s+", " ").Trim();
        if (!candidates.Contains(aggressive)) candidates.Add(aggressive);

        logger?.LogDebug("Generated {Count} search candidates: {Candidates}",
            candidates.Count, string.Join(" | ", candidates));

        return candidates;
    }

    /// <inheritdoc />
    public async Task<GameMetadata?> GetByIdAsync(int providerId, CancellationToken ct = default)
    {
        var query = $"where id = {providerId}; fields {GameFields}; limit 1;";

        var results = await QueryGamesAsync(query, ct);
        return results.Count > 0 ? MapToMetadata(results[0]) : null;
    }

    /// <summary>
    /// Sends a query to the IGDB /games endpoint and returns the raw response objects.
    /// Handles authentication automatically and caches responses to avoid rate limits.
    /// Retries once with a fresh token on 401 Unauthorized.
    /// </summary>
    private async Task<List<IgdbGame>> QueryGamesAsync(string query, CancellationToken ct)
    {
        var cacheKey = $"igdb_query_{Convert.ToBase64String(Encoding.UTF8.GetBytes(query))}";

        if (_cache.TryGetValue(cacheKey, out List<IgdbGame>? cachedResult) && cachedResult != null)
        {
            _logger.LogDebug("IGDB cache hit for query: {Query}", query);
            return cachedResult;
        }

        // Try up to 2 times: first with the current token, then with a fresh one on 401
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await EnsureAuthenticatedAsync(ct);

            var clientId = _config["Igdb:ClientId"] ?? "";
            var request = new HttpRequestMessage(HttpMethod.Post, $"{IgdbBaseUrl}/games")
            {
                Content = new StringContent(query, Encoding.UTF8, "text/plain")
            };
            request.Headers.Add("Client-ID", clientId);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

            var response = await _http.SendAsync(request, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && attempt == 0)
            {
                _logger.LogWarning("IGDB returned 401 — forcing token refresh and retrying");
                _accessToken = null; // Force re-auth on next loop iteration
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("IGDB API returned {StatusCode}: {Body}", response.StatusCode, body);
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<List<IgdbGame>>(json, JsonOptions) ?? [];

            // Cache non-empty results for 24 hours; skip caching empty results
            // so the game can be retried on the next enrichment run
            if (result.Count > 0)
                _cache.Set(cacheKey, result, TimeSpan.FromHours(24));

            return result;
        }

        return [];
    }

    /// <summary>
    /// Ensures a valid OAuth2 access token is available, requesting a new one if needed.
    /// Tokens are cached until 60 seconds before their expiry to avoid edge-case failures.
    /// </summary>
    internal async Task EnsureAuthenticatedAsync(CancellationToken ct)
    {
        // Reuse token if still valid (with 60-second buffer)
        if (_accessToken != null && DateTime.UtcNow < _tokenExpiresAt.AddSeconds(-60))
            return;

        var clientId = _config["Igdb:ClientId"];
        var clientSecret = _config["Igdb:ClientSecret"];

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            _logger.LogWarning("IGDB credentials not configured — enrichment will return no results");
            return;
        }

        var tokenUrl = $"{TwitchTokenUrl}?client_id={clientId}&client_secret={clientSecret}&grant_type=client_credentials";
        var response = await _http.PostAsync(tokenUrl, null, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Failed to get Twitch OAuth token: {StatusCode} {Body}", response.StatusCode, body);
            return;
        }

        var tokenResponse = await response.Content.ReadFromJsonAsync<TwitchTokenResponse>(ct);
        if (tokenResponse != null)
        {
            _accessToken = tokenResponse.AccessToken;
            _tokenExpiresAt = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn);
            _logger.LogInformation("IGDB authentication successful, token expires in {Hours}h",
                tokenResponse.ExpiresIn / 3600);
        }
    }

    /// <summary>Maps an IGDB API response object to the provider-agnostic GameMetadata record.</summary>
    private static GameMetadata MapToMetadata(IgdbGame game)
    {
        string? SanitizeImageUrl(string? url, string size = "t_1080p")
        {
            if (url == null) return null;
            var mapped = url.Replace("t_thumb", size);
            if (mapped.StartsWith("//")) mapped = "https:" + mapped;
            return mapped;
        }

        var coverUrl = SanitizeImageUrl(game.Cover?.Url, "t_cover_big");

        DateTime? releaseDate = null;
        if (game.FirstReleaseDate.HasValue)
        {
            releaseDate = DateTimeOffset.FromUnixTimeSeconds(game.FirstReleaseDate.Value).UtcDateTime;
        }

        var genres = game.Genres?.Select(g => g.Name).Where(n => n != null).ToList() ?? [];
        var platforms = game.Platforms?.Select(p => p.Name).Where(n => n != null).Cast<string>().ToList();
        var artworks = game.Artworks?.Select(a => SanitizeImageUrl(a.Url)).Where(u => u != null).Cast<string>().ToList();
        var screenshots = game.Screenshots?.Select(s => SanitizeImageUrl(s.Url)).Where(u => u != null).Cast<string>().ToList();

        var developers = game.InvolvedCompanies?
            .Where(c => c.Developer == true && c.Company?.Name != null)
            .Select(c => c.Company!.Name!)
            .ToList();

        var publishers = game.InvolvedCompanies?
            .Where(c => c.Publisher == true && c.Company?.Name != null)
            .Select(c => c.Company!.Name!)
            .ToList();

        return new GameMetadata(
            ProviderId: game.Id,
            CoverUrl: coverUrl,
            Summary: game.Summary,
            Genres: genres!,
            FirstReleaseDate: releaseDate,
            Rating: game.AggregatedRating,
            MatchedTitle: game.Name,
            Platforms: platforms,
            Artworks: artworks,
            Screenshots: screenshots,
            Developers: developers,
            Publishers: publishers
        );
    }

    // =========================================================================
    // IGDB Response DTOs (internal, not part of the public contract)
    // =========================================================================

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    internal class IgdbGame
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("cover")]
        public IgdbCover? Cover { get; set; }

        [JsonPropertyName("summary")]
        public string? Summary { get; set; }

        [JsonPropertyName("genres")]
        public List<IgdbGenre>? Genres { get; set; }

        [JsonPropertyName("platforms")]
        public List<IgdbPlatform>? Platforms { get; set; }

        [JsonPropertyName("artworks")]
        public List<IgdbImage>? Artworks { get; set; }

        [JsonPropertyName("screenshots")]
        public List<IgdbImage>? Screenshots { get; set; }

        [JsonPropertyName("involved_companies")]
        public List<IgdbInvolvedCompany>? InvolvedCompanies { get; set; }

        [JsonPropertyName("first_release_date")]
        public long? FirstReleaseDate { get; set; }

        [JsonPropertyName("aggregated_rating")]
        public double? AggregatedRating { get; set; }
    }

    internal class IgdbCover
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }

    internal class IgdbImage
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }

    internal class IgdbGenre
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    internal class IgdbPlatform
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    internal class IgdbInvolvedCompany
    {
        [JsonPropertyName("company")]
        public IgdbCompany? Company { get; set; }

        [JsonPropertyName("developer")]
        public bool? Developer { get; set; }

        [JsonPropertyName("publisher")]
        public bool? Publisher { get; set; }
    }

    internal class IgdbCompany
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private class TwitchTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = "";

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }
}
