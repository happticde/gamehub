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

    /// <inheritdoc />
    public async Task<GameMetadata?> SearchAsync(string title, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        // Escape double quotes in the title for the IGDB query
        var escaped = title.Replace("\"", "\\\"");
        var query = $"search \"{escaped}\"; fields {GameFields}; limit 1;";

        var results = await QueryGamesAsync(query, ct);
        return results.Count > 0 ? MapToMetadata(results[0]) : null;
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
    /// </summary>
    private async Task<List<IgdbGame>> QueryGamesAsync(string query, CancellationToken ct)
    {
        var cacheKey = $"igdb_query_{Convert.ToBase64String(Encoding.UTF8.GetBytes(query))}";
        
        if (_cache.TryGetValue(cacheKey, out List<IgdbGame>? cachedResult) && cachedResult != null)
        {
            _logger.LogDebug("IGDB cache hit for query: {Query}", query);
            return cachedResult;
        }

        await EnsureAuthenticatedAsync(ct);

        var clientId = _config["Igdb:ClientId"] ?? "";
        var request = new HttpRequestMessage(HttpMethod.Post, $"{IgdbBaseUrl}/games")
        {
            Content = new StringContent(query, Encoding.UTF8, "text/plain")
        };
        request.Headers.Add("Client-ID", clientId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        var response = await _http.SendAsync(request, ct);

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
