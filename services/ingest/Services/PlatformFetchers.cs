using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace GameHub.Ingest.Services;

public interface ISteamFetcher
{
    Task<List<JsonElement>> FetchGamesAsync(string apiKey, string steamId);
}

public interface IGogFetcher
{
    Task<List<JsonElement>> FetchGamesAsync(string gogAlCookie, string username);
}

public interface IPsnFetcher
{
    Task<List<JsonElement>> FetchGamesAsync(Dictionary<string, string> cookies);
}

/// <summary>
/// Fetches the user's Steam game library via the Steam Web API.
///
/// API endpoint: IPlayerService/GetOwnedGames
/// Returns a JSON array of game objects with appid, name, playtime_forever, etc.
/// </summary>
public class SteamFetcher : ISteamFetcher
{
    private readonly HttpClient _http;
    private readonly ILogger<SteamFetcher> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public SteamFetcher(HttpClient http, ILogger<SteamFetcher> logger, IServiceScopeFactory scopeFactory)
    {
        _http = http;
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// Fetches all owned games for the given Steam user.
    ///
    /// Parameters:
    ///   - apiKey: Steam Web API key (from steamcommunity.com/dev/apikey)
    ///   - steamId: 64-bit Steam ID
    ///
    /// Returns the raw JSON elements from the "games" array,
    /// ready to be fed into IngestControllerBase.IngestBatch().
    /// </summary>
    public async Task<List<JsonElement>> FetchGamesAsync(string apiKey, string steamId)
    {
        _logger.LogInformation("Fetching Steam library for user {SteamId}", steamId);

        var url = $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v0001/" +
                  $"?key={apiKey}&steamid={steamId}&format=json&include_appinfo=1";

        try
        {
            var resp = await _http.GetAsync(url);
            
            if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
            {
                await WriteAuditLogAsync("Steam", "TokenExpired", 0, "API Key is invalid or expired.");
                throw new HttpRequestException("Steam API Key is invalid or expired.");
            }
            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                await WriteAuditLogAsync("Steam", "RateLimited", 0, "Steam API rate limit exceeded.");
                throw new HttpRequestException("Steam API rate limit exceeded.");
            }

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogError("Steam API returned {StatusCode}", (int)resp.StatusCode);
                await WriteAuditLogAsync("Steam", "Error", 0, $"HTTP {(int)resp.StatusCode}");
                throw new HttpRequestException($"Steam API returned {(int)resp.StatusCode}.");
            }

            var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());

            // Navigate: response -> games (array)
            if (doc.RootElement.TryGetProperty("response", out var response) &&
                response.TryGetProperty("games", out var games))
            {
                var list = games.EnumerateArray().ToList();
                _logger.LogInformation("Steam: fetched {Count} games", list.Count);
                await WriteAuditLogAsync("Steam", "Success", list.Count);
                return list;
            }

            _logger.LogWarning("Steam API returned a valid response but no games array — library may be private");
            await WriteAuditLogAsync("Steam", "Success", 0, "Library may be private (no games array found).");
            return [];
        }
        catch (Exception ex) when (ex is not HttpRequestException)
        {
            await WriteAuditLogAsync("Steam", "Error", 0, ex.Message);
            throw;
        }
    }

    private async Task WriteAuditLogAsync(string platform, string status, int count, string? error = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameHub.Ingest.Data.IngestContext>();
        db.IngestAuditLogs.Add(new GameHub.Ingest.Data.IngestAuditLog
        {
            Platform = platform,
            Status = status,
            GamesFound = count,
            ErrorMessage = error
        });
        await db.SaveChangesAsync();
    }
}

/// <summary>
/// Fetches the user's GOG game library via the public GOG stats endpoint.
///
/// Uses cookie-based auth (gog-al cookie) — matching the user's Python GogClient.
/// The endpoint is paginated, returning embedded game items per page.
/// </summary>
public class GogFetcher : IGogFetcher
{
    private readonly ILogger<GogFetcher> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public GogFetcher(ILogger<GogFetcher> logger, IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// Fetches all games from the user's GOG library.
    ///
    /// Parameters:
    ///   - gogAlCookie: value of the "gog-al" authentication cookie
    ///   - username: GOG username
    ///
    /// Paginates through all pages and returns game objects as JSON elements.
    /// </summary>
    public async Task<List<JsonElement>> FetchGamesAsync(string gogAlCookie, string username)
    {
        _logger.LogInformation("Fetching GOG library for user {Username}", username);

        // Create a client with the gog-al auth cookie
        var handler = new HttpClientHandler();
        handler.CookieContainer.Add(new Cookie("gog-al", gogAlCookie, "/", ".gog.com"));
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        var allGames = new List<JsonElement>();
        int page = 1;

        try
        {
            while (true)
            {
                var url = $"https://www.gog.com/u/{username}/games/stats?page={page}";
                _logger.LogDebug("GOG: fetching page {Page} from {Url}", page, url);

                var resp = await client.GetAsync(url);

                if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                {
                    await WriteAuditLogAsync("GOG", "TokenExpired", 0, "gog-al cookie expired or invalid.");
                    throw new HttpRequestException("GOG API returned 401/403. gog-al cookie has expired.");
                }
                if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    await WriteAuditLogAsync("GOG", "RateLimited", 0, "GOG API rate limit exceeded.");
                    throw new HttpRequestException("GOG API rate limit exceeded.");
                }

                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogError("GOG API returned {StatusCode} on page {Page}",
                        (int)resp.StatusCode, page);
                    await WriteAuditLogAsync("GOG", "Error", 0, $"HTTP {(int)resp.StatusCode}");
                    throw new HttpRequestException($"GOG API returned {(int)resp.StatusCode} on page {page}.");
                }

                var rawJson = await resp.Content.ReadAsStringAsync();
                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(rawJson);
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "GOG: failed to parse JSON response on page {Page}", page);
                    await WriteAuditLogAsync("GOG", "Error", 0, "JSON Parse Exception");
                    throw;
                }

                var root = doc.RootElement;

                // Extract game objects from _embedded.items[].game
                if (root.TryGetProperty("_embedded", out var embedded) &&
                    embedded.TryGetProperty("items", out var items))
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        if (item.TryGetProperty("game", out var game))
                        {
                            allGames.Add(game.Clone());
                        }
                    }
                }
                else
                {
                    _logger.LogWarning("GOG: page {Page} response missing _embedded.items structure", page);
                }

                // Check if we've reached the last page
                if (!root.TryGetProperty("pages", out var pagesElement))
                {
                    _logger.LogWarning("GOG: response missing 'pages' property, stopping pagination");
                    break;
                }

                var totalPages = pagesElement.GetInt32();
                _logger.LogDebug("GOG: page {Page}/{TotalPages}, games so far: {Count}",
                    page, totalPages, allGames.Count);

                if (page >= totalPages) break;
                page++;
            }

            _logger.LogInformation("GOG: fetched {Count} games across {Pages} pages", allGames.Count, page);
            await WriteAuditLogAsync("GOG", "Success", allGames.Count);
            return allGames;
        }
        catch (Exception ex) when (ex is not HttpRequestException)
        {
            await WriteAuditLogAsync("GOG", "Error", 0, ex.Message);
            throw;
        }
    }

    private async Task WriteAuditLogAsync(string platform, string status, int count, string? error = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameHub.Ingest.Data.IngestContext>();
        db.IngestAuditLogs.Add(new GameHub.Ingest.Data.IngestAuditLog
        {
            Platform = platform,
            Status = status,
            GamesFound = count,
            ErrorMessage = error
        });
        await db.SaveChangesAsync();
    }
}

/// <summary>
/// Fetches the user's PlayStation game library via the PSN GraphQL API.
///
/// Uses cookie-based auth — matching the user's Python PSN_Client.
/// The endpoint uses a persisted GraphQL query with pagination.
/// </summary>
public class PsnFetcher : IPsnFetcher
{
    private readonly ILogger<PsnFetcher> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    /// The PSN GraphQL endpoint with persisted query hash.
    /// Placeholders: {0} = start offset, {1} = page size.
    /// </summary>
    private const string GraphQLUrl =
        "https://web.np.playstation.com/api/graphql/v1/op" +
        "?operationName=getPurchasedGameList" +
        "&variables={{\"isActive\":true,\"platform\":[\"ps3\",\"ps4\",\"ps5\"],\"start\":{0},\"size\":{1},\"subscriptionService\":\"NONE\"}}" +
        "&extensions={{\"persistedQuery\":{{\"version\":1,\"sha256Hash\":\"2c045408b0a4d0264bb5a3edfed4efd49fb4749cf8d216be9043768adff905e2\"}}}}";

    public PsnFetcher(ILogger<PsnFetcher> logger, IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// Fetches all purchased games from the user's PSN library.
    ///
    /// Parameters:
    ///   - cookies: dictionary of cookie name/value pairs from store.playstation.com
    ///
    /// Paginates through all pages until pageInfo.isLast is true.
    /// </summary>
    public async Task<List<JsonElement>> FetchGamesAsync(Dictionary<string, string> cookies)
    {
        _logger.LogInformation("Fetching PSN library ({CookieCount} cookies provided)", cookies.Count);

        // Create a client with the PSN auth cookies
        var handler = new HttpClientHandler();
        foreach (var (name, value) in cookies)
        {
            handler.CookieContainer.Add(new Cookie(name, value, "/", "playstation.com"));
        }
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        var allGames = new List<JsonElement>();
        int offset = 0;
        const int pageSize = 100;

        try
        {
            while (true)
            {
                var url = string.Format(GraphQLUrl, offset, pageSize);
                _logger.LogDebug("PSN: fetching offset {Offset}", offset);

                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("Accept", "application/json");
                req.Headers.Add("x-apollo-operation-name", "getPurchasedGameList");
                req.Headers.Add("apollo-require-preflight", "true");

                var resp = await client.SendAsync(req);

                if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                {
                    await WriteAuditLogAsync("PSN", "TokenExpired", 0, "PSN cookies expired or invalid.");
                    throw new HttpRequestException("PSN API returned 401/403. Cookies have expired.");
                }
                if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    await WriteAuditLogAsync("PSN", "RateLimited", 0, "PSN API rate limit exceeded.");
                    throw new HttpRequestException("PSN API rate limit exceeded.");
                }

                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogError("PSN API returned {StatusCode} at offset {Offset}",
                        (int)resp.StatusCode, offset);
                    await WriteAuditLogAsync("PSN", "Error", 0, $"HTTP {(int)resp.StatusCode}");
                    throw new HttpRequestException(
                        $"PSN API returned {(int)resp.StatusCode}. " +
                        "Cookies may have expired.");
                }

                var rawJson = await resp.Content.ReadAsStringAsync();
                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(rawJson);
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "PSN: failed to parse JSON at offset {Offset}", offset);
                    await WriteAuditLogAsync("PSN", "Error", 0, "JSON Parse Exception");
                    throw;
                }

                // Navigate: data -> purchasedTitlesRetrieve -> games
                if (!doc.RootElement.TryGetProperty("data", out var data) ||
                    !data.TryGetProperty("purchasedTitlesRetrieve", out var retrieve))
                {
                    _logger.LogError("PSN: unexpected response structure at offset {Offset}", offset);
                    await WriteAuditLogAsync("PSN", "Error", 0, "Unexpected Response Structure");
                    throw new InvalidOperationException("PSN API returned an unexpected response structure");
                }

                var games = retrieve.GetProperty("games");
                foreach (var game in games.EnumerateArray())
                {
                    allGames.Add(game.Clone());
                }

                // Check if this is the last page
                var isLast = retrieve.GetProperty("pageInfo").GetProperty("isLast").GetBoolean();
                _logger.LogDebug("PSN: offset {Offset}, games so far: {Count}, isLast: {IsLast}",
                    offset, allGames.Count, isLast);

                if (isLast) break;
                offset += pageSize;
            }

            _logger.LogInformation("PSN: fetched {Count} games", allGames.Count);
            await WriteAuditLogAsync("PSN", "Success", allGames.Count);
            return allGames;
        }
        catch (Exception ex) when (ex is not HttpRequestException && ex is not InvalidOperationException)
        {
            await WriteAuditLogAsync("PSN", "Error", 0, ex.Message);
            throw;
        }
    }

    private async Task WriteAuditLogAsync(string platform, string status, int count, string? error = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameHub.Ingest.Data.IngestContext>();
        db.IngestAuditLogs.Add(new GameHub.Ingest.Data.IngestAuditLog
        {
            Platform = platform,
            Status = status,
            GamesFound = count,
            ErrorMessage = error
        });
        await db.SaveChangesAsync();
    }
}
