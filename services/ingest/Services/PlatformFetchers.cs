using System.Net;
using System.Text.Json;

namespace GameHub.Ingest.Services;

/// <summary>
/// Fetches the user's Steam game library via the Steam Web API.
///
/// API endpoint: IPlayerService/GetOwnedGames
/// Returns a JSON array of game objects with appid, name, playtime_forever, etc.
/// </summary>
public class SteamFetcher
{
    private readonly HttpClient _http;

    public SteamFetcher(HttpClient http) { _http = http; }

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
        var url = $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v0001/" +
                  $"?key={apiKey}&steamid={steamId}&format=json&include_appinfo=1";

        var resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();

        var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());

        // Navigate: response -> games (array)
        if (doc.RootElement.TryGetProperty("response", out var response) &&
            response.TryGetProperty("games", out var games))
        {
            return games.EnumerateArray().ToList();
        }

        return [];
    }
}

/// <summary>
/// Fetches the user's GOG game library via the public GOG stats endpoint.
///
/// Uses cookie-based auth (gog-al cookie) — matching the user's Python GogClient.
/// The endpoint is paginated, returning embedded game items per page.
/// </summary>
public class GogFetcher
{

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
        // Create a client with the gog-al auth cookie
        var handler = new HttpClientHandler();
        handler.CookieContainer.Add(new Cookie("gog-al", gogAlCookie, "/", ".gog.com"));
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        var allGames = new List<JsonElement>();
        int page = 1;

        while (true)
        {
            var url = $"https://www.gog.com/u/{username}/games/stats?page={page}";
            var resp = await client.GetAsync(url);
            resp.EnsureSuccessStatusCode();

            var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
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

            // Check if we've reached the last page
            var totalPages = root.GetProperty("pages").GetInt32();
            if (page >= totalPages) break;
            page++;
        }

        return allGames;
    }
}

/// <summary>
/// Fetches the user's PlayStation game library via the PSN GraphQL API.
///
/// Uses cookie-based auth — matching the user's Python PSN_Client.
/// The endpoint uses a persisted GraphQL query with pagination.
/// </summary>
public class PsnFetcher
{

    /// <summary>
    /// The PSN GraphQL endpoint with persisted query hash.
    /// Placeholders: {0} = start offset, {1} = page size.
    /// </summary>
    private const string GraphQLUrl =
        "https://web.np.playstation.com/api/graphql/v1/op" +
        "?operationName=getPurchasedGameList" +
        "&variables={{\"isActive\":true,\"platform\":[\"ps3\",\"ps4\",\"ps5\"],\"start\":{0},\"size\":{1},\"subscriptionService\":\"NONE\"}}" +
        "&extensions={{\"persistedQuery\":{{\"version\":1,\"sha256Hash\":\"2c045408b0a4d0264bb5a3edfed4efd49fb4749cf8d216be9043768adff905e2\"}}}}";

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

        while (true)
        {
            var url = string.Format(GraphQLUrl, offset, pageSize);

            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Accept", "application/json");

            var resp = await client.SendAsync(req);
            resp.EnsureSuccessStatusCode();

            var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());

            // Navigate: data -> purchasedTitlesRetrieve -> games
            var retrieve = doc.RootElement
                .GetProperty("data")
                .GetProperty("purchasedTitlesRetrieve");

            var games = retrieve.GetProperty("games");
            foreach (var game in games.EnumerateArray())
            {
                allGames.Add(game.Clone());
            }

            // Check if this is the last page
            var isLast = retrieve.GetProperty("pageInfo").GetProperty("isLast").GetBoolean();
            if (isLast) break;

            offset += pageSize;
        }

        return allGames;
    }
}
