using GameHub.Shared;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    private string? _accessToken;
    private DateTime _tokenExpiresAt = DateTime.MinValue;

    private const string TwitchTokenUrl = "https://id.twitch.tv/oauth2/token";
    private const string IgdbBaseUrl = "https://api.igdb.com/v4";

    /// <summary>Fields to request from IGDB for game enrichment.</summary>
    private const string GameFields = "name,cover.url,summary,genres.name,first_release_date,aggregated_rating";

    public string ProviderName => "IGDB";

    public IgdbClient(HttpClient http, IConfiguration config, ILogger<IgdbClient> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
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
    /// Handles authentication automatically.
    /// </summary>
    private async Task<List<IgdbGame>> QueryGamesAsync(string query, CancellationToken ct)
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

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("IGDB API returned {StatusCode}: {Body}", response.StatusCode, body);
            return [];
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<IgdbGame>>(json, JsonOptions) ?? [];
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
        // IGDB cover URLs come as //images.igdb.com/... — prepend https:
        // Also upgrade to higher resolution (t_cover_big = 264x374)
        string? coverUrl = null;
        if (game.Cover?.Url != null)
        {
            coverUrl = game.Cover.Url
                .Replace("t_thumb", "t_cover_big");
            if (coverUrl.StartsWith("//"))
                coverUrl = "https:" + coverUrl;
        }

        // Convert UNIX timestamp to DateTime
        DateTime? releaseDate = null;
        if (game.FirstReleaseDate.HasValue)
        {
            releaseDate = DateTimeOffset.FromUnixTimeSeconds(game.FirstReleaseDate.Value).UtcDateTime;
        }

        var genres = game.Genres?.Select(g => g.Name).Where(n => n != null).ToList()
            ?? [];

        return new GameMetadata(
            ProviderId: game.Id,
            CoverUrl: coverUrl,
            Summary: game.Summary,
            Genres: genres!,
            FirstReleaseDate: releaseDate,
            Rating: game.AggregatedRating,
            MatchedTitle: game.Name
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

    internal class IgdbGenre
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
