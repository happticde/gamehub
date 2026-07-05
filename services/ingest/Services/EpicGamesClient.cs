using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameHub.Ingest.Data;
using GameHub.Ingest.Models;

namespace GameHub.Ingest.Services;

public interface IEpicAuthenticationClient
{
    /// <summary>Exchanges a short-lived Epic authorization code for a vault-storable refresh token.</summary>
    Task<EpicPlatformCredential> ExchangeAuthorizationCodeAsync(
        string authorizationCode,
        CancellationToken cancellationToken = default);
}

public interface IEpicFetcher
{
    /// <summary>Fetches the user's Epic Games library using a stored refresh token.</summary>
    Task<List<JsonElement>> FetchGamesAsync(
        EpicPlatformCredential credential,
        CancellationToken cancellationToken = default);
}

/// <summary>Minimal Epic Games API client following Legendary's launcher-compatible flow.</summary>
public sealed class EpicGamesClient(
    HttpClient http,
    ILogger<EpicGamesClient> logger,
    IServiceScopeFactory scopeFactory) : IEpicAuthenticationClient, IEpicFetcher
{
    private const string LauncherClientId = "34a02cf8f4414e29b15921876da36f9a";
    private const string LauncherClientSecret = "daafbccc737745039dffe53d94fc76cf";
    private const string OAuthTokenUrl =
        "https://account-public-service-prod03.ol.epicgames.com/account/api/oauth/token";
    private const string LibraryItemsUrl =
        "https://library-service.live.use1a.on.epicgames.com/library/api/public/items";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<EpicPlatformCredential> ExchangeAuthorizationCodeAsync(
        string authorizationCode,
        CancellationToken cancellationToken = default)
    {
        var session = await StartSessionAsync(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = authorizationCode,
                ["token_type"] = "eg1"
            },
            cancellationToken);

        if (string.IsNullOrWhiteSpace(session.RefreshToken))
        {
            throw new PlatformCredentialValidationException(
                "Epic authorization did not return a refresh token.");
        }

        return new EpicPlatformCredential(
            session.RefreshToken,
            session.AccountId,
            session.DisplayName);
    }

    public async Task<List<JsonElement>> FetchGamesAsync(
        EpicPlatformCredential credential,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Fetching Epic Games library using encrypted stored credential");

        try
        {
            EpicOAuthSession session;
            try
            {
                session = await StartSessionAsync(
                    new Dictionary<string, string>
                    {
                        ["grant_type"] = "refresh_token",
                        ["refresh_token"] = credential.RefreshToken,
                        ["token_type"] = "eg1"
                    },
                    cancellationToken);
            }
            catch (PlatformCredentialValidationException ex)
            {
                await WriteAuditLogAsync("TokenExpired", 0, "Epic refresh token is invalid or expired.");
                throw new HttpRequestException("Epic authentication failed.", ex);
            }

            if (string.IsNullOrWhiteSpace(session.AccessToken))
            {
                await WriteAuditLogAsync("TokenExpired", 0, "Epic refresh token is invalid or expired.");
                throw new HttpRequestException("Epic authentication failed.");
            }

            var rawItems = await FetchLibraryItemsAsync(session.AccessToken, cancellationToken);
            var mapped = new List<JsonElement>();
            foreach (var rawItem in rawItems)
            {
                var mappedItem = EpicLibraryItemMapper.MapToIngestItem(rawItem);
                if (mappedItem is not null)
                {
                    mapped.Add(mappedItem.Value);
                }
            }

            logger.LogInformation(
                "Epic Games: fetched {RawCount} library records and mapped {MappedCount} games",
                rawItems.Count,
                mapped.Count);
            await WriteAuditLogAsync("Success", mapped.Count);
            return mapped;
        }
        catch (Exception ex) when (ex is not HttpRequestException && ex is not InvalidOperationException)
        {
            await WriteAuditLogAsync("Error", 0, ex.Message);
            throw;
        }
    }

    private async Task<EpicOAuthSession> StartSessionAsync(
        Dictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, OAuthTokenUrl)
        {
            Content = new FormUrlEncodedContent(form)
        };
        var basic = Convert.ToBase64String(
            Encoding.ASCII.GetBytes($"{LauncherClientId}:{LauncherClientSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new PlatformCredentialValidationException(
                "Epic authorization failed. The code or refresh token is invalid or expired.");
        }
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new HttpRequestException("Epic API rate limit exceeded.");
        }
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Epic OAuth request failed with status {StatusCode}", (int)response.StatusCode);
            throw new HttpRequestException("Epic authorization failed.");
        }

        return await response.Content.ReadFromJsonAsync<EpicOAuthSession>(
                JsonOptions,
                cancellationToken)
            ?? throw new JsonException("Epic authorization returned an empty response.");
    }

    private async Task<List<JsonElement>> FetchLibraryItemsAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        var items = new List<JsonElement>();
        string? cursor = null;

        do
        {
            var url = $"{LibraryItemsUrl}?includeMetadata=true";
            if (!string.IsNullOrWhiteSpace(cursor))
            {
                url += $"&cursor={Uri.EscapeDataString(cursor)}";
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                await WriteAuditLogAsync("TokenExpired", 0, "Epic access token was rejected.");
                throw new HttpRequestException("Epic authentication failed.");
            }
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                await WriteAuditLogAsync("RateLimited", 0, "Epic API rate limit exceeded.");
                throw new HttpRequestException("Epic API rate limit exceeded.");
            }
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Epic library request failed with status {StatusCode}", (int)response.StatusCode);
                await WriteAuditLogAsync("Error", 0, $"HTTP {(int)response.StatusCode}");
                throw new HttpRequestException("Epic library request failed.");
            }

            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("records", out var records)
                || records.ValueKind != JsonValueKind.Array)
            {
                await WriteAuditLogAsync("Error", 0, "Unexpected Response Structure");
                throw new InvalidOperationException(
                    "Epic library API returned an unexpected response structure.");
            }

            foreach (var record in records.EnumerateArray())
            {
                items.Add(record.Clone());
            }

            cursor = null;
            if (document.RootElement.TryGetProperty("responseMetadata", out var metadata)
                && metadata.TryGetProperty("nextCursor", out var cursorElement))
            {
                cursor = cursorElement.GetString();
            }
        } while (!string.IsNullOrWhiteSpace(cursor));

        return items;
    }

    private async Task WriteAuditLogAsync(string status, int count, string? error = null)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IngestContext>();
        db.IngestAuditLogs.Add(new IngestAuditLog
        {
            Platform = "Epic",
            Status = status,
            GamesFound = count,
            ErrorMessage = error
        });
        await db.SaveChangesAsync();
    }

    private sealed record EpicOAuthSession(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string RefreshToken,
        [property: JsonPropertyName("account_id")] string? AccountId,
        [property: JsonPropertyName("displayName")] string? DisplayName);
}

public static class EpicLibraryItemMapper
{
    /// <summary>Maps an Epic Library record to the existing Epic raw-ingest shape.</summary>
    public static JsonElement? MapToIngestItem(JsonElement record)
    {
        var appName =
            ReadString(record, "appName") ??
            ReadString(record, "app_name") ??
            ReadString(record, "artifactId") ??
            ReadString(record, "catalogItemId");
        if (string.IsNullOrWhiteSpace(appName))
        {
            return null;
        }

        var title =
            ReadString(record, "appTitle") ??
            ReadString(record, "app_title") ??
            ReadString(record, "title") ??
            ReadString(record, "displayName") ??
            ReadNestedString(record, "metadata", "title") ??
            ReadNestedString(record, "catalogItem", "title") ??
            appName;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("app_name", appName);
            writer.WriteString("app_title", title);
            writer.WritePropertyName("legendary_library_item");
            record.WriteTo(writer);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static string? ReadNestedString(JsonElement element, string parent, string property)
    {
        if (!element.TryGetProperty(parent, out var nested)
            || nested.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return ReadString(nested, property);
    }

    private static string? ReadString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
