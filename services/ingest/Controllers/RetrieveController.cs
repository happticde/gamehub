using GameHub.Ingest.Data;
using GameHub.Ingest.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace GameHub.Ingest.Controllers;

/// <summary>
/// Server-side game library retrieval (ADR-001).
///
/// These endpoints fetch game data directly from platform APIs,
/// then feed the results into the existing ingest pipeline.
/// This eliminates the need for the CLI — a single HTTP call
/// does both the fetch and the ingest.
///
/// Each endpoint:
///   1. Receives credentials (API key, cookie, etc.) in the request body
///   2. Calls the external platform API to fetch the game library
///   3. Passes the raw JSON through the same upsert logic as IngestBatch
///   4. Returns the count of added/updated records
/// </summary>
[ApiController]
[Route("api/retrieve")]
public class RetrieveController : ControllerBase
{
    private readonly IngestContext _db;
    private readonly SteamFetcher _steamFetcher;
    private readonly GogFetcher _gogFetcher;
    private readonly PsnFetcher _psnFetcher;

    // Reuse the existing platform controllers to process the fetched data.
    // We instantiate them directly with the same DbContext.
    private readonly SteamIngestController _steamIngest;
    private readonly GogIngestController _gogIngest;
    private readonly PsnIngestController _psnIngest;

    public RetrieveController(
        IngestContext db,
        SteamFetcher steamFetcher,
        GogFetcher gogFetcher,
        PsnFetcher psnFetcher)
    {
        _db = db;
        _steamFetcher = steamFetcher;
        _gogFetcher = gogFetcher;
        _psnFetcher = psnFetcher;

        // Create controller instances to reuse their IngestBatch logic
        _steamIngest = new SteamIngestController(db);
        _gogIngest = new GogIngestController(db);
        _psnIngest = new PsnIngestController(db);
    }

    /// <summary>
    /// POST: api/retrieve/steam
    /// Body: { "apiKey": "YOUR_KEY", "steamId": "YOUR_STEAM_ID_64" }
    ///
    /// Fetches the user's Steam library and ingests it in one call.
    /// </summary>
    [HttpPost("steam")]
    public async Task<IActionResult> RetrieveSteam([FromBody] SteamRetrieveRequest request)
    {
        if (string.IsNullOrEmpty(request.ApiKey) || string.IsNullOrEmpty(request.SteamId))
            return BadRequest(new { Message = "apiKey and steamId are required" });

        try
        {
            var games = await _steamFetcher.FetchGamesAsync(request.ApiKey, request.SteamId);
            var result = await _steamIngest.IngestBatch(games);
            return result;
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(502, new { Message = $"Failed to fetch from Steam API: {ex.Message}" });
        }
    }

    /// <summary>
    /// POST: api/retrieve/gog
    /// Body: { "cookie": "GOG_AL_VALUE", "user": "GOG_USERNAME" }
    ///
    /// Fetches the user's GOG library using the gog-al cookie and ingests it.
    /// </summary>
    [HttpPost("gog")]
    public async Task<IActionResult> RetrieveGog([FromBody] GogRetrieveRequest request)
    {
        if (string.IsNullOrEmpty(request.Cookie) || string.IsNullOrEmpty(request.User))
            return BadRequest(new { Message = "cookie and user are required" });

        try
        {
            var games = await _gogFetcher.FetchGamesAsync(request.Cookie, request.User);
            var result = await _gogIngest.IngestBatch(games);
            return result;
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(502, new { Message = $"Failed to fetch from GOG API: {ex.Message}" });
        }
    }

    /// <summary>
    /// POST: api/retrieve/psn
    /// Body: { "cookies": { "JSESSIONID": "abc", "sb-cdn.session": "xyz", ... } }
    ///
    /// Fetches the user's PSN library using browser cookies and ingests it.
    /// </summary>
    [HttpPost("psn")]
    public async Task<IActionResult> RetrievePsn([FromBody] PsnRetrieveRequest request)
    {
        if (request.Cookies == null || request.Cookies.Count == 0)
            return BadRequest(new { Message = "cookies dictionary is required" });

        try
        {
            var games = await _psnFetcher.FetchGamesAsync(request.Cookies);
            var result = await _psnIngest.IngestBatch(games);
            return result;
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(502, new { Message = $"Failed to fetch from PSN API: {ex.Message}" });
        }
    }
}

// =============================================================================
// Request DTOs
// =============================================================================

/// <summary>Request body for Steam retrieval.</summary>
public class SteamRetrieveRequest
{
    public string ApiKey { get; set; } = "";
    public string SteamId { get; set; } = "";
}

/// <summary>Request body for GOG retrieval.</summary>
public class GogRetrieveRequest
{
    /// <summary>Value of the gog-al cookie.</summary>
    public string Cookie { get; set; } = "";
    /// <summary>GOG username.</summary>
    public string User { get; set; } = "";
}

/// <summary>Request body for PSN retrieval.</summary>
public class PsnRetrieveRequest
{
    /// <summary>Browser cookies from store.playstation.com as key-value pairs.</summary>
    public Dictionary<string, string> Cookies { get; set; } = new();
}
