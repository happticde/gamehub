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
    IServiceScopeFactory scopeFactory,
    IEpicRefreshTokenRotationStore refreshTokenRotationStore) : IEpicAuthenticationClient, IEpicFetcher
{
    private const string LauncherClientId = "34a02cf8f4414e29b15921876da36f9a";
    private const string LauncherClientSecret = "daafbccc737745039dffe53d94fc76cf";
    private const string LauncherUserAgent =
        "UELauncher/11.0.1-14907503+++Portal+Release-Live Windows/10.0.19041.1.256.64bit";
    private const string OAuthTokenUrl =
        "https://account-public-service-prod03.ol.epicgames.com/account/api/oauth/token";
    private const string LauncherAssetsUrl =
        "https://launcher-public-service-prod06.ol.epicgames.com/launcher/api/public/assets/Windows?label=Live";
    private const string CatalogItemsUrl =
        "https://catalog-public-service-prod06.ol.epicgames.com/catalog/api/shared/namespace";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private enum EpicProviderFailure
    {
        Authentication,
        RateLimited,
        UpstreamUnavailable,
        MalformedResponse
    }

    /// <summary>Keeps provider-specific failure details inside Ingest while retaining a safe category.</summary>
    private sealed class EpicProviderException(EpicProviderFailure failure, Exception? innerException = null)
        : HttpRequestException("Epic provider request failed.", innerException)
    {
        public EpicProviderFailure Failure { get; } = failure;
    }

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
            var session = await StartSessionAsync(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = credential.RefreshToken,
                    ["token_type"] = "eg1"
                },
                cancellationToken);

            if (string.IsNullOrWhiteSpace(session.AccessToken))
            {
                throw new EpicProviderException(EpicProviderFailure.Authentication);
            }

            await PersistRefreshTokenRotationAsync(credential, session, cancellationToken);

            var launcherGames = await FetchLauncherGamesAsync(session.AccessToken, cancellationToken);
            var mapped = new List<JsonElement>();
            foreach (var rawItem in launcherGames.Items)
            {
                var mappedItem = EpicLibraryItemMapper.MapToIngestItem(
                    rawItem.Asset,
                    rawItem.CatalogMetadata);
                if (mappedItem is not null)
                {
                    mapped.Add(mappedItem.Value);
                }
            }

            logger.LogInformation(
                "Epic Games: fetched {RawCount} launcher asset records, mapped {MappedCount} games, and skipped {SkippedCount} unavailable catalog records",
                launcherGames.Items.Count,
                mapped.Count,
                launcherGames.SkippedCatalogRecords);
            await WriteAuditLogAsync(
                launcherGames.SkippedCatalogRecords == 0 ? "Success" : "PartialSuccess",
                mapped.Count,
                launcherGames.SkippedCatalogRecords == 0
                    ? null
                    : "Some Epic catalog metadata records were unavailable or malformed and were skipped.");
            return mapped;
        }
        catch (PlatformCredentialValidationException ex)
        {
            await WriteAuditLogAsync("TokenExpired", 0, "Epic refresh token is invalid or expired.");
            throw new HttpRequestException("Epic authentication failed.", ex);
        }
        catch (PlatformCredentialRotationException)
        {
            await WriteAuditLogAsync("Error", 0, "Epic refresh token rotation could not be persisted.");
            throw;
        }
        catch (EpicProviderException ex)
        {
            var (status, error) = ex.Failure switch
            {
                EpicProviderFailure.Authentication => ("TokenExpired", "Epic authentication was rejected."),
                EpicProviderFailure.RateLimited => ("RateLimited", "Epic API rate limit exceeded."),
                EpicProviderFailure.UpstreamUnavailable => ("UpstreamUnavailable", "Epic provider is temporarily unavailable."),
                EpicProviderFailure.MalformedResponse => ("MalformedResponse", "Epic provider returned an unexpected response."),
                _ => ("Error", "Epic retrieval failed.")
            };
            await WriteAuditLogAsync(status, 0, error);
            throw new HttpRequestException("Epic library request failed.", ex);
        }
        catch (JsonException ex)
        {
            await WriteAuditLogAsync("MalformedResponse", 0, "Epic provider returned an unexpected response.");
            throw new HttpRequestException("Epic library request failed.", ex);
        }
        catch (InvalidOperationException ex)
        {
            await WriteAuditLogAsync("MalformedResponse", 0, "Epic provider returned an unexpected response.");
            throw new HttpRequestException("Epic library request failed.", ex);
        }
        catch (HttpRequestException)
        {
            await WriteAuditLogAsync("UpstreamUnavailable", 0, "Epic provider is temporarily unavailable.");
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await WriteAuditLogAsync("Error", 0, "Epic retrieval failed unexpectedly.");
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
        ApplyEpicHeaders(request);

        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode is
            HttpStatusCode.BadRequest or
            HttpStatusCode.Unauthorized or
            HttpStatusCode.Forbidden)
        {
            throw new PlatformCredentialValidationException(
                "Epic authorization failed. The code or refresh token is invalid or expired.");
        }
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new EpicProviderException(EpicProviderFailure.RateLimited);
        }
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Epic OAuth request failed with status {StatusCode}", (int)response.StatusCode);
            throw new EpicProviderException(EpicProviderFailure.UpstreamUnavailable);
        }

        return await response.Content.ReadFromJsonAsync<EpicOAuthSession>(
                JsonOptions,
                cancellationToken)
            ?? throw new JsonException("Epic authorization returned an empty response.");
    }

    private async Task PersistRefreshTokenRotationAsync(
        EpicPlatformCredential credential,
        EpicOAuthSession session,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(session.RefreshToken)
            || string.Equals(session.RefreshToken, credential.RefreshToken, StringComparison.Ordinal))
        {
            return;
        }

        await refreshTokenRotationStore.PersistIfRotatedAsync(
            credential,
            new EpicPlatformCredential(
                session.RefreshToken,
                session.AccountId ?? credential.AccountId,
                session.DisplayName ?? credential.DisplayName),
            cancellationToken);
    }

    private static void ApplyEpicHeaders(HttpRequestMessage request)
    {
        if (request.Headers.UserAgent.Count == 0)
        {
            request.Headers.UserAgent.ParseAdd(LauncherUserAgent);
        }
    }

    private async Task<EpicLauncherGameResult> FetchLauncherGamesAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        var items = new List<EpicLauncherGameRecord>();
        var skippedCatalogRecords = 0;
        var assets = await FetchGameAssetsAsync(accessToken, cancellationToken);
        var seenAppNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var asset in assets)
        {
            var appName = EpicLibraryItemMapper.ReadAppName(asset);
            var namespaceName = EpicLibraryItemMapper.ReadNamespace(asset);
            var catalogItemId = EpicLibraryItemMapper.ReadCatalogItemId(asset);
            if (string.IsNullOrWhiteSpace(appName)
                || string.IsNullOrWhiteSpace(namespaceName)
                || string.IsNullOrWhiteSpace(catalogItemId)
                || string.Equals(namespaceName, "ue", StringComparison.OrdinalIgnoreCase)
                || !seenAppNames.Add(appName))
            {
                continue;
            }

            JsonElement? metadata;
            try
            {
                metadata = await FetchCatalogMetadataAsync(
                    accessToken,
                    namespaceName,
                    catalogItemId,
                    cancellationToken);
            }
            catch (EpicProviderException ex) when (ex.Failure is
                EpicProviderFailure.UpstreamUnavailable or EpicProviderFailure.MalformedResponse)
            {
                skippedCatalogRecords++;
                logger.LogWarning(
                    "Skipping Epic catalog metadata record because the provider response was unavailable or malformed");
                continue;
            }
            if (metadata is null)
            {
                skippedCatalogRecords++;
                continue;
            }

            items.Add(new EpicLauncherGameRecord(asset, metadata.Value));
        }

        return new EpicLauncherGameResult(items, skippedCatalogRecords);
    }

    private async Task<List<JsonElement>> FetchGameAssetsAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LauncherAssetsUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await SendEpicApiRequestAsync(request, "Epic launcher assets", cancellationToken);
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new EpicProviderException(EpicProviderFailure.MalformedResponse, ex);
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new EpicProviderException(EpicProviderFailure.MalformedResponse);
            }

            var items = new List<JsonElement>();
            foreach (var record in document.RootElement.EnumerateArray())
            {
                items.Add(record.Clone());
            }

            return items;
        }
    }

    private async Task<JsonElement?> FetchCatalogMetadataAsync(
        string accessToken,
        string namespaceName,
        string catalogItemId,
        CancellationToken cancellationToken)
    {
        var url =
            $"{CatalogItemsUrl}/{Uri.EscapeDataString(namespaceName)}/bulk/items" +
            $"?id={Uri.EscapeDataString(catalogItemId)}" +
            "&includeDLCDetails=true&includeMainGameDetails=true&country=US&locale=en-US";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await SendEpicApiRequestAsync(request, "Epic catalog metadata", cancellationToken);
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new EpicProviderException(EpicProviderFailure.MalformedResponse, ex);
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new EpicProviderException(EpicProviderFailure.MalformedResponse);
            }

            if (document.RootElement.TryGetProperty(catalogItemId, out var metadata)
                && metadata.ValueKind == JsonValueKind.Object)
            {
                return metadata.Clone();
            }

            return null;
        }
    }

    private async Task<HttpResponseMessage> SendEpicApiRequestAsync(
        HttpRequestMessage request,
        string operation,
        CancellationToken cancellationToken)
    {
        ApplyEpicHeaders(request);
        var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            response.Dispose();
            throw new EpicProviderException(EpicProviderFailure.Authentication);
        }
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            response.Dispose();
            throw new EpicProviderException(EpicProviderFailure.RateLimited);
        }
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("{Operation} request failed with status {StatusCode}", operation, (int)response.StatusCode);
            response.Dispose();
            throw new EpicProviderException(EpicProviderFailure.UpstreamUnavailable);
        }

        return response;
    }

    private sealed record EpicLauncherGameRecord(JsonElement Asset, JsonElement CatalogMetadata);

    private sealed record EpicLauncherGameResult(
        List<EpicLauncherGameRecord> Items,
        int SkippedCatalogRecords);

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
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("account_id")] string? AccountId,
        [property: JsonPropertyName("displayName")] string? DisplayName);
}

public static class EpicLibraryItemMapper
{
    /// <summary>Maps Epic launcher asset and catalog metadata to the existing raw-ingest shape.</summary>
    public static JsonElement? MapToIngestItem(JsonElement asset, JsonElement catalogMetadata)
    {
        var appName = ReadAppName(asset);
        var title = ReadString(catalogMetadata, "title");
        if (string.IsNullOrWhiteSpace(appName)
            || string.IsNullOrWhiteSpace(title)
            || ShouldSkip(asset, catalogMetadata))
        {
            return null;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("app_name", appName);
            writer.WriteString("app_title", title);
            WriteOptionalString(writer, "namespace", ReadNamespace(asset) ?? ReadString(catalogMetadata, "namespace"));
            WriteOptionalString(writer, "catalog_item_id", ReadCatalogItemId(asset) ?? ReadString(catalogMetadata, "id"));
            WriteOptionalString(writer, "asset_id", ReadString(asset, "assetId"));
            WriteOptionalString(writer, "developer", ReadDeveloper(catalogMetadata));
            WriteOptionalString(writer, "publisher", ReadPublisher(catalogMetadata));
            WriteOptionalString(writer, "release_date", ReadReleaseDate(catalogMetadata));
            WriteOptionalString(writer, "description", ReadString(catalogMetadata, "description"));
            WriteOptionalString(writer, "summary", ReadString(catalogMetadata, "summary") ?? ReadString(catalogMetadata, "shortDescription"));
            WriteOptionalStringArray(writer, "categories", ReadCategoryPaths(catalogMetadata));
            WriteOptionalString(writer, "cover_url", ReadKeyImageUrl(catalogMetadata, CoverImageTypes));
            WriteOptionalString(writer, "key_art_url", ReadKeyImageUrl(catalogMetadata, KeyArtImageTypes));
            writer.WritePropertyName("legendary_asset");
            asset.WriteTo(writer);
            writer.WritePropertyName("legendary_metadata");
            catalogMetadata.WriteTo(writer);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    /// <summary>Maps legacy library-service records to the existing raw-ingest shape.</summary>
    public static JsonElement? MapToIngestItem(JsonElement record)
    {
        if (!record.TryGetProperty("metadata", out var metadata)
            || metadata.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return MapToIngestItem(record, metadata);
    }

    /// <summary>Reads Legendary's stable app name from an Epic asset or compatible record.</summary>
    public static string? ReadAppName(JsonElement element) =>
        ReadString(element, "appName") ??
        ReadString(element, "app_name") ??
        ReadString(element, "artifactId");

    /// <summary>Reads the Epic catalog item identifier from an Epic asset or compatible record.</summary>
    public static string? ReadCatalogItemId(JsonElement element) =>
        ReadString(element, "catalogItemId") ??
        ReadString(element, "catalog_item_id");

    /// <summary>Reads the Epic namespace from an Epic asset or compatible record.</summary>
    public static string? ReadNamespace(JsonElement element) =>
        ReadString(element, "namespace");

    private static readonly string[] CoverImageTypes =
    [
        "DieselGameBox",
        "DieselGameBoxTall",
        "OfferImageTall",
        "Thumbnail"
    ];

    private static readonly string[] KeyArtImageTypes =
    [
        "DieselGameBoxWide",
        "DieselStoreFrontWide",
        "OfferImageWide"
    ];

    private static string? ReadDeveloper(JsonElement catalogMetadata) =>
        ReadString(catalogMetadata, "developerDisplayName") ??
        ReadString(catalogMetadata, "developer");

    private static string? ReadPublisher(JsonElement catalogMetadata) =>
        ReadString(catalogMetadata, "publisherDisplayName") ??
        ReadString(catalogMetadata, "publisher") ??
        ReadNestedString(catalogMetadata, "seller", "name");

    private static string? ReadReleaseDate(JsonElement catalogMetadata) =>
        ReadString(catalogMetadata, "releaseDate") ??
        ReadString(catalogMetadata, "effectiveDate") ??
        ReadFirstReleaseInfoDate(catalogMetadata);

    private static string? ReadFirstReleaseInfoDate(JsonElement catalogMetadata)
    {
        if (!catalogMetadata.TryGetProperty("releaseInfo", out var releaseInfo)
            || releaseInfo.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var release in releaseInfo.EnumerateArray())
        {
            var date = ReadString(release, "date");
            if (date is not null)
            {
                return date;
            }
        }

        return null;
    }

    private static bool ShouldSkip(JsonElement asset, JsonElement catalogMetadata)
    {
        var namespaceName = ReadNamespace(asset) ?? ReadString(catalogMetadata, "namespace");
        if (string.Equals(namespaceName, "ue", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (catalogMetadata.TryGetProperty("mainGameItem", out _))
        {
            return true;
        }

        if (TryGetStringProperty(catalogMetadata, "sandboxType", out var sandboxType)
            && string.Equals(sandboxType, "PRIVATE", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var hasGameCategory = false;
        foreach (var path in ReadCategoryPaths(catalogMetadata))
        {
            if (path.Equals("mods", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)
                || path.Equals("addons", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("addons/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (path.Equals("games", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("games/", StringComparison.OrdinalIgnoreCase))
            {
                hasGameCategory = true;
            }
        }

        return !hasGameCategory;
    }

    private static IEnumerable<string> ReadCategoryPaths(JsonElement catalogMetadata)
    {
        if (!catalogMetadata.TryGetProperty("categories", out var categories)
            || categories.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var category in categories.EnumerateArray())
        {
            if (TryGetStringProperty(category, "path", out var path))
            {
                yield return path;
            }
        }
    }

    private static string? ReadKeyImageUrl(JsonElement catalogMetadata, IReadOnlyList<string> imageTypes)
    {
        if (!catalogMetadata.TryGetProperty("keyImages", out var keyImages)
            || keyImages.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var imageType in imageTypes)
        {
            foreach (var image in keyImages.EnumerateArray())
            {
                if (TryGetStringProperty(image, "type", out var type)
                    && string.Equals(type, imageType, StringComparison.OrdinalIgnoreCase)
                    && TryGetStringProperty(image, "url", out var url))
                {
                    return url;
                }
            }
        }

        return null;
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

    private static string? ReadNestedString(JsonElement element, string property, string nestedProperty)
    {
        if (!element.TryGetProperty(property, out var nested)
            || nested.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return ReadString(nested, nestedProperty);
    }

    private static bool TryGetStringProperty(JsonElement element, string property, out string value)
    {
        value = string.Empty;
        var text = ReadString(element, property);
        if (text is null)
        {
            return false;
        }

        value = text;
        return true;
    }

    private static void WriteOptionalString(Utf8JsonWriter writer, string property, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            writer.WriteString(property, value);
        }
    }

    private static void WriteOptionalStringArray(
        Utf8JsonWriter writer,
        string property,
        IEnumerable<string> values)
    {
        var stableValues = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (stableValues.Length == 0)
        {
            return;
        }

        writer.WritePropertyName(property);
        writer.WriteStartArray();
        foreach (var value in stableValues)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }
}
