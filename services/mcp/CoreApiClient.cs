using System.Net.Http.Json;
using System.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace GameHub.Mcp;

/// <summary>
/// Typed HTTP client that wraps the GameHub Core REST API.
/// Reads configuration from Core:BaseUrl and Core:ApiKey.
/// </summary>
public sealed class CoreApiClient
{
    private readonly HttpClient _http;
    private readonly ILogger<CoreApiClient> _logger;

    public CoreApiClient(HttpClient http, IConfiguration config, ILogger<CoreApiClient> logger)
    {
        _http = http;
        _logger = logger;

        var baseUrl = config["Core:BaseUrl"] ?? "http://localhost:5001";
        var apiKey = config["Core:ApiKey"] ?? "";

        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        }
    }

    // -------------------------------------------------------------------------
    // Library queries
    // -------------------------------------------------------------------------

    /// <summary>
    /// GET /api/library — paginated, filterable list of unified games.
    /// </summary>
    public async Task<string?> SearchLibraryAsync(
        string? search = null,
        string? platform = null,
        string? playStatus = null,
        string? genre = null,
        string? sortBy = null,
        string sortOrder = "asc",
        int page = 1,
        int pageSize = 20,
        CancellationToken ct = default)
    {
        var qs = BuildQuery(new Dictionary<string, string?>
        {
            ["search"]    = search,
            ["platforms"] = platform,
            ["playStatus"]= playStatus,
            ["tags"]      = genre,
            ["sortBy"]    = sortBy,
            ["sortOrder"] = sortOrder,
            ["page"]      = page.ToString(),
            ["pageSize"]  = pageSize.ToString()
        });
        return await GetJsonStringAsync($"api/library{qs}", ct);
    }

    /// <summary>
    /// GET /api/library/{id} — full details for a single game.
    /// </summary>
    public async Task<string?> GetGameAsync(int id, CancellationToken ct = default)
        => await GetJsonStringAsync($"api/library/{id}", ct);

    /// <summary>
    /// GET /api/library/random — pick a random game ID (respects optional filters).
    /// </summary>
    public async Task<string?> GetRandomGameAsync(
        string? platform = null,
        string? playStatus = null,
        string? genre = null,
        CancellationToken ct = default)
    {
        var qs = BuildQuery(new Dictionary<string, string?>
        {
            ["platforms"] = platform,
            ["playStatus"]= playStatus,
            ["tags"]      = genre,
        });
        return await GetJsonStringAsync($"api/library/random{qs}", ct);
    }

    /// <summary>
    /// GET /api/library/tags — all tag names.
    /// </summary>
    public async Task<string?> GetTagsAsync(CancellationToken ct = default)
        => await GetJsonStringAsync("api/library/tags", ct);

    /// <summary>
    /// GET /api/library/stats — comprehensive library statistics.
    /// </summary>
    public async Task<string?> GetStatsAsync(CancellationToken ct = default)
        => await GetJsonStringAsync("api/library/stats", ct);

    // -------------------------------------------------------------------------
    // Library mutations
    // -------------------------------------------------------------------------

    /// <summary>
    /// PATCH /api/library/{id}/personal — update play status and/or user rating.
    /// Maps to the PersonalUpdateRequest DTO in LibraryController.
    /// </summary>
    public async Task<string?> UpdatePersonalTrackingAsync(int id, object patchBody, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.PatchAsJsonAsync($"api/library/{id}/personal", patchBody, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PATCH /api/library/{Id}/personal failed", id);
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private async Task<string?> GetJsonStringAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "GET {Url} failed — is the Core service running?", url);
            return null;
        }
    }

    private static string BuildQuery(Dictionary<string, string?> parameters)
    {
        var pairs = parameters
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .Select(kv => $"{HttpUtility.UrlEncode(kv.Key)}={HttpUtility.UrlEncode(kv.Value)}");

        var qs = string.Join("&", pairs);
        return string.IsNullOrEmpty(qs) ? "" : "?" + qs;
    }
}
