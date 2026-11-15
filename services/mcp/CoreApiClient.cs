using System.Net.Http.Json;
using System.Net;
using System.Net.Http.Headers;
using System.Web;
using GameHub.Mcp.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace GameHub.Mcp;

/// <summary>
/// Typed HTTP client that wraps the GameHub Core REST API.
/// Selects user authorization or the separate operator-key compatibility mode.
/// </summary>
public sealed class CoreApiClient
{
    private readonly HttpClient _http;
    private readonly ILogger<CoreApiClient> _logger;
    private readonly McpTokenProvider? _tokens;
    private readonly AsyncLocal<string?> _operationToken = new();

    public CoreApiClient(HttpClient http, IConfiguration config, ILogger<CoreApiClient> logger)
    {
        _http = http;
        _logger = logger;

        _http.DefaultRequestHeaders.Remove("Authorization");
        _http.DefaultRequestHeaders.Remove("X-Api-Key");
        var mode = config["Mcp:AuthenticationMode"] ?? "ApiKey";
        if (mode == "User")
        {
            var settings = new McpLoginSettings(config["Mcp:Issuer"], config["Mcp:Resource"],
                config["Mcp:Connection"], config["Mcp:CredentialDirectory"] ?? "/var/lib/gamehub-mcp");
            _http.BaseAddress = settings.Issuer;
            _tokens = new McpTokenProvider(settings, http);
            return;
        }
        if (mode != "ApiKey")
            throw new McpLoginException("Configure Mcp__AuthenticationMode as User or ApiKey. Authorization failure never changes authentication mode.");

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
        => await SendAsync(HttpMethod.Patch, $"api/library/{id}/personal", patchBody, ct);

    /// <summary>Prevents logout/login from changing the owner between a tracking read and its update.</summary>
    public async Task<string> WithTrackingConnectionAsync(Func<Task<string>> operation)
    {
        try
        {
            if (_tokens is null) return await operation();
            return await _tokens.UseAccessTokenAsync(true, async token =>
            {
                var previous = _operationToken.Value;
                _operationToken.Value = token;
                try { return await operation(); }
                finally { _operationToken.Value = previous; }
            });
        }
        catch (McpLoginException error) { throw new McpException(error.Message); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new McpException("Credential storage is unavailable. Check the dedicated volume and permissions."); }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private async Task<string?> GetJsonStringAsync(string url, CancellationToken ct)
        => await SendAsync(HttpMethod.Get, url, null, ct);

    private async Task<string?> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        try
        {
            if (_tokens is not null && _operationToken.Value is null)
                return await _tokens.UseAccessTokenAsync(method == HttpMethod.Patch,
                    token => SendAuthorizedAsync(token), ct);
            return await SendAuthorizedAsync(_operationToken.Value);
        }
        catch (McpLoginException error) { throw new McpException(error.Message); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new McpException("Credential storage is unavailable. Check the dedicated volume and permissions; no authorization fallback was attempted.");
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
        {
            _logger.LogWarning("GameHub request failed or timed out. No request was retried.");
            return null;
        }

        async Task<string?> SendAuthorizedAsync(string? token)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = JsonContent.Create(body);
            if (token is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            // No response-triggered retry: a write may already have committed when its receipt is lost.
            using var response = await _http.SendAsync(request, ct);
            if (_tokens is not null && response.StatusCode == HttpStatusCode.Unauthorized)
                throw new McpException("GameHub rejected this authorization. Run logout, then login in an interactive terminal. No operation was retried.");
            if (_tokens is not null && response.StatusCode == HttpStatusCode.Forbidden)
                throw new McpException("GameHub denied this operation for the selected connection. Check its approved permissions. No operation was retried.");
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsStringAsync(ct);
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
