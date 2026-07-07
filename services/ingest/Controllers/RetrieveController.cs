using GameHub.Ingest.Data;
using GameHub.Ingest.Models;
using GameHub.Ingest.Services;
using GameHub.Shared;
using GameHub.Shared.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
/// Compatibility endpoints receive credentials in the request body. The internal stored route
/// instead decrypts the delegated tenant's vault record immediately before fetching.
/// Each endpoint:
///   1. Resolves credentials from its authorized input path
///   2. Calls the external platform API to fetch the game library
///   3. Passes the raw JSON through the same upsert logic as IngestBatch
///   4. Returns the count of added/updated records
/// </summary>
[ApiController]
[Route("api/retrieve")]
[Authorize]
public class RetrieveController : ControllerBase
{
    private readonly IngestContext _db;
    private readonly ISteamFetcher _steamFetcher;
    private readonly IGogFetcher _gogFetcher;
    private readonly IPsnFetcher _psnFetcher;
    private readonly IEpicFetcher _epicFetcher;
    private readonly PlatformCredentialService _credentials;
    private readonly ILogger<RetrieveController> _logger;

    // Reuse the existing platform controllers to process the fetched data.
    // We instantiate them directly with the same DbContext.
    private readonly SteamIngestController _steamIngest;
    private readonly GogIngestController _gogIngest;
    private readonly PsnIngestController _psnIngest;
    private readonly EpicIngestController _epicIngest;

    public RetrieveController(
        IngestContext db,
        ISteamFetcher steamFetcher,
        IGogFetcher gogFetcher,
        IPsnFetcher psnFetcher,
        IEpicFetcher epicFetcher,
        PlatformCredentialService credentials,
        ILogger<RetrieveController> logger,
        ILoggerFactory loggerFactory)
    {
        _db = db;
        _steamFetcher = steamFetcher;
        _gogFetcher = gogFetcher;
        _psnFetcher = psnFetcher;
        _epicFetcher = epicFetcher;
        _credentials = credentials;
        _logger = logger;

        // Create controller instances to reuse their IngestBatch logic
        _steamIngest = new SteamIngestController(db, loggerFactory.CreateLogger<SteamIngestController>());
        _gogIngest = new GogIngestController(db, loggerFactory.CreateLogger<GogIngestController>());
        _psnIngest = new PsnIngestController(db, loggerFactory.CreateLogger<PsnIngestController>());
        _epicIngest = new EpicIngestController(db, loggerFactory.CreateLogger<EpicIngestController>());
    }

    /// <summary>
    /// POST: api/retrieve/{platform}/stored
    /// Uses the delegated tenant's encrypted vault credential. Only Core's internal
    /// service-key scheme may call this endpoint; compatibility API keys cannot select it.
    /// </summary>
    [HttpPost("{platform}/stored")]
    [Authorize(AuthenticationSchemes = TenantDelegationAuthenticationHandler.SchemeName)]
    public async Task<IActionResult> RetrieveStored(
        Platform platform,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(platform))
        {
            return BadRequest(new PlatformCredentialErrorResponse(
                "Stored retrieval is unsupported for this platform."));
        }

        try
        {
            IActionResult? result = platform switch
            {
                Platform.Steam => await RetrieveStoredSteam(cancellationToken),
                Platform.Gog => await RetrieveStoredGog(cancellationToken),
                Platform.Psn => await RetrieveStoredPsn(cancellationToken),
                Platform.Epic => await RetrieveStoredEpic(cancellationToken),
                _ => null
            };
            return result ?? NotFound(new PlatformCredentialErrorResponse(
                $"No credential is configured for {platform}."));
        }
        catch (PlatformCredentialVaultNotConfiguredException)
        {
            _logger.LogWarning("Stored retrieval rejected because the credential vault is unavailable");
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new PlatformCredentialErrorResponse("Platform credential storage is unavailable."));
        }
        catch (PlatformCredentialReadException)
        {
            _logger.LogWarning("Stored credential for {Platform} could not be decoded", platform);
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new PlatformCredentialErrorResponse("Stored platform credential is unavailable."));
        }
        catch (Exception ex) when (
            ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            _logger.LogWarning("Stored retrieval failed for upstream platform {Platform}", platform);
            return StatusCode(
                StatusCodes.Status502BadGateway,
                new PlatformCredentialErrorResponse("Platform retrieval failed."));
        }
    }

    private async Task<IActionResult?> RetrieveStoredSteam(CancellationToken cancellationToken)
    {
        var credential = await _credentials.GetSteamAsync(cancellationToken);
        if (credential is null)
        {
            return null;
        }

        var games = await _steamFetcher.FetchGamesAsync(credential.ApiKey, credential.SteamId);
        _logger.LogInformation("Stored Steam retrieve completed: {Count} games processed", games.Count);
        return await _steamIngest.IngestBatch(games);
    }

    private async Task<IActionResult?> RetrieveStoredGog(CancellationToken cancellationToken)
    {
        var credential = await _credentials.GetGogAsync(cancellationToken);
        if (credential is null)
        {
            return null;
        }

        var games = await _gogFetcher.FetchGamesAsync(credential.Cookie, credential.User);
        _logger.LogInformation("Stored GOG retrieve completed: {Count} games processed", games.Count);
        return await _gogIngest.IngestBatch(games);
    }

    private async Task<IActionResult?> RetrieveStoredPsn(CancellationToken cancellationToken)
    {
        var credential = await _credentials.GetPsnAsync(cancellationToken);
        if (credential is null)
        {
            return null;
        }

        var games = await _psnFetcher.FetchGamesAsync(credential.Cookies);
        _logger.LogInformation("Stored PSN retrieve completed: {Count} games processed", games.Count);
        return await _psnIngest.IngestBatch(games);
    }

    private async Task<IActionResult?> RetrieveStoredEpic(CancellationToken cancellationToken)
    {
        var credential = await _credentials.GetEpicAsync(cancellationToken);
        if (credential is null)
        {
            return null;
        }

        var games = await _epicFetcher.FetchGamesAsync(credential, cancellationToken);
        _logger.LogInformation("Stored Epic retrieve completed: {Count} games processed", games.Count);
        return await _epicIngest.IngestBatch(games);
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
            _logger.LogInformation("Starting Steam retrieve for user {SteamId}", request.SteamId);
            var games = await _steamFetcher.FetchGamesAsync(request.ApiKey, request.SteamId);
            var result = await _steamIngest.IngestBatch(games);
            _logger.LogInformation("Steam retrieve completed: {Count} games processed", games.Count);
            return result;
        }
        catch (HttpRequestException)
        {
            _logger.LogWarning("Steam API request failed for user {SteamId}", request.SteamId);
            return StatusCode(502, new { Message = "Failed to fetch from Steam API." });
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse Steam API response for user {SteamId}", request.SteamId);
            return StatusCode(502, new { Message = "Steam API returned unexpected data." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during Steam retrieve for user {SteamId}", request.SteamId);
            return StatusCode(500, new { Message = "Internal retrieval error." });
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
            _logger.LogInformation("Starting GOG retrieve for user {Username}", request.User);
            var games = await _gogFetcher.FetchGamesAsync(request.Cookie, request.User);
            var result = await _gogIngest.IngestBatch(games);
            _logger.LogInformation("GOG retrieve completed: {Count} games processed", games.Count);
            return result;
        }
        catch (HttpRequestException)
        {
            _logger.LogWarning("GOG API request failed for user {Username}", request.User);
            return StatusCode(502, new { Message = "Failed to fetch from GOG API." });
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse GOG API response for user {Username}", request.User);
            return StatusCode(502, new { Message = "GOG API returned unexpected data." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during GOG retrieve for user {Username}", request.User);
            return StatusCode(500, new { Message = "Internal retrieval error." });
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
            _logger.LogInformation("Starting PSN retrieve ({CookieCount} cookies provided)", request.Cookies.Count);
            var games = await _psnFetcher.FetchGamesAsync(request.Cookies);
            var result = await _psnIngest.IngestBatch(games);
            _logger.LogInformation("PSN retrieve completed: {Count} games processed", games.Count);
            return result;
        }
        catch (HttpRequestException)
        {
            _logger.LogWarning("PSN API request failed");
            return StatusCode(502, new { Message = "Failed to fetch from PSN API." });
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse PSN API response");
            return StatusCode(502, new { Message = "PSN API returned unexpected data." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during PSN retrieve");
            return StatusCode(500, new { Message = "Internal retrieval error." });
        }
    }

    /// <summary>
    /// GET: api/retrieve/history
    /// Returns the most recent ingest audit logs.
    /// Optionally filter by platform (e.g., "Steam").
    /// </summary>
    [HttpGet("history")]
    public async Task<IActionResult> GetIngestHistory(
        [FromQuery] string? platform,
        [FromQuery] int limit = 50)
    {
        var query = _db.IngestAuditLogs.AsNoTracking();

        if (!string.IsNullOrEmpty(platform))
            query = query.Where(l => l.Platform.ToLower() == platform.ToLower());

        var logs = await query
            .OrderByDescending(l => l.Timestamp)
            .Take(limit)
            .Select(l => new PlatformRetrievalAuditLogResponse(
                l.Id,
                l.Timestamp,
                l.Platform,
                l.Status,
                l.GamesFound,
                l.ErrorMessage))
            .ToListAsync();

        return Ok(logs);
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

/// <summary>Secret-free retrieval audit row returned to Core.</summary>
public sealed record PlatformRetrievalAuditLogResponse(
    int Id,
    DateTime Timestamp,
    string Platform,
    string Status,
    int ItemsProcessed,
    string? ErrorMessage);
