using GameHub.Core.Data;
using GameHub.Shared;
using GameHub.Shared.Authentication;
using System.Net;

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
    private readonly ITenantContext _tenantContext;
    private readonly string _serviceKey;

    /// <summary>Creates a client that delegates Core's trusted tenant to Ingest.</summary>
    public IngestClient(
        HttpClient http,
        ITenantContext tenantContext,
        IConfiguration configuration)
    {
        _http = http;
        _tenantContext = tenantContext;
        _serviceKey = configuration["Services:IngestServiceKey"] ?? "";
    }

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

        using var request = CreateDelegatedRequest(HttpMethod.Get, url);

        using var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PagedResult<RawGameDto>>()
            ?? new PagedResult<RawGameDto>();
    }

    /// <summary>
    /// Asks Ingest to refresh one platform using the delegated tenant's encrypted credential.
    /// Returns false when that tenant has no credential for the platform.
    /// </summary>
    public async Task<bool> RetrieveStoredAsync(Platform platform)
    {
        var url = $"/api/retrieve/{platform.ToString().ToLowerInvariant()}/stored";
        using var request = CreateDelegatedRequest(HttpMethod.Post, url);
        using var response = await _http.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    private HttpRequestMessage CreateDelegatedRequest(HttpMethod method, string url)
    {
        if (string.IsNullOrWhiteSpace(_serviceKey))
        {
            throw new InvalidOperationException(
                "Services:IngestServiceKey must be configured before Core can call Ingest.");
        }

        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(
            TenantDelegationAuthenticationHandler.ServiceKeyHeaderName,
            _serviceKey);
        request.Headers.Add(
            TenantDelegationAuthenticationHandler.TenantHeaderName,
            _tenantContext.TenantId.ToString("D"));
        return request;
    }
}
