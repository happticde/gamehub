using GameHub.Shared;

namespace GameHub.Core.Services;

/// <summary>
/// Typed HTTP client for communicating with the Ingest service's ExportController.
///
/// Provides a single method to fetch any platform's data in pages,
/// with optional incremental filtering via updatedAfter timestamp.
///
/// Registered in DI with: builder.Services.AddHttpClient&lt;IngestClient&gt;(...)
/// </summary>
public class IngestClient
{
    private readonly HttpClient _http;

    public IngestClient(HttpClient http) { _http = http; }

    /// <summary>
    /// Fetches a single page of game data from the Ingest service.
    ///
    /// Example URL: /api/export/steam?page=1&amp;pageSize=200&amp;updatedAfter=2024-01-01T00:00:00Z
    ///
    /// Parameters:
    ///   - platform: Which platform to fetch (Steam, Psn, Gog, Epic)
    ///   - page: Page number (1-indexed)
    ///   - pageSize: Items per page
    ///   - updatedAfter: If provided, only return records updated after this timestamp
    ///                   (used for incremental sync)
    ///
    /// Returns a PagedResult containing the items and pagination metadata,
    /// or an empty result if the request fails.
    /// </summary>
    public async Task<PagedResult<RawGameDto>> FetchPageAsync(
        Platform platform,
        int page = 1,
        int pageSize = 200,
        DateTime? updatedAfter = null)
    {
        // Build the URL with query parameters
        var url = $"/api/export/{platform.ToString().ToLowerInvariant()}?page={page}&pageSize={pageSize}";

        // Append the incremental sync filter if a timestamp is provided
        if (updatedAfter.HasValue)
            url += $"&updatedAfter={updatedAfter.Value:O}"; // ISO 8601 format

        // Fetch and deserialize, returning an empty result on failure
        return await _http.GetFromJsonAsync<PagedResult<RawGameDto>>(url)
            ?? new PagedResult<RawGameDto>();
    }
}
