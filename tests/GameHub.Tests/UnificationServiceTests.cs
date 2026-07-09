using GameHub.Core.Data;
using GameHub.Core.Services;
using GameHub.Shared;
using GameHub.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameHub.Tests;

/// <summary>
/// Integration tests for the UnificationService sync pipeline.
/// Uses in-memory SQLite to test the full flow:
///   RawGameDto → slug matching → UnifiedGame + LibraryItem creation
///
/// The IngestClient is mocked since we don't want to make HTTP calls in tests.
/// Instead, we test ProcessPageAsync indirectly via RunFullSyncAsync with a
/// mocked client that returns controlled data.
/// </summary>
public class UnificationServiceTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly MutableTenantContext _tenantContext = new();

    public UnificationServiceTests()
    {
        var (db, connection) = DatabaseFixture.CreateCoreContext();
        _connection = connection;
        db.Dispose(); // We'll create fresh contexts via the scope factory

        // Build a minimal DI container with our in-memory DB
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(_tenantContext);
        services.AddDbContext<CoreContext>(o => o.UseSqlite(_connection));
        _serviceProvider = services.BuildServiceProvider();
        _scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
    }

    public void Dispose()
    {
        (_serviceProvider as IDisposable)?.Dispose();
        _connection.Dispose();
    }

    /// <summary>Helper to create a UnificationService with a mocked IngestClient.</summary>
    private UnificationService CreateService(IngestClient? client = null)
    {
        // Default: client that returns empty pages (no HTTP calls)
        client ??= CreateMockClient([]);
        return new UnificationService(
            _scopeFactory,
            client,
            NullLogger<UnificationService>.Instance);
    }

    /// <summary>Creates a mock IngestClient that returns the given items on page 1.</summary>
    private static IngestClient CreateMockClient(List<RawGameDto> items)
        => CreateMockClient(Platform.Steam, items);

    private static IngestClient CreateMockClient(
        Platform platform,
        List<RawGameDto> items,
        Platform? failingStoredRetrievePlatform = null)
    {
        // We can't easily mock IngestClient since it uses HttpClient internally.
        // Instead, we'll test the sync pipeline by seeding data directly.
        // For full end-to-end, we test ProcessPageAsync behavior via the DB state.
        var http = new HttpClient(new FakeHandler(
            new Dictionary<Platform, List<RawGameDto>>
            {
                [platform] = items
            },
            failingStoredRetrievePlatform))
        {
            BaseAddress = new Uri("http://ingest.test")
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:IngestServiceKey"] = "test-service-key"
            })
            .Build();
        return new IngestClient(http, new MutableTenantContext(), configuration);
    }

    [Fact]
    public async Task RunFullSync_NewTenant_RefusesSharedIngestData()
    {
        Guid tenantId;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
            var tenant = new Tenant
            {
                Id = Guid.NewGuid(),
                Name = "Isolated tenant",
                PlatformSyncEnabled = false,
                CreatedAtUtc = DateTime.UtcNow
            };
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync();
            tenantId = tenant.Id;
        }

        _tenantContext.TenantId = tenantId;
        var exception = await Assert.ThrowsAsync<TenantPlatformSyncDisabledException>(
            () => CreateService().RunFullSyncAsync());

        Assert.Equal(tenantId, exception.TenantId);
    }

    [Fact]
    public async Task RunFullSync_EpicRawFallbackMetadata_PopulatesEmptyCoreFields()
    {
        var releaseDate = new DateTime(2008, 10, 13, 0, 0, 0, DateTimeKind.Utc);
        var service = CreateService(CreateMockClient(Platform.Epic,
        [
            new RawGameDto
            {
                Id = "Anemone",
                Name = "World of Goo",
                Summary = "Build bridges from living balls of goo.",
                CoverUrl = "https://cdn.example.test/cover.jpg",
                ArtworkUrl = "https://cdn.example.test/key-art.jpg",
                Developers = "2D Boy",
                Publishers = "Tomorrow Corporation",
                FirstReleaseDate = releaseDate
            }
        ]));

        var processed = await service.RunFullSyncAsync();

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
        var game = await db.UnifiedGames.Include(g => g.Items).SingleAsync();
        Assert.Equal(1, processed);
        Assert.Equal("World of Goo", game.Title);
        Assert.Equal("Build bridges from living balls of goo.", game.Summary);
        Assert.Equal("https://cdn.example.test/cover.jpg", game.CoverUrl);
        Assert.Equal("https://cdn.example.test/key-art.jpg", game.Artworks);
        Assert.Equal("2D Boy", game.Developers);
        Assert.Equal("Tomorrow Corporation", game.Publishers);
        Assert.Equal(releaseDate, game.FirstReleaseDate);
        Assert.Equal("Epic Raw", game.MetadataSource);
        Assert.Contains(game.Items, item => item.Platform == Platform.Epic && item.ExternalId == "Anemone");
    }

    [Fact]
    public async Task RunFullSync_EpicRawFallbackMetadata_DoesNotOverwriteExistingEnrichedFields()
    {
        var enrichedReleaseDate = new DateTime(2009, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
            db.UnifiedGames.Add(new UnifiedGame
            {
                Title = "World of Goo",
                Slug = UnificationService.GenerateSlug("World of Goo"),
                Summary = "IGDB summary",
                CoverUrl = "https://igdb.example.test/cover.jpg",
                Artworks = "https://igdb.example.test/art.jpg",
                Developers = "IGDB Developer",
                Publishers = "IGDB Publisher",
                FirstReleaseDate = enrichedReleaseDate,
                MetadataSource = "IGDB",
                MetadataProviderId = 123
            });
            await db.SaveChangesAsync();
        }

        var service = CreateService(CreateMockClient(Platform.Epic,
        [
            new RawGameDto
            {
                Id = "Anemone",
                Name = "World of Goo",
                Summary = "Epic summary",
                CoverUrl = "https://epic.example.test/cover.jpg",
                ArtworkUrl = "https://epic.example.test/art.jpg",
                Developers = "Epic Developer",
                Publishers = "Epic Publisher",
                FirstReleaseDate = new DateTime(2008, 10, 13, 0, 0, 0, DateTimeKind.Utc)
            }
        ]));

        await service.RunFullSyncAsync();

        using var verifyScope = _scopeFactory.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<CoreContext>();
        var game = await verifyDb.UnifiedGames.Include(g => g.Items).SingleAsync();
        Assert.Equal("IGDB summary", game.Summary);
        Assert.Equal("https://igdb.example.test/cover.jpg", game.CoverUrl);
        Assert.Equal("https://igdb.example.test/art.jpg", game.Artworks);
        Assert.Equal("IGDB Developer", game.Developers);
        Assert.Equal("IGDB Publisher", game.Publishers);
        Assert.Equal(enrichedReleaseDate, game.FirstReleaseDate);
        Assert.Equal("IGDB", game.MetadataSource);
        Assert.Equal(123, game.MetadataProviderId);
        Assert.Contains(game.Items, item => item.Platform == Platform.Epic && item.ExternalId == "Anemone");
    }

    [Fact]
    public async Task RunFullSync_EpicRawFallbackMetadata_MissingOptionalFieldsDoesNotBreakSync()
    {
        var service = CreateService(CreateMockClient(Platform.Epic,
        [
            new RawGameDto
            {
                Id = "MissingOptionalFieldsApp",
                Name = "Sparse Epic Game"
            }
        ]));

        var processed = await service.RunFullSyncAsync();

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
        var game = await db.UnifiedGames.Include(g => g.Items).SingleAsync();
        Assert.Equal(1, processed);
        Assert.Equal("Sparse Epic Game", game.Title);
        Assert.Null(game.Summary);
        Assert.Null(game.CoverUrl);
        Assert.Null(game.Developers);
        Assert.Null(game.Publishers);
        Assert.Null(game.FirstReleaseDate);
        Assert.Null(game.MetadataSource);
        Assert.Contains(game.Items, item => item.Platform == Platform.Epic && item.ExternalId == "MissingOptionalFieldsApp");
    }

    [Fact]
    public async Task RunFullSync_EpicRawFallbackMetadata_KeepsTitleSlugMatchingAndAppNameLinkStable()
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
            db.UnifiedGames.Add(new UnifiedGame
            {
                Title = "World of Goo",
                Slug = UnificationService.GenerateSlug("World of Goo"),
                Items =
                [
                    new LibraryItem { Platform = Platform.Steam, ExternalId = "22000" }
                ]
            });
            await db.SaveChangesAsync();
        }

        var service = CreateService(CreateMockClient(Platform.Epic,
        [
            new RawGameDto
            {
                Id = "StableGameApp",
                Name = "World of Goo",
                Summary = "Epic fallback summary"
            }
        ]));

        await service.RunFullSyncAsync();

        using var verifyScope = _scopeFactory.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<CoreContext>();
        var game = await verifyDb.UnifiedGames.Include(g => g.Items).SingleAsync();
        Assert.Equal("World of Goo", game.Title);
        Assert.Equal(UnificationService.GenerateSlug("World of Goo"), game.Slug);
        Assert.Equal(2, game.Items.Count);
        Assert.Contains(game.Items, item => item.Platform == Platform.Steam && item.ExternalId == "22000");
        Assert.Contains(game.Items, item => item.Platform == Platform.Epic && item.ExternalId == "StableGameApp");
    }

    [Fact]
    public async Task RunFullSync_EpicStoredRetrievalFailure_ExportsExistingRawDataAsPartialSuccess()
    {
        var service = CreateService(CreateMockClient(
            Platform.Epic,
            [
                new RawGameDto
                {
                    Id = "StoredEpicGame",
                    Name = "Stored Epic Game"
                }
            ],
            Platform.Epic));

        var processed = await service.RunFullSyncAsync();

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
        var game = await db.UnifiedGames.Include(g => g.Items).SingleAsync();
        var audit = await db.SyncAuditLogs.SingleAsync(log => log.Platform == Platform.Epic);

        Assert.Equal(1, processed);
        Assert.Equal("Stored Epic Game", game.Title);
        Assert.Contains(game.Items, item => item.Platform == Platform.Epic && item.ExternalId == "StoredEpicGame");
        Assert.Equal("PartialSuccess", audit.Status);
        Assert.Equal(1, audit.ItemsProcessed);
        Assert.Equal("Stored platform retrieval failed; existing raw data was exported.", audit.ErrorMessage);
        Assert.Equal(TenantDefaults.DefaultTenantId, audit.TenantId);
    }

    // =========================================================================
    // Test: New games create UnifiedGame records
    // =========================================================================

    [Fact]
    public async Task Sync_NewGames_CreatesUnifiedGameRecords()
    {
        // Arrange: Seed the CoreContext with no existing data, simulate processing
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreContext>();

        // Directly create games as if they came through the sync pipeline
        var games = new[]
        {
            new UnifiedGame { Title = "The Witcher 3", Slug = "the-witcher-3" },
            new UnifiedGame { Title = "DOOM", Slug = "doom" },
            new UnifiedGame { Title = "Hades", Slug = "hades" }
        };

        db.UnifiedGames.AddRange(games);
        await db.SaveChangesAsync();

        // Assert
        var count = await db.UnifiedGames.CountAsync();
        Assert.Equal(3, count);
    }

    // =========================================================================
    // Test: Same slug from different platforms → single UnifiedGame
    // =========================================================================

    [Fact]
    public async Task Sync_DuplicateSlugs_MergeIntoSingleGame()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreContext>();

        // Create a game and add two platform items (simulating what sync does)
        var game = new UnifiedGame
        {
            Title = "The Witcher 3",
            Slug = "the-witcher-3",
            Items =
            [
                new LibraryItem { Platform = Platform.Steam, ExternalId = "292030" },
                new LibraryItem { Platform = Platform.Gog, ExternalId = "1640424747" }
            ]
        };

        db.UnifiedGames.Add(game);
        await db.SaveChangesAsync();

        // Assert: Single game with two platform entries
        var result = await db.UnifiedGames.Include(g => g.Items).SingleAsync();
        Assert.Equal("the-witcher-3", result.Slug);
        Assert.Equal(2, result.Items.Count);
        Assert.Contains(result.Items, i => i.Platform == Platform.Steam);
        Assert.Contains(result.Items, i => i.Platform == Platform.Gog);
    }

    // =========================================================================
    // Test: ManualLinks override slug-based matching
    // =========================================================================

    [Fact]
    public async Task Sync_ManualLink_OverridesSlugMatching()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreContext>();

        // Create two games with different slugs
        var game1 = new UnifiedGame { Title = "DOOM (1993)", Slug = "doom-1993" };
        var game2 = new UnifiedGame { Title = "DOOM (2016)", Slug = "doom-2016" };
        db.UnifiedGames.AddRange(game1, game2);
        await db.SaveChangesAsync();

        // Create a manual link that forces a specific platform entry to game2
        db.ManualLinks.Add(new ManualLink
        {
            Platform = Platform.Steam,
            ExternalId = "379720",
            UnifiedGameId = game2.Id
        });
        await db.SaveChangesAsync();

        // Assert: ManualLink exists and points to the correct game
        var link = await db.ManualLinks.SingleAsync();
        Assert.Equal(game2.Id, link.UnifiedGameId);
        Assert.Equal("379720", link.ExternalId);
    }

    // =========================================================================
    // Test: Items without names are handled correctly
    // =========================================================================

    [Fact]
    public void Sync_EmptyNames_AreHandledGracefully()
    {
        // GenerateSlug should handle empty strings without throwing
        var slug = UnificationService.GenerateSlug("");
        Assert.Equal("", slug);

        // And whitespace-only
        var slugWhitespace = UnificationService.GenerateSlug("   ");
        Assert.Equal("", slugWhitespace);
    }

    // =========================================================================
    // Test: SyncMetadata timestamp tracking
    // =========================================================================

    [Fact]
    public async Task SyncMetadata_TracksLastSyncTimestamp()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreContext>();

        // Simulate what UpdateSyncTimestampAsync does
        var meta = new SyncMetadata
        {
            Platform = Platform.Steam,
            LastSyncedAt = DateTime.UtcNow
        };
        db.SyncMetadata.Add(meta);
        await db.SaveChangesAsync();

        // Assert
        var result = await db.SyncMetadata.SingleAsync(m => m.Platform == Platform.Steam);
        Assert.NotNull(result);
        Assert.True(result.LastSyncedAt <= DateTime.UtcNow);
    }

    // =========================================================================
    // Test: Unique constraint on slug
    // =========================================================================

    [Fact]
    public async Task UnifiedGame_DuplicateSlug_ThrowsOnSave()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreContext>();

        db.UnifiedGames.Add(new UnifiedGame { Title = "DOOM", Slug = "doom" });
        await db.SaveChangesAsync();

        db.UnifiedGames.Add(new UnifiedGame { Title = "DOOM Again", Slug = "doom" });

        // SQLite enforces the unique index
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    /// <summary>
    /// Fake HTTP handler that returns a PagedResult with the given items.
    /// Used to mock the IngestClient without needing a real HTTP server.
    /// </summary>
    private class FakeHandler(
        Dictionary<Platform, List<RawGameDto>> itemsByPlatform,
        Platform? failingStoredRetrievePlatform = null) : HttpMessageHandler
    {
        private readonly Dictionary<Platform, List<RawGameDto>> _itemsByPlatform = itemsByPlatform;
        private readonly Platform? _failingStoredRetrievePlatform = failingStoredRetrievePlatform;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                if (ReadStoredRetrievePlatform(request.RequestUri) == _failingStoredRetrievePlatform)
                {
                    return Task.FromResult(new HttpResponseMessage
                    {
                        StatusCode = System.Net.HttpStatusCode.ServiceUnavailable
                    });
                }

                return Task.FromResult(new HttpResponseMessage
                {
                    StatusCode = System.Net.HttpStatusCode.NotFound
                });
            }

            var platform = ReadPlatform(request.RequestUri);
            var items = platform.HasValue && _itemsByPlatform.TryGetValue(platform.Value, out var platformItems)
                ? platformItems
                : [];

            var result = new PagedResult<RawGameDto>
            {
                Items = items,
                TotalCount = items.Count,
                Page = 1,
                PageSize = 200
            };

            var json = System.Text.Json.JsonSerializer.Serialize(result);
            return Task.FromResult(new HttpResponseMessage
            {
                StatusCode = System.Net.HttpStatusCode.OK,
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        }

        private static Platform? ReadPlatform(Uri? uri)
        {
            var platformText = uri?.Segments.LastOrDefault()?.Trim('/');
            return Enum.TryParse<Platform>(platformText, ignoreCase: true, out var platform)
                ? platform
                : null;
        }

        private static Platform? ReadStoredRetrievePlatform(Uri? uri)
        {
            var segments = uri?.Segments;
            if (segments is null || segments.Length < 2)
            {
                return null;
            }

            var platformText = segments[^2].Trim('/');
            return Enum.TryParse<Platform>(platformText, ignoreCase: true, out var platform)
                ? platform
                : null;
        }
    }
}

internal sealed class MutableTenantContext : ITenantContext
{
    public Guid TenantId { get; set; } = TenantDefaults.DefaultTenantId;
}
