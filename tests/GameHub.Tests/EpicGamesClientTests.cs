using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using GameHub.Ingest.Data;
using GameHub.Ingest.Models;
using GameHub.Ingest.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameHub.Tests;

public sealed class EpicGamesClientTests
{
    [Fact]
    public async Task ExchangeAuthorizationCodeAsync_SendsLauncherUserAgentAndForm()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "access_token": "epic-access-token",
                  "refresh_token": "epic-refresh-token",
                  "account_id": "account-1",
                  "displayName": "Player"
                }
                """,
                Encoding.UTF8,
                "application/json")
        });
        var client = CreateClient(handler);

        var credential = await client.ExchangeAuthorizationCodeAsync("short-lived-code");

        Assert.Equal("epic-refresh-token", credential.RefreshToken);
        Assert.NotNull(handler.LastRequest);
        Assert.Contains(
            "UELauncher/",
            handler.LastRequest.Headers.UserAgent.ToString(),
            StringComparison.Ordinal);
        Assert.Equal("Basic", handler.LastRequest.Headers.Authorization?.Scheme);
        Assert.Contains("grant_type=authorization_code", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("code=short-lived-code", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("token_type=eg1", handler.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExchangeAuthorizationCodeAsync_BadRequest_RaisesCredentialValidation()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                "{\"errorCode\":\"errors.com.epicgames.oauth.invalid_grant\"}",
                Encoding.UTF8,
                "application/json")
        });
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<PlatformCredentialValidationException>(
            () => client.ExchangeAuthorizationCodeAsync("short-lived-code"));

        Assert.Contains("invalid or expired", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchGamesAsync_RotatedRefreshToken_PersistsBeforeLibraryFetch()
    {
        using var fixture = new IngestScopeFixture();
        var rotationStore = new CapturingRefreshTokenRotationStore();
        var logger = new CapturingLogger<EpicGamesClient>();
        var handler = new RecordingHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return JsonResponse(
                    """
                    {
                      "access_token": "epic-access-token",
                      "refresh_token": "new-refresh-token",
                      "account_id": "account-2",
                      "displayName": "Renamed Player"
                    }
                    """);
            }

            return JsonResponse("[]");
        });
        var client = CreateClient(
            handler,
            fixture.ScopeFactory,
            rotationStore,
            logger);

        var games = await client.FetchGamesAsync(
            new EpicPlatformCredential("old-refresh-token", "account-1", "Player"));

        Assert.Empty(games.Games);
        Assert.True(games.IsComplete);
        Assert.Equal("old-refresh-token", rotationStore.CurrentCredential?.RefreshToken);
        Assert.Equal("new-refresh-token", rotationStore.RefreshedCredential?.RefreshToken);
        Assert.Equal("account-2", rotationStore.RefreshedCredential?.AccountId);
        Assert.Equal("Renamed Player", rotationStore.RefreshedCredential?.DisplayName);
        Assert.Equal(2, handler.RequestCount);
        Assert.Contains(
            logger.Messages,
            message => message.Contains(
                "Epic refresh-token rotation persistence completed",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task FetchGamesAsync_RotationPersistenceFailure_WritesSanitizedAuditAndStops()
    {
        using var fixture = new IngestScopeFixture();
        const string oldRefreshToken = "old-refresh-token-secret";
        const string newRefreshToken = "new-refresh-token-secret";
        var rotationStore = new FailingRefreshTokenRotationStore(new InvalidOperationException(newRefreshToken));
        var handler = new RecordingHandler(request => JsonResponse(
            $$"""
            {
              "access_token": "epic-access-token",
              "refresh_token": "{{newRefreshToken}}",
              "account_id": "account-2",
              "displayName": "Renamed Player"
            }
            """));
        var client = CreateClient(
            handler,
            fixture.ScopeFactory,
            rotationStore);

        var exception = await Assert.ThrowsAsync<PlatformCredentialRotationException>(
            () => client.FetchGamesAsync(new EpicPlatformCredential(oldRefreshToken)));
        var audit = await fixture.ReadSingleAuditLogAsync();

        Assert.Equal("Epic refresh token rotation could not be persisted.", exception.Message);
        Assert.Equal("Error", audit.Status);
        Assert.Equal("Epic refresh token rotation could not be persisted.", audit.ErrorMessage);
        Assert.DoesNotContain(oldRefreshToken, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(newRefreshToken, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(oldRefreshToken, audit.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(newRefreshToken, audit.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task FetchGamesAsync_UnavailableCatalogMetadata_SkipsOnlyThatRecordAndWritesPartialSuccess()
    {
        using var fixture = new IngestScopeFixture();
        var handler = new RecordingHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return JsonResponse("""
                {
                  "access_token": "epic-access-token",
                  "refresh_token": "epic-refresh-token"
                }
                """);
            }

            if (request.RequestUri?.AbsolutePath.Contains("/launcher/api/public/assets/", StringComparison.Ordinal) == true)
            {
                return JsonResponse("""
                [
                  { "appName": "UnavailableCatalog", "namespace": "catalog-one", "catalogItemId": "catalog-one" },
                  { "appName": "AvailableCatalog", "namespace": "catalog-two", "catalogItemId": "catalog-two" }
                ]
                """);
            }

            if (request.RequestUri?.Query.Contains("id=catalog-one", StringComparison.Ordinal) == true)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            if (request.RequestUri?.Query.Contains("id=catalog-two", StringComparison.Ordinal) == true)
            {
                return JsonResponse("""
                {
                  "catalog-two": {
                    "title": "Available Catalog Game",
                    "categories": [{ "path": "games" }]
                  }
                }
                """);
            }

            throw new InvalidOperationException("Unexpected Epic request in test.");
        });
        var client = CreateClient(handler, fixture.ScopeFactory);

        var games = await client.FetchGamesAsync(new EpicPlatformCredential("epic-refresh-token"));
        var audit = await fixture.ReadSingleAuditLogAsync();

        var game = Assert.Single(games.Games);
        Assert.False(games.IsComplete);
        Assert.Equal("AvailableCatalog", game.GetProperty("app_name").GetString());
        Assert.Equal("PartialSuccess", audit.Status);
        Assert.Equal(1, audit.GamesFound);
        Assert.Equal(
            "Some Epic catalog metadata records were unavailable or malformed and were skipped.",
            audit.ErrorMessage);
        Assert.Equal(IngestScopeFixture.TenantId, audit.TenantId);
        Assert.DoesNotContain("epic-refresh-token", audit.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(4, handler.RequestCount);
    }

    [Fact]
    public async Task FetchGamesAsync_SameNamespaceCatalogItems_UsesSingleBatchRequest()
    {
        using var fixture = new IngestScopeFixture();
        var logger = new CapturingLogger<EpicGamesClient>();
        var handler = CreateLibraryHandler(
            """
            [
              { "appName": "BatchGameOne", "namespace": "shared-namespace", "catalogItemId": "catalog-one" },
              { "appName": "BatchGameTwo", "namespace": "shared-namespace", "catalogItemId": "catalog-two" },
              { "appName": "BatchGameThree", "namespace": "shared-namespace", "catalogItemId": "catalog-three" }
            ]
            """);
        var client = CreateClient(handler, fixture.ScopeFactory, logger: logger);

        var games = await client.FetchGamesAsync(new EpicPlatformCredential("epic-refresh-token"));

        Assert.Equal(3, games.Games.Count);
        var catalogRequest = Assert.Single(handler.CatalogRequestUris);
        Assert.Equal(
            new[] { "catalog-one", "catalog-two", "catalog-three" },
            ReadCatalogIds(catalogRequest));
        Assert.Contains("includeDLCDetails=true", catalogRequest.Query, StringComparison.Ordinal);
        Assert.Contains("includeMainGameDetails=true", catalogRequest.Query, StringComparison.Ordinal);
        var logs = string.Join("\n", logger.Messages);
        Assert.Contains("Epic OAuth refresh completed", logs, StringComparison.Ordinal);
        Assert.Contains("Epic launcher asset retrieval completed", logs, StringComparison.Ordinal);
        Assert.Contains("Epic catalog metadata retrieval completed", logs, StringComparison.Ordinal);
        Assert.Contains("Epic mapping completed", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("epic-refresh-token", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("catalog-one", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("shared-namespace", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("BatchGameOne", logs, StringComparison.Ordinal);
        Assert.DoesNotContain(IngestScopeFixture.TenantId.ToString(), logs, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", logs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchGamesAsync_DifferentNamespaces_UsesSeparateBatchRequests()
    {
        using var fixture = new IngestScopeFixture();
        var handler = CreateLibraryHandler(
            """
            [
              { "appName": "NamespaceGameOne", "namespace": "namespace-one", "catalogItemId": "catalog-one" },
              { "appName": "NamespaceGameTwo", "namespace": "namespace-two", "catalogItemId": "catalog-two" }
            ]
            """);
        var client = CreateClient(handler, fixture.ScopeFactory);

        var games = await client.FetchGamesAsync(new EpicPlatformCredential("epic-refresh-token"));

        Assert.Equal(2, games.Games.Count);
        Assert.Equal(2, handler.CatalogRequestUris.Count);
        Assert.Contains(handler.CatalogRequestUris, uri => uri.AbsolutePath.Contains("/namespace-one/", StringComparison.Ordinal));
        Assert.Contains(handler.CatalogRequestUris, uri => uri.AbsolutePath.Contains("/namespace-two/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FetchGamesAsync_DuplicateCatalogPair_FetchesMetadataOnce()
    {
        using var fixture = new IngestScopeFixture();
        var handler = CreateLibraryHandler(
            """
            [
              { "appName": "DuplicatePairOne", "namespace": "shared-namespace", "catalogItemId": "shared-catalog" },
              { "appName": "DuplicatePairTwo", "namespace": "shared-namespace", "catalogItemId": "shared-catalog" }
            ]
            """);
        var client = CreateClient(handler, fixture.ScopeFactory);

        var games = await client.FetchGamesAsync(new EpicPlatformCredential("epic-refresh-token"));

        Assert.Equal(2, games.Games.Count);
        var catalogRequest = Assert.Single(handler.CatalogRequestUris);
        Assert.Equal(new[] { "shared-catalog" }, ReadCatalogIds(catalogRequest));
    }

    [Fact]
    public async Task FetchGamesAsync_ManyCatalogBatches_EnforcesMaximumConcurrency()
    {
        using var fixture = new IngestScopeFixture();
        var activeCatalogRequests = 0;
        var maximumCatalogRequests = 0;
        var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return OAuthResponse();
            }
            if (IsLauncherAssetsRequest(request))
            {
                return JsonResponse(BuildAssetsJson(65));
            }

            var active = Interlocked.Increment(ref activeCatalogRequests);
            UpdateMaximum(ref maximumCatalogRequests, active);
            try
            {
                await Task.Delay(40, cancellationToken);
                return CatalogResponse(request.RequestUri!);
            }
            finally
            {
                Interlocked.Decrement(ref activeCatalogRequests);
            }
        });
        var client = CreateClient(handler, fixture.ScopeFactory);

        var games = await client.FetchGamesAsync(new EpicPlatformCredential("epic-refresh-token"));

        Assert.Equal(65, games.Games.Count);
        Assert.Equal(4, handler.CatalogRequestUris.Count);
        Assert.All(handler.CatalogRequestUris, uri => Assert.InRange(ReadCatalogIds(uri).Length, 1, 20));
        Assert.InRange(maximumCatalogRequests, 2, 3);
    }

    [Fact]
    public async Task FetchGamesAsync_RateLimitedCatalogBatch_AbortsRemainingWorkAndKeepsLogsSecretFree()
    {
        using var fixture = new IngestScopeFixture();
        const string refreshToken = "rate-limit-refresh-token-secret";
        const string catalogId = "catalog-secret-id-000";
        const string namespaceName = "namespace-secret-value";
        var logger = new CapturingLogger<EpicGamesClient>();
        var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return OAuthResponse(refreshToken);
            }
            if (IsLauncherAssetsRequest(request))
            {
                return JsonResponse(BuildAssetsJson(100, namespaceName, "catalog-secret-id"));
            }
            if (request.RequestUri?.Query.Contains(catalogId, StringComparison.Ordinal) == true)
            {
                return RateLimitedResponse();
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Canceled catalog request unexpectedly resumed.");
        });
        var client = CreateClient(handler, fixture.ScopeFactory, logger: logger);

        var exception = await Assert.ThrowsAsync<ProviderRequestException>(
            () => client.FetchGamesAsync(new EpicPlatformCredential(refreshToken)));
        var audit = await fixture.ReadSingleAuditLogAsync();
        var logs = string.Join("\n", logger.Messages);

        Assert.Equal(ProviderRequestFailure.RateLimited, exception.Failure);
        Assert.Equal("RateLimited", audit.Status);
        Assert.InRange(handler.CatalogRequestUris.Count, 1, 3);
        Assert.DoesNotContain(refreshToken, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(catalogId, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(namespaceName, logs, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", logs, StringComparison.Ordinal);
        Assert.DoesNotContain(refreshToken, audit.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(catalogId, audit.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(namespaceName, audit.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", audit.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchGamesAsync_UnusableCatalogEntry_SkipsOnlyAffectedEntry()
    {
        using var fixture = new IngestScopeFixture();
        var handler = new RecordingHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return OAuthResponse();
            }
            if (IsLauncherAssetsRequest(request))
            {
                return JsonResponse(
                    """
                    [
                      { "appName": "UsableCatalog", "namespace": "shared-namespace", "catalogItemId": "catalog-usable" },
                      { "appName": "UnusableCatalog", "namespace": "shared-namespace", "catalogItemId": "catalog-unusable" }
                    ]
                    """);
            }

            return JsonResponse(
                """
                {
                  "catalog-usable": {
                    "title": "Usable Catalog Game",
                    "categories": [{ "path": "games" }]
                  },
                  "catalog-unusable": "unexpected-shape"
                }
                """);
        });
        var client = CreateClient(handler, fixture.ScopeFactory);

        var games = await client.FetchGamesAsync(new EpicPlatformCredential("epic-refresh-token"));
        var audit = await fixture.ReadSingleAuditLogAsync();

        Assert.Single(games.Games);
        Assert.False(games.IsComplete);
        Assert.Equal("PartialSuccess", audit.Status);
        Assert.Equal(1, audit.GamesFound);
        Assert.Single(handler.CatalogRequestUris);
    }

    [Fact]
    public async Task FetchGamesAsync_RateLimitedLibraryRequest_WritesSanitizedRateLimitAudit()
    {
        using var fixture = new IngestScopeFixture();
        const string refreshToken = "epic-refresh-token-secret";
        var handler = new RecordingHandler(request => request.Method == HttpMethod.Post
            ? JsonResponse("""
            {
              "access_token": "epic-access-token",
              "refresh_token": "epic-refresh-token-secret"
            }
            """)
            : RateLimitedResponse());
        var client = CreateClient(handler, fixture.ScopeFactory);

        var exception = await Assert.ThrowsAsync<ProviderRequestException>(
            () => client.FetchGamesAsync(new EpicPlatformCredential(refreshToken)));
        var audit = await fixture.ReadSingleAuditLogAsync();

        Assert.Equal("RateLimited", audit.Status);
        Assert.Equal(ProviderRequestFailure.RateLimited, exception.Failure);
        Assert.Equal("Provider rate limit exceeded. Retry after 2030-01-02T03:04:05.0000000+00:00.", audit.ErrorMessage);
        Assert.Equal(IngestScopeFixture.TenantId, audit.TenantId);
        Assert.DoesNotContain(refreshToken, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(refreshToken, audit.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchGamesAsync_MalformedLauncherResponse_WritesSanitizedAudit()
    {
        using var fixture = new IngestScopeFixture();
        const string refreshToken = "epic-refresh-token-secret";
        var handler = new RecordingHandler(request => request.Method == HttpMethod.Post
            ? JsonResponse("""
            {
              "access_token": "epic-access-token",
              "refresh_token": "epic-refresh-token-secret"
            }
            """)
            : JsonResponse("not-valid-json"));
        var client = CreateClient(handler, fixture.ScopeFactory);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.FetchGamesAsync(new EpicPlatformCredential(refreshToken)));
        var audit = await fixture.ReadSingleAuditLogAsync();

        Assert.Equal("MalformedResponse", audit.Status);
        Assert.Equal("Epic provider returned an unexpected response.", audit.ErrorMessage);
        Assert.Equal(IngestScopeFixture.TenantId, audit.TenantId);
        Assert.DoesNotContain(refreshToken, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(refreshToken, audit.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void MapToIngestItem_ProviderContractFixtures_PreserveCompatibleRecordsAndSkipUnsupportedRecords()
    {
        using var fixture = LoadEpicContractFixture();
        foreach (var contractCase in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            var name = contractCase.GetProperty("name").GetString();
            var mapped = EpicLibraryItemMapper.MapToIngestItem(
                contractCase.GetProperty("asset"),
                contractCase.GetProperty("catalogMetadata"));
            var expected = contractCase.GetProperty("expected");

            if (!expected.GetProperty("isMapped").GetBoolean())
            {
                Assert.Null(mapped);
                continue;
            }

            Assert.True(mapped.HasValue, $"Expected contract fixture '{name}' to map.");
            Assert.Equal(expected.GetProperty("appName").GetString(), mapped.Value.GetProperty("app_name").GetString());
            Assert.Equal(expected.GetProperty("title").GetString(), mapped.Value.GetProperty("app_title").GetString());

            foreach (var property in expected.GetProperty("presentProperties").EnumerateArray())
            {
                var propertyName = property.GetString();
                Assert.False(string.IsNullOrWhiteSpace(propertyName), $"Fixture '{name}' has an empty property name.");
                Assert.True(
                    mapped.Value.TryGetProperty(propertyName!, out _),
                    $"Expected contract fixture '{name}' to include '{propertyName}'.");
            }

            foreach (var property in expected.GetProperty("absentProperties").EnumerateArray())
            {
                var propertyName = property.GetString();
                Assert.False(string.IsNullOrWhiteSpace(propertyName), $"Fixture '{name}' has an empty property name.");
                Assert.False(
                    mapped.Value.TryGetProperty(propertyName!, out _),
                    $"Expected contract fixture '{name}' to omit '{propertyName}'.");
            }
        }
    }

    [Fact]
    public void MapToIngestItem_LauncherAssetWithCatalogMetadata_UsesAppNameAndTitle()
    {
        using var asset = JsonDocument.Parse("""
        {
          "appName": "Anemone",
          "assetId": "asset-1",
          "namespace": "anemone",
          "catalogItemId": "catalog-1"
        }
        """);
        using var metadata = JsonDocument.Parse("""
        {
          "id": "catalog-1",
          "namespace": "anemone",
          "title": "World of Goo",
          "developerDisplayName": "2D Boy",
          "publisherDisplayName": "Tomorrow Corporation",
          "releaseInfo": [{ "date": "2008-10-13T00:00:00.000Z" }],
          "description": "A physics puzzle game about building with balls of goo.",
          "shortDescription": "Build strange towers with goo balls.",
          "categories": [{ "path": "games" }, { "path": "games/puzzle" }],
          "keyImages": [
            { "type": "DieselGameBox", "url": "https://cdn.example.test/world-of-goo-cover.jpg" },
            { "type": "DieselGameBoxWide", "url": "https://cdn.example.test/world-of-goo-key-art.jpg" }
          ]
        }
        """);

        var mapped = EpicLibraryItemMapper.MapToIngestItem(asset.RootElement, metadata.RootElement);

        Assert.NotNull(mapped);
        Assert.Equal("Anemone", mapped.Value.GetProperty("app_name").GetString());
        Assert.Equal("World of Goo", mapped.Value.GetProperty("app_title").GetString());
        Assert.Equal("anemone", mapped.Value.GetProperty("namespace").GetString());
        Assert.Equal("catalog-1", mapped.Value.GetProperty("catalog_item_id").GetString());
        Assert.Equal("asset-1", mapped.Value.GetProperty("asset_id").GetString());
        Assert.Equal("2D Boy", mapped.Value.GetProperty("developer").GetString());
        Assert.Equal("Tomorrow Corporation", mapped.Value.GetProperty("publisher").GetString());
        Assert.Equal("2008-10-13T00:00:00.000Z", mapped.Value.GetProperty("release_date").GetString());
        Assert.Equal(
            "A physics puzzle game about building with balls of goo.",
            mapped.Value.GetProperty("description").GetString());
        Assert.Equal("Build strange towers with goo balls.", mapped.Value.GetProperty("summary").GetString());
        var categories = mapped.Value.GetProperty("categories").EnumerateArray()
            .Select(category => category.GetString()!)
            .ToArray();
        Assert.Equal(new[] { "games", "games/puzzle" }, categories);
        Assert.Equal(
            "https://cdn.example.test/world-of-goo-cover.jpg",
            mapped.Value.GetProperty("cover_url").GetString());
        Assert.Equal(
            "https://cdn.example.test/world-of-goo-key-art.jpg",
            mapped.Value.GetProperty("key_art_url").GetString());
        Assert.True(mapped.Value.TryGetProperty("legendary_asset", out var originalAsset));
        Assert.True(mapped.Value.TryGetProperty("legendary_metadata", out var originalMetadata));
        Assert.Equal("catalog-1", originalAsset.GetProperty("catalogItemId").GetString());
        Assert.Equal("catalog-1", originalMetadata.GetProperty("id").GetString());
    }

    [Fact]
    public void MapToIngestItem_MissingOptionalMetadata_OmitsEmptyRawFields()
    {
        using var asset = JsonDocument.Parse("""
        {
          "appName": "MinimalGame",
          "assetId": "",
          "namespace": "minimal-game",
          "catalogItemId": "minimal-catalog"
        }
        """);
        using var metadata = JsonDocument.Parse("""
        {
          "id": "minimal-catalog",
          "namespace": "minimal-game",
          "title": "Minimal Game",
          "developerDisplayName": "",
          "publisherDisplayName": " ",
          "description": "",
          "shortDescription": " ",
          "categories": [{ "path": "games" }],
          "keyImages": [{ "type": "DieselGameBox", "url": "" }]
        }
        """);

        var mapped = EpicLibraryItemMapper.MapToIngestItem(asset.RootElement, metadata.RootElement);

        Assert.NotNull(mapped);
        Assert.False(mapped.Value.TryGetProperty("asset_id", out _));
        Assert.False(mapped.Value.TryGetProperty("developer", out _));
        Assert.False(mapped.Value.TryGetProperty("publisher", out _));
        Assert.False(mapped.Value.TryGetProperty("release_date", out _));
        Assert.False(mapped.Value.TryGetProperty("description", out _));
        Assert.False(mapped.Value.TryGetProperty("summary", out _));
        Assert.False(mapped.Value.TryGetProperty("cover_url", out _));
        Assert.False(mapped.Value.TryGetProperty("key_art_url", out _));
    }

    [Fact]
    public void MapToIngestItem_ProviderCredentialLikeMetadata_DoesNotPromoteSecrets()
    {
        using var asset = JsonDocument.Parse("""
        {
          "appName": "SecretSafeGame",
          "assetId": "asset-secret-safe",
          "namespace": "secret-safe",
          "catalogItemId": "secret-safe-catalog",
          "access_token": "asset-access-token"
        }
        """);
        using var metadata = JsonDocument.Parse("""
        {
          "id": "secret-safe-catalog",
          "namespace": "secret-safe",
          "title": "Secret Safe Game",
          "categories": [{ "path": "games" }],
          "access_token": "metadata-access-token",
          "refresh_token": "metadata-refresh-token",
          "authorizationCode": "metadata-authorization-code",
          "client_secret": "metadata-client-secret",
          "cookie": "metadata-cookie"
        }
        """);

        var mapped = EpicLibraryItemMapper.MapToIngestItem(asset.RootElement, metadata.RootElement);

        Assert.NotNull(mapped);
        Assert.False(mapped.Value.TryGetProperty("access_token", out _));
        Assert.False(mapped.Value.TryGetProperty("refresh_token", out _));
        Assert.False(mapped.Value.TryGetProperty("authorizationCode", out _));
        Assert.False(mapped.Value.TryGetProperty("client_secret", out _));
        Assert.False(mapped.Value.TryGetProperty("cookie", out _));
    }

    [Fact]
    public void MapToIngestItem_DlcRecord_ReturnsNull()
    {
        using var asset = JsonDocument.Parse("""
        {
          "appName": "BaseGameDlc",
          "assetId": "asset-dlc",
          "namespace": "game",
          "catalogItemId": "catalog-dlc"
        }
        """);
        using var metadata = JsonDocument.Parse("""
        {
          "id": "catalog-dlc",
          "namespace": "game",
          "title": "Base Game DLC",
          "mainGameItem": { "id": "catalog-base" },
          "categories": [{ "path": "addons" }]
        }
        """);

        Assert.Null(EpicLibraryItemMapper.MapToIngestItem(asset.RootElement, metadata.RootElement));
    }

    [Theory]
    [InlineData("addons")]
    [InlineData("addons/launchable")]
    [InlineData("mods")]
    public void MapToIngestItem_AddonOrModRecord_ReturnsNull(string category)
    {
        using var asset = JsonDocument.Parse("""
        {
          "appName": "TechnicalRecord",
          "assetId": "asset-technical",
          "namespace": "game",
          "catalogItemId": "catalog-technical"
        }
        """);
        using var metadata = JsonDocument.Parse($$"""
        {
          "id": "catalog-technical",
          "namespace": "game",
          "title": "Technical Record",
          "categories": [{ "path": "{{category}}" }]
        }
        """);

        Assert.Null(EpicLibraryItemMapper.MapToIngestItem(asset.RootElement, metadata.RootElement));
    }

    [Fact]
    public void MapToIngestItem_UnrealMarketplaceRecord_ReturnsNull()
    {
        using var asset = JsonDocument.Parse("""
        {
          "appName": "MarketplacePack",
          "assetId": "asset-ue",
          "namespace": "ue",
          "catalogItemId": "catalog-ue"
        }
        """);
        using var metadata = JsonDocument.Parse("""
        {
          "id": "catalog-ue",
          "namespace": "ue",
          "title": "Marketplace Pack",
          "categories": [{ "path": "games" }]
        }
        """);

        Assert.Null(EpicLibraryItemMapper.MapToIngestItem(asset.RootElement, metadata.RootElement));
    }

    [Fact]
    public void MapToIngestItem_RecordWithoutGameCategory_ReturnsNull()
    {
        using var asset = JsonDocument.Parse("""
        {
          "appName": "UnknownRecord",
          "assetId": "asset-unknown",
          "namespace": "game",
          "catalogItemId": "catalog-unknown"
        }
        """);
        using var metadata = JsonDocument.Parse("""
        {
          "id": "catalog-unknown",
          "namespace": "game",
          "title": "Unknown Record",
          "categories": []
        }
        """);

        Assert.Null(EpicLibraryItemMapper.MapToIngestItem(asset.RootElement, metadata.RootElement));
    }

    [Fact]
    public void MapToIngestItem_VariantAssetForSameGame_UsesStableAppNameExternalId()
    {
        using var asset = JsonDocument.Parse("""
        {
          "appName": "StableGameApp",
          "assetId": "variant-asset",
          "namespace": "stable-game",
          "catalogItemId": "stable-catalog"
        }
        """);
        using var metadata = JsonDocument.Parse("""
        {
          "id": "stable-catalog",
          "namespace": "stable-game",
          "title": "Stable Game",
          "categories": [{ "path": "games/edition/base" }]
        }
        """);

        var mapped = EpicLibraryItemMapper.MapToIngestItem(asset.RootElement, metadata.RootElement);

        Assert.NotNull(mapped);
        Assert.Equal("StableGameApp", mapped.Value.GetProperty("app_name").GetString());
        Assert.Equal("stable-catalog", mapped.Value.GetProperty("catalog_item_id").GetString());
        Assert.Equal("variant-asset", mapped.Value.GetProperty("asset_id").GetString());
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new()
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage OAuthResponse(string refreshToken = "epic-refresh-token") =>
        JsonResponse(
            $$"""
            {
              "access_token": "epic-access-token",
              "refresh_token": "{{refreshToken}}"
            }
            """);

    private static RecordingHandler CreateLibraryHandler(string assetsJson) =>
        new(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return OAuthResponse();
            }
            if (IsLauncherAssetsRequest(request))
            {
                return JsonResponse(assetsJson);
            }

            return CatalogResponse(request.RequestUri!);
        });

    private static bool IsLauncherAssetsRequest(HttpRequestMessage request) =>
        request.RequestUri?.AbsolutePath.Contains(
            "/launcher/api/public/assets/",
            StringComparison.Ordinal) == true;

    private static HttpResponseMessage CatalogResponse(Uri requestUri)
    {
        var metadata = ReadCatalogIds(requestUri).ToDictionary(
            id => id,
            id => new
            {
                title = $"Game {id}",
                categories = new[] { new { path = "games" } }
            },
            StringComparer.Ordinal);
        return JsonResponse(JsonSerializer.Serialize(metadata));
    }

    private static string[] ReadCatalogIds(Uri requestUri) =>
        requestUri.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(part => part.Length == 2 && string.Equals(part[0], "id", StringComparison.Ordinal))
            .Select(part => Uri.UnescapeDataString(part[1]))
            .ToArray();

    private static string BuildAssetsJson(
        int count,
        string namespaceName = "shared-namespace",
        string catalogIdPrefix = "catalog") =>
        JsonSerializer.Serialize(Enumerable.Range(0, count).Select(index => new
        {
            appName = $"BatchGame{index:D3}",
            @namespace = namespaceName,
            catalogItemId = $"{catalogIdPrefix}-{index:D3}"
        }));

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        var observed = Volatile.Read(ref maximum);
        while (candidate > observed)
        {
            var prior = Interlocked.CompareExchange(ref maximum, candidate, observed);
            if (prior == observed)
            {
                return;
            }

            observed = prior;
        }
    }

    private static HttpResponseMessage RateLimitedResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
            new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero));
        return response;
    }

    private static JsonDocument LoadEpicContractFixture()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Epic",
            "epic-provider-contract-cases.json");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static EpicGamesClient CreateClient(
        RecordingHandler handler,
        IServiceScopeFactory? scopeFactory = null,
        IEpicRefreshTokenRotationStore? refreshTokenRotationStore = null,
        ILogger<EpicGamesClient>? logger = null)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return new EpicGamesClient(
            new HttpClient(handler),
            logger ?? NullLogger<EpicGamesClient>.Instance,
            scopeFactory ?? services.GetRequiredService<IServiceScopeFactory>(),
            refreshTokenRotationStore ?? new NoopRefreshTokenRotationStore());
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responseFactory;
        private readonly ConcurrentQueue<Uri> _requestUris = new();
        private int _requestCount;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
            : this((request, _) => Task.FromResult(responseFactory(request)))
        {
        }

        public RecordingHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public HttpRequestMessage? LastRequest { get; private set; }
        public string LastBody { get; private set; } = "";
        public int RequestCount => Volatile.Read(ref _requestCount);
        public IReadOnlyList<Uri> CatalogRequestUris => _requestUris
            .Where(uri => uri.AbsolutePath.Contains("/bulk/items", StringComparison.Ordinal))
            .ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            LastRequest = request;
            if (request.RequestUri is not null)
            {
                _requestUris.Enqueue(request.RequestUri);
            }
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return await _responseFactory(request, cancellationToken);
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Enqueue(formatter(state, exception));
    }

    private sealed class IngestScopeFixture : IDisposable
    {
        public static readonly Guid TenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly ServiceProvider _provider;

        public IngestScopeFixture()
        {
            _connection.Open();
            _provider = new ServiceCollection()
                .AddSingleton<IIngestTenantContext>(new FixedIngestTenantContext(TenantId))
                .AddDbContext<IngestContext>(options => options.UseSqlite(_connection))
                .BuildServiceProvider();

            using var scope = _provider.CreateScope();
            scope.ServiceProvider.GetRequiredService<IngestContext>().Database.EnsureCreated();
        }

        public IServiceScopeFactory ScopeFactory => _provider.GetRequiredService<IServiceScopeFactory>();

        public async Task<IngestAuditLog> ReadSingleAuditLogAsync()
        {
            using var scope = _provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IngestContext>();
            return await db.IngestAuditLogs.SingleAsync();
        }

        public void Dispose()
        {
            _provider.Dispose();
            _connection.Dispose();
        }
    }

    private sealed class FixedIngestTenantContext(Guid tenantId) : IIngestTenantContext
    {
        public Guid TenantId { get; } = tenantId;
    }

    private sealed class NoopRefreshTokenRotationStore : IEpicRefreshTokenRotationStore
    {
        public Task PersistIfRotatedAsync(
            EpicPlatformCredential currentCredential,
            EpicPlatformCredential refreshedCredential,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class CapturingRefreshTokenRotationStore : IEpicRefreshTokenRotationStore
    {
        public EpicPlatformCredential? CurrentCredential { get; private set; }
        public EpicPlatformCredential? RefreshedCredential { get; private set; }

        public Task PersistIfRotatedAsync(
            EpicPlatformCredential currentCredential,
            EpicPlatformCredential refreshedCredential,
            CancellationToken cancellationToken = default)
        {
            CurrentCredential = currentCredential;
            RefreshedCredential = refreshedCredential;
            return Task.CompletedTask;
        }
    }

    private sealed class FailingRefreshTokenRotationStore(Exception innerException)
        : IEpicRefreshTokenRotationStore
    {
        public Task PersistIfRotatedAsync(
            EpicPlatformCredential currentCredential,
            EpicPlatformCredential refreshedCredential,
            CancellationToken cancellationToken = default) =>
            throw new PlatformCredentialRotationException(innerException);
    }
}
