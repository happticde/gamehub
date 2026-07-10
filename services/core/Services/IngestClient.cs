using GameHub.Core.Data;
using GameHub.Core.Models.PlatformCredentials;
using GameHub.Shared;
using GameHub.Shared.Authentication;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
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
    public async Task<bool> RetrieveStoredAsync(
        Platform platform,
        CancellationToken cancellationToken = default)
    {
        var url = $"/api/retrieve/{platform.ToString().ToLowerInvariant()}/stored";
        using var request = CreateDelegatedRequest(HttpMethod.Post, url);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var throttle = await response.Content.ReadFromJsonAsync<StoredRetrievalThrottleResponse>(
                JsonOptions,
                cancellationToken);
            throw new StoredRetrievalRateLimitedException(platform, throttle?.RetryAfterUtc);
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    /// <summary>Returns tenant-scoped stored-retrieval audit entries from Ingest.</summary>
    public async Task<IReadOnlyList<PlatformRetrievalAuditLogResponse>> GetRetrievalHistoryAsync(
        string? platform,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var url = $"/api/retrieve/history?limit={limit}";
        if (!string.IsNullOrWhiteSpace(platform))
        {
            url += $"&platform={Uri.EscapeDataString(platform)}";
        }

        using var request = CreateDelegatedRequest(HttpMethod.Get, url);
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsurePlatformCredentialSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<PlatformRetrievalAuditLogResponse>>(
                JsonOptions,
                cancellationToken)
            ?? [];
    }

    /// <summary>Returns secret-free encrypted credential status for the active tenant.</summary>
    public async Task<IReadOnlyList<PlatformCredentialStatusResponse>> ListPlatformCredentialStatusesAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = CreateDelegatedRequest(HttpMethod.Get, "/api/internal/platform-credentials");
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsurePlatformCredentialSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<PlatformCredentialStatusResponse>>(
                JsonOptions,
                cancellationToken)
            ?? [];
    }

    /// <summary>Forwards one credential to Ingest's encrypted tenant vault.</summary>
    public async Task UpsertPlatformCredentialAsync(
        Platform platform,
        UpsertPlatformCredentialRequest credential,
        CancellationToken cancellationToken = default)
    {
        var url = $"/api/internal/platform-credentials/{platform.ToString().ToLowerInvariant()}";
        using var request = CreateDelegatedRequest(HttpMethod.Put, url);
        request.Content = JsonContent.Create(credential, options: JsonOptions);
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsurePlatformCredentialSuccessAsync(response, cancellationToken);
    }

    /// <summary>Deletes one encrypted credential for the active tenant.</summary>
    public async Task DeletePlatformCredentialAsync(
        Platform platform,
        CancellationToken cancellationToken = default)
    {
        var url = $"/api/internal/platform-credentials/{platform.ToString().ToLowerInvariant()}";
        using var request = CreateDelegatedRequest(HttpMethod.Delete, url);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        await EnsurePlatformCredentialSuccessAsync(response, cancellationToken);
    }

    /// <summary>
    /// Idempotently deletes every Ingest row, including encrypted credentials, for the active
    /// tenant selected by Core's trusted delegation headers.
    /// </summary>
    public async Task DeleteTenantDataAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateDelegatedRequest(HttpMethod.Delete, "/api/internal/tenant");
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsurePlatformCredentialSuccessAsync(response, cancellationToken);
    }

    /// <summary>
    /// Probes Ingest through the real tenant-delegation trust boundary. The response contains only
    /// process mode and vault readiness; no credential, account, tenant, or library data is read.
    /// </summary>
    public async Task<IngestMultiUserReadinessState> GetMultiUserReadinessAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = CreateDelegatedRequest(
            HttpMethod.Get,
            "/api/internal/multi-user/readiness");
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<IngestMultiUserReadinessState>(
                JsonOptions,
                cancellationToken)
            ?? throw new HttpRequestException("Ingest returned an invalid readiness response.");
    }

    private static async Task EnsurePlatformCredentialSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        PlatformCredentialUpstreamError? error = null;
        try
        {
            error = await response.Content.ReadFromJsonAsync<PlatformCredentialUpstreamError>(
                JsonOptions,
                cancellationToken);
        }
        catch (JsonException)
        {
            // Upstream response bodies are never forwarded verbatim. The fallback below keeps
            // infrastructure errors and accidental secret-bearing content out of Core responses.
        }

        var safeMessage = response.StatusCode switch
        {
            HttpStatusCode.BadRequest => error?.Message ?? "The platform credential is invalid.",
            HttpStatusCode.ServiceUnavailable => "Platform credential storage is unavailable.",
            _ => "The Ingest service could not complete the credential request."
        };
        throw new IngestPlatformCredentialException(response.StatusCode, safeMessage);
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

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed record PlatformCredentialUpstreamError(string Message);
}

/// <summary>Secret-free Ingest guarded-mode state.</summary>
public sealed record IngestMultiUserReadinessState(
    bool MultiUserEnabled,
    bool CredentialVaultReady);

/// <summary>Sanitized failure returned by Ingest's credential vault API.</summary>
public sealed class IngestPlatformCredentialException(
    HttpStatusCode statusCode,
    string safeMessage) : HttpRequestException(safeMessage, null, statusCode)
{
    public HttpStatusCode UpstreamStatusCode { get; } = statusCode;
    public string SafeMessage { get; } = safeMessage;
}
