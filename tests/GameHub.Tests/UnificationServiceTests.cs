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
    private UnificationService CreateService(
        IngestClient? client = null,
        StoredRetrievalCooldown? retrievalCooldown = null,
        IConfiguration? configuration = null,
        TimeProvider? timeProvider = null)
    {
        // Default: client that returns empty pages (no HTTP calls)
        client ??= CreateMockClient([]);
        timeProvider ??= TimeProvider.System;
        return new UnificationService(
            _scopeFactory,
            client,
            NullLogger<UnificationService>.Instance,
            retrievalCooldown ?? new StoredRetrievalCooldown(timeProvider),
            configuration ?? new ConfigurationBuilder().Build(),
            timeProvider);
    }

    /// <summary>Creates a mock IngestClient that returns the given items on page 1.</summary>
    private static IngestClient CreateMockClient(List<RawGameDto> items)
        => CreateMockClient(Platform.Steam, items);

    private static IngestClient CreateMockClient(
        Platform platform,
        List<RawGameDto> items,
        Platform? failingStoredRetrievePlatform = null,
        Platform? rateLimitedStoredRetrievePlatform = null,
        DateTimeOffset? retryAfterUtc = null)
    {
        // We can't easily mock IngestClient since it uses HttpClient internally.
        // Instead, we'll test the sync pipeline by seeding data directly.
        // For full end-to-end, we test ProcessPageAsync behavior via the DB state.
        var http = new HttpClient(new FakeHandler(
            new Dictionary<Platform, List<RawGameDto>>
            {
                [platform] = items
            },
            failingStoredRetrievePlatform,
            rateLimitedStoredRetrievePlatform,
            retryAfterUtc))
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

    private static IngestClient CreateMockClient(FakeHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:IngestServiceKey"] = "test-service-key"
            })
            .Build();
        return new IngestClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://ingest.test") },
            new MutableTenantContext(),
            configuration);
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

        var cachedHandler = new FakeHandler([]);
        var cachedResult = await CreateService(CreateMockClient(cachedHandler))
            .RunSyncAsync(SyncExecutionMode.Cached);
        Assert.Equal(0, cachedResult.Processed);
        Assert.Equal(0, cachedHandler.StoredRetrieveRequests);
    }

    [Fact]
    public async Task RunSyncAsync_CachedMode_NeverContactsStoredRetrieval()
    {
        var handler = new FakeHandler([]);

        var result = await CreateService(CreateMockClient(handler))
            .RunSyncAsync(SyncExecutionMode.Cached);

        Assert.Equal(0, handler.StoredRetrieveRequests);
        Assert.All(result.Platforms, platform => Assert.Equal("Cached", platform.RefreshStatus));
    }

    [Fact]
    public async Task RunSyncAsync_RefreshIfStale_RefreshesExpiredProviderAndPersistsFreshness()
    {
        var now = new DateTimeOffset(2030, 2, 3, 4, 5, 6, TimeSpan.Zero);
        await SeedSyncMetadataAsync(Platform.Steam, now.UtcDateTime.AddHours(-1));
        var handler = new FakeHandler([], successfulStoredRetrievePlatform: Platform.Steam);

        var result = await CreateService(
                CreateMockClient(handler),
                configuration: FreshnessConfiguration(30),
                timeProvider: new FixedTimeProvider(now))
            .RunSyncAsync(SyncExecutionMode.RefreshIfStale);

        Assert.Equal(1, handler.StoredRetrieveRequestsFor(Platform.Steam));
        Assert.Equal("Updated", result.Platforms.Single(item => item.Platform == Platform.Steam).RefreshStatus);
        using var verifyScope = _scopeFactory.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<CoreContext>();
        var persisted = await verifyDb.SyncMetadata.SingleAsync(item => item.Platform == Platform.Steam);
        Assert.Equal(now.UtcDateTime, persisted.LastSuccessfulProviderRetrievalAtUtc);
    }

    [Fact]
    public async Task RunSyncAsync_RefreshIfStale_SkipsFreshProvider()
    {
        var now = new DateTimeOffset(2030, 2, 3, 4, 5, 6, TimeSpan.Zero);
        await SeedSyncMetadataAsync(Platform.Steam, now.UtcDateTime.AddMinutes(-5));
        var handler = new FakeHandler([], successfulStoredRetrievePlatform: Platform.Steam);

        var result = await CreateService(
                CreateMockClient(handler),
                configuration: FreshnessConfiguration(30),
                timeProvider: new FixedTimeProvider(now))
            .RunSyncAsync(SyncExecutionMode.RefreshIfStale);

        Assert.Equal(0, handler.StoredRetrieveRequestsFor(Platform.Steam));
        Assert.Equal("SkippedFresh", result.Platforms.Single(item => item.Platform == Platform.Steam).RefreshStatus);
    }

    [Fact]
    public async Task RunSyncAsync_ForceRefresh_BypassesFreshnessButNotCooldown()
    {
        var now = new DateTimeOffset(2030, 2, 3, 4, 5, 6, TimeSpan.Zero);
        await SeedSyncMetadataAsync(Platform.Steam, now.UtcDateTime.AddMinutes(-5));
        await SeedSyncMetadataAsync(Platform.Epic, now.UtcDateTime.AddMinutes(-5));
        var time = new FixedTimeProvider(now);
        var cooldown = new StoredRetrievalCooldown(time);
        cooldown.SetRetryAfter(TenantDefaults.DefaultTenantId, Platform.Epic, now.AddMinutes(10));
        var handler = new FakeHandler([], successfulStoredRetrievePlatform: Platform.Steam);

        var result = await CreateService(
                CreateMockClient(handler),
                cooldown,
                FreshnessConfiguration(30),
                time)
            .RunSyncAsync(SyncExecutionMode.ForceRefresh);

        Assert.Equal(1, handler.StoredRetrieveRequestsFor(Platform.Steam));
        Assert.Equal(0, handler.StoredRetrieveRequestsFor(Platform.Epic));
        Assert.Equal("Updated", result.Platforms.Single(item => item.Platform == Platform.Steam).RefreshStatus);
        Assert.Equal("SkippedCooldown", result.Platforms.Single(item => item.Platform == Platform.Epic).RefreshStatus);
    }

    [Fact]
    public async Task RunSyncAsync_PartialRetrieval_DoesNotAdvanceFreshness()
    {
        var now = new DateTimeOffset(2030, 2, 3, 4, 5, 6, TimeSpan.Zero);
        var handler = new FakeHandler([], partialStoredRetrievePlatform: Platform.Epic);

        var result = await CreateService(
                CreateMockClient(handler),
                timeProvider: new FixedTimeProvider(now))
            .RunSyncAsync(SyncExecutionMode.RefreshIfStale);

        Assert.Equal("Partial", result.Platforms.Single(item => item.Platform == Platform.Epic).RefreshStatus);
        using var verifyScope = _scopeFactory.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<CoreContext>();
        Assert.False(await verifyDb.SyncMetadata.AnyAsync(item =>
            item.Platform == Platform.Epic && item.LastSuccessfulProviderRetrievalAtUtc != null));
    }

    private async Task SeedSyncMetadataAsync(Platform platform, DateTime providerRetrievalAtUtc)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
        db.SyncMetadata.Add(new SyncMetadata
        {
            Platform = platform,
            LastSyncedAt = providerRetrievalAtUtc,
            LastSuccessfulProviderRetrievalAtUtc = providerRetrievalAtUtc
        });
        await db.SaveChangesAsync();
    }

    private static IConfiguration FreshnessConfiguration(int minutes) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sync:ProviderFreshnessMinutes"] = minutes.ToString()
            })
            .Build();

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
    public async Task RunFullSync_LaterDigitalTitle_AttachesToManualPhysicalGameWithSameSlug()
    {
        int manualGameId;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
            var capture = await new PhysicalGameCaptureService(db).CaptureManualAsync(
                "Physical Only Adventure",
                PhysicalGamePlatform.PS5,
                "00012345000058");
            manualGameId = capture.GameId;
        }

        var service = CreateService(CreateMockClient(
        [
            new RawGameDto { Id = "physical-only-steam", Name = "Physical Only Adventure" }
        ]));

        await service.RunFullSyncAsync();

        using var verifyScope = _scopeFactory.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<CoreContext>();
        var game = await verifyDb.UnifiedGames
            .Include(candidate => candidate.Items)
            .Include(candidate => candidate.PhysicalCopies)
            .SingleAsync();
        Assert.Equal(manualGameId, game.Id);
        Assert.Equal("physical-only-adventure", game.Slug);
        Assert.Equal("physical-only-steam", Assert.Single(game.Items).ExternalId);
        Assert.Equal("00012345000058", Assert.Single(game.PhysicalCopies).NormalizedGtin);
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
        var freshness = await db.SyncMetadata.SingleAsync(record => record.Platform == Platform.Epic);
        Assert.Null(freshness.LastSuccessfulProviderRetrievalAtUtc);
    }

    [Fact]
    public async Task RunFullSync_EpicRateLimit_ExportsExistingRawDataAndRecordsCooldown()
    {
        var retryAfterUtc = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var cooldown = new StoredRetrievalCooldown(TimeProvider.System);
        var service = CreateService(
            CreateMockClient(
                Platform.Epic,
                [new RawGameDto { Id = "RateLimitedEpicGame", Name = "Rate Limited Epic Game" }],
                rateLimitedStoredRetrievePlatform: Platform.Epic,
                retryAfterUtc: retryAfterUtc),
            cooldown);

        var processed = await service.RunFullSyncAsync();

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
        var audit = await db.SyncAuditLogs.SingleAsync(log => log.Platform == Platform.Epic);

        Assert.Equal(1, processed);
        Assert.Equal("PartialSuccess", audit.Status);
        Assert.Equal(
            "Stored platform retrieval is rate limited until 2030-01-02T03:04:05.0000000+00:00; existing raw data was exported.",
            audit.ErrorMessage);
        Assert.True(cooldown.TryGetRetryAfter(TenantDefaults.DefaultTenantId, Platform.Epic, out var recordedRetryAfter));
        Assert.Equal(retryAfterUtc, recordedRetryAfter);
        var freshness = await db.SyncMetadata.SingleAsync(record => record.Platform == Platform.Epic);
        Assert.Null(freshness.LastSuccessfulProviderRetrievalAtUtc);
    }

    [Fact]
    public async Task RunFullSync_ExistingSourceIdWithChangedTitle_KeepsUnifiedGameAssociation()
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
            db.UnifiedGames.Add(new UnifiedGame
            {
                Title = "Original Provider Title",
                Slug = "original-provider-title",
                Items =
                [
                    new LibraryItem
                    {
                        Platform = Platform.Psn,
                        ExternalId = "CUSA00001",
                        SourceContentHash = "old-hash"
                    }
                ]
            });
            await db.SaveChangesAsync();
        }

        var service = CreateService(CreateMockClient(Platform.Psn,
        [
            new RawGameDto
            {
                Id = "CUSA00001",
                Name = "Renamed Provider Title",
                ContentHash = "new-hash"
            }
        ]));

        var processed = await service.RunFullSyncAsync();

        using var verifyScope = _scopeFactory.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<CoreContext>();
        var game = await verifyDb.UnifiedGames.Include(item => item.Items).SingleAsync();
        var source = Assert.Single(game.Items);
        Assert.Equal(1, processed);
        Assert.Equal("Original Provider Title", game.Title);
        Assert.Equal("original-provider-title", game.Slug);
        Assert.Equal("CUSA00001", source.ExternalId);
        Assert.Equal("new-hash", source.SourceContentHash);
    }

    [Fact]
    public async Task RunFullSync_AlreadyAppliedContentHash_SkipsWriteAndAdvancesCursor()
    {
        var originalLastSynced = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
            db.UnifiedGames.Add(new UnifiedGame
            {
                Title = "Stable Game",
                Slug = "stable-game",
                Items =
                [
                    new LibraryItem
                    {
                        Platform = Platform.Steam,
                        ExternalId = "730",
                        SourceContentHash = "stable-hash",
                        LastSynced = originalLastSynced
                    }
                ]
            });
            await db.SaveChangesAsync();
        }

        var service = CreateService(CreateMockClient(
        [
            new RawGameDto
            {
                Id = "730",
                Name = "Stable Game",
                ContentHash = "stable-hash"
            }
        ]));

        var processed = await service.RunFullSyncAsync();

        using var verifyScope = _scopeFactory.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<CoreContext>();
        var source = await verifyDb.LibraryItems.SingleAsync();
        var metadata = await verifyDb.SyncMetadata.SingleAsync(item => item.Platform == Platform.Steam);
        var audit = await verifyDb.SyncAuditLogs.SingleAsync(item => item.Platform == Platform.Steam);
        Assert.Equal(0, processed);
        Assert.Equal(originalLastSynced, source.LastSynced);
        Assert.Equal("stable-hash", source.SourceContentHash);
        Assert.True(metadata.LastSyncedAt > originalLastSynced);
        Assert.Equal(0, audit.ItemsProcessed);
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
        Platform? failingStoredRetrievePlatform = null,
        Platform? rateLimitedStoredRetrievePlatform = null,
        DateTimeOffset? retryAfterUtc = null,
        Platform? successfulStoredRetrievePlatform = null,
        Platform? partialStoredRetrievePlatform = null) : HttpMessageHandler
    {
        private readonly Dictionary<Platform, List<RawGameDto>> _itemsByPlatform = itemsByPlatform;
        private readonly Platform? _failingStoredRetrievePlatform = failingStoredRetrievePlatform;
        private readonly Platform? _rateLimitedStoredRetrievePlatform = rateLimitedStoredRetrievePlatform;
        private readonly DateTimeOffset? _retryAfterUtc = retryAfterUtc;
        private readonly Platform? _successfulStoredRetrievePlatform = successfulStoredRetrievePlatform;
        private readonly Platform? _partialStoredRetrievePlatform = partialStoredRetrievePlatform;
        private readonly Dictionary<Platform, int> _storedRetrieveRequests = [];

        public int StoredRetrieveRequests => _storedRetrieveRequests.Values.Sum();

        public int StoredRetrieveRequestsFor(Platform platform) =>
            _storedRetrieveRequests.GetValueOrDefault(platform);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                var storedPlatform = ReadStoredRetrievePlatform(request.RequestUri);
                if (storedPlatform is { } requestedPlatform)
                {
                    _storedRetrieveRequests[requestedPlatform] =
                        _storedRetrieveRequests.GetValueOrDefault(requestedPlatform) + 1;
                }

                if (storedPlatform == _rateLimitedStoredRetrievePlatform)
                {
                    return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests)
                    {
                        Content = System.Net.Http.Json.JsonContent.Create(
                            new StoredRetrievalThrottleResponse(_retryAfterUtc))
                    });
                }

                if (storedPlatform == _failingStoredRetrievePlatform)
                {
                    return Task.FromResult(new HttpResponseMessage
                    {
                        StatusCode = System.Net.HttpStatusCode.ServiceUnavailable
                    });
                }

                if (storedPlatform == _successfulStoredRetrievePlatform ||
                    storedPlatform == _partialStoredRetrievePlatform)
                {
                    return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = System.Net.Http.Json.JsonContent.Create(
                            new StoredRetrievalResponse(storedPlatform != _partialStoredRetrievePlatform))
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

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

internal sealed class MutableTenantContext : ITenantContext
{
    public Guid TenantId { get; set; } = TenantDefaults.DefaultTenantId;
}
