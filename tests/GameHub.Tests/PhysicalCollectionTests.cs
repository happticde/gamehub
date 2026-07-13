using System.Reflection;
using GameHub.Core.Controllers;
using GameHub.Core.Data;
using GameHub.Core.Services;
using GameHub.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameHub.Tests;

/// <summary>Integration coverage for tenant-owned physical game copies.</summary>
public sealed class PhysicalCollectionTests : IDisposable
{
    private static readonly Guid TenantA = TenantDefaults.DefaultTenantId;
    private static readonly Guid TenantB = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid TenantC = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly DateTimeOffset FixedNow = new(2026, 7, 13, 20, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<CoreContext> _options;

    public PhysicalCollectionTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<CoreContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = CreateContext(TenantA);
        db.Database.EnsureCreated();
        db.Tenants.AddRange(
            new Tenant { Id = TenantB, Name = "Tenant B", CreatedAtUtc = FixedNow.UtcDateTime },
            new Tenant { Id = TenantC, Name = "Tenant C", CreatedAtUtc = FixedNow.UtcDateTime });
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task PhysicalCopies_CrudDetailAndFilter_AllowDuplicatePlatforms()
    {
        int gameId;
        await using (var db = CreateContext(TenantA))
        {
            var game = new UnifiedGame { Title = "Physical game", Slug = "physical-game" };
            db.UnifiedGames.Add(game);
            await db.SaveChangesAsync();
            gameId = game.Id;
        }

        await using var actionDb = CreateContext(TenantA);
        var controller = CreateController(actionDb, TenantA);
        var first = Assert.IsType<ObjectResult>(await controller.AddPhysicalCopy(
            gameId,
            new AddPhysicalGameCopyRequest { Platform = PhysicalGamePlatform.PS5 }));
        var second = Assert.IsType<ObjectResult>(await controller.AddPhysicalCopy(
            gameId,
            new AddPhysicalGameCopyRequest { Platform = PhysicalGamePlatform.PS5 }));
        await controller.AddPhysicalCopy(
            gameId,
            new AddPhysicalGameCopyRequest { Platform = PhysicalGamePlatform.PS3 });

        Assert.Equal(StatusCodes.Status201Created, first.StatusCode);
        Assert.Equal(StatusCodes.Status201Created, second.StatusCode);
        var firstCopyId = GetProperty<int>(first.Value!, "Id");
        Assert.NotEqual(firstCopyId, GetProperty<int>(second.Value!, "Id"));

        var listResult = Assert.IsType<OkObjectResult>(await controller.GetPhysicalCopies(gameId));
        Assert.Equal(3, Assert.IsAssignableFrom<IEnumerable<object>>(listResult.Value).Count());

        var detailResult = Assert.IsType<OkObjectResult>(await controller.GetById(gameId));
        Assert.Equal(
            3,
            GetProperty<IEnumerable<object>>(detailResult.Value!, "PhysicalCopies").Count());

        var libraryResult = Assert.IsType<OkObjectResult>(await controller.GetLibrary(
            null,
            null,
            null,
            [PhysicalGamePlatform.PS5],
            null,
            null,
            null,
            null));
        var page = Assert.IsType<PagedResult<object>>(libraryResult.Value);
        var listGame = Assert.Single(page.Items);
        Assert.Equal(3, GetProperty<IEnumerable<object>>(listGame, "PhysicalCopies").Count());

        Assert.IsType<NoContentResult>(await controller.RemovePhysicalCopy(gameId, firstCopyId));
        Assert.Equal(2, await actionDb.PhysicalGameCopies.CountAsync());
    }

    [Fact]
    public async Task PhysicalCopies_CrossTenantReadsWritesAndRelationships_AreRejected()
    {
        int tenantBGameId;
        int tenantBCopyId;
        await using (var tenantB = CreateContext(TenantB))
        {
            var game = new UnifiedGame
            {
                Title = "Tenant B physical game",
                Slug = "shared-physical-game",
                PhysicalCopies = [new PhysicalGameCopy { Platform = PhysicalGamePlatform.PS4 }]
            };
            tenantB.UnifiedGames.Add(game);
            await tenantB.SaveChangesAsync();
            tenantBGameId = game.Id;
            tenantBCopyId = Assert.Single(game.PhysicalCopies).Id;
        }

        int tenantAGameId;
        await using (var tenantA = CreateContext(TenantA))
        {
            var game = new UnifiedGame
            {
                Title = "Tenant A physical game",
                Slug = "shared-physical-game",
                PhysicalCopies = [new PhysicalGameCopy { Platform = PhysicalGamePlatform.PS4 }]
            };
            tenantA.UnifiedGames.Add(game);
            await tenantA.SaveChangesAsync();
            tenantAGameId = game.Id;

            var controller = CreateController(tenantA, TenantA);
            Assert.IsType<NotFoundObjectResult>(await controller.GetPhysicalCopies(tenantBGameId));
            Assert.IsType<NotFoundObjectResult>(await controller.RemovePhysicalCopy(tenantBGameId, tenantBCopyId));
            Assert.IsType<NotFoundObjectResult>(await controller.AddPhysicalCopy(
                tenantBGameId,
                new AddPhysicalGameCopyRequest { Platform = PhysicalGamePlatform.PS5 }));
        }

        await using (var tenantA = CreateContext(TenantA))
        {
            tenantA.PhysicalGameCopies.Add(new PhysicalGameCopy
            {
                UnifiedGameId = tenantBGameId,
                Platform = PhysicalGamePlatform.PS3
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => tenantA.SaveChangesAsync());
        }

        await using var verifyA = CreateContext(TenantA);
        await using var verifyB = CreateContext(TenantB);
        Assert.Equal(tenantAGameId, (await verifyA.PhysicalGameCopies.SingleAsync()).UnifiedGameId);
        Assert.Equal(tenantBCopyId, (await verifyB.PhysicalGameCopies.SingleAsync()).Id);
        Assert.Equal(2, await verifyA.PhysicalGameCopies.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task MergeGames_MovesAllPhysicalCopiesWithoutDeduplication()
    {
        int sourceId;
        int targetId;
        await using (var db = CreateContext(TenantA))
        {
            var source = new UnifiedGame
            {
                Title = "Source",
                Slug = "physical-merge-source",
                PhysicalCopies =
                [
                    new PhysicalGameCopy { Platform = PhysicalGamePlatform.PS5 },
                    new PhysicalGameCopy { Platform = PhysicalGamePlatform.PS5 }
                ]
            };
            var target = new UnifiedGame
            {
                Title = "Target",
                Slug = "physical-merge-target",
                PhysicalCopies = [new PhysicalGameCopy { Platform = PhysicalGamePlatform.PS5 }]
            };
            db.AddRange(source, target);
            await db.SaveChangesAsync();
            sourceId = source.Id;
            targetId = target.Id;
        }

        await using (var db = CreateContext(TenantA))
        {
            var result = await CreateController(db, TenantA).MergeGames(new MergeRequest
            {
                SourceGameId = sourceId,
                TargetGameId = targetId
            });
            Assert.IsType<OkObjectResult>(result);
        }

        await using var verify = CreateContext(TenantA);
        Assert.False(await verify.UnifiedGames.AnyAsync(game => game.Id == sourceId));
        var copies = await verify.PhysicalGameCopies.OrderBy(copy => copy.Id).ToListAsync();
        Assert.Equal(3, copies.Count);
        Assert.All(copies, copy => Assert.Equal(targetId, copy.UnifiedGameId));
    }

    [Fact]
    public async Task SplitGame_LeavesPhysicalCopiesOnOriginalGame()
    {
        int originalGameId;
        int itemToSplitId;
        int physicalCopyId;
        await using (var db = CreateContext(TenantA))
        {
            var game = new UnifiedGame
            {
                Title = "Combined",
                Slug = "physical-split-combined",
                Items =
                [
                    new LibraryItem { Platform = Platform.Steam, ExternalId = "steam" },
                    new LibraryItem { Platform = Platform.Gog, ExternalId = "gog" }
                ],
                PhysicalCopies = [new PhysicalGameCopy { Platform = PhysicalGamePlatform.PS3 }]
            };
            db.UnifiedGames.Add(game);
            await db.SaveChangesAsync();
            originalGameId = game.Id;
            itemToSplitId = game.Items.Single(item => item.Platform == Platform.Gog).Id;
            physicalCopyId = Assert.Single(game.PhysicalCopies).Id;
        }

        await using (var db = CreateContext(TenantA))
        {
            var result = await CreateController(db, TenantA).SplitGame(
                new SplitRequest { LibraryItemId = itemToSplitId });
            Assert.IsType<OkObjectResult>(result);
        }

        await using var verify = CreateContext(TenantA);
        var copy = await verify.PhysicalGameCopies.SingleAsync();
        Assert.Equal(physicalCopyId, copy.Id);
        Assert.Equal(originalGameId, copy.UnifiedGameId);
    }

    [Fact]
    public async Task BackupExportAndRestore_PhysicalCopiesRemainTenantIsolated()
    {
        await SeedPhysicalGameAsync(TenantA, "Tenant A backup", "tenant-a-backup", PhysicalGamePlatform.PS3);
        await SeedPhysicalGameAsync(TenantB, "Tenant B backup", "tenant-b-backup", PhysicalGamePlatform.PS5);

        GameHub.Core.Models.Backup.GameHubBackupV1 backup;
        await using (var tenantA = CreateContext(TenantA))
        {
            backup = await new BackupExportService(tenantA, new FixedTimeProvider(FixedNow))
                .CreateBackupAsync();
        }

        Assert.Equal("Tenant A backup", Assert.Single(backup.Games).Title);
        Assert.Equal(PhysicalGamePlatform.PS3, Assert.Single(backup.Games[0].PhysicalCopies!).Platform);

        await using (var tenantC = CreateContext(TenantC))
        {
            var result = await new BackupImportService(
                tenantC,
                new BackupJsonSerializer(),
                new FixedTimeProvider(FixedNow)).RestoreAsync(backup);
            Assert.True(result.Restored);
            var restored = await tenantC.PhysicalGameCopies.SingleAsync();
            Assert.Equal(TenantC, restored.TenantId);
            Assert.Equal(PhysicalGamePlatform.PS3, restored.Platform);
        }

        await using var verifyB = CreateContext(TenantB);
        var tenantBCopy = await verifyB.PhysicalGameCopies.SingleAsync();
        Assert.Equal(TenantB, tenantBCopy.TenantId);
        Assert.Equal(PhysicalGamePlatform.PS5, tenantBCopy.Platform);
    }

    [Fact]
    public async Task PhysicalCopyMigration_SqliteUpgradePreservesGamesAndEnforcesBoundaries()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CoreContext>()
            .UseSqlite(connection)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var db = new CoreContext(options, new FixedTenantContext(TenantA));
        var migrator = db.Database.GetService<IMigrator>();

        await migrator.MigrateAsync("20260712073612_CategorizeCustomTagDefinitionsPostgres");
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Tenants" ("Id", "Name", "PlatformSyncEnabled", "CreatedAtUtc", "DeletionRequestedAtUtc")
            VALUES ({TenantB}, {"Tenant B"}, {false}, {FixedNow.UtcDateTime}, NULL);
            INSERT INTO "UnifiedGames" ("TenantId", "Title", "Slug", "PlayStatus", "IsPlayStatusManual", "IsIgnored")
            VALUES ({TenantA}, {"Preserved"}, {"preserved-physical-upgrade"}, {0}, {false}, {false});
            """);

        await migrator.MigrateAsync();
        var gameId = await db.UnifiedGames.Select(game => game.Id).SingleAsync();
        db.PhysicalGameCopies.Add(new PhysicalGameCopy
        {
            UnifiedGameId = gameId,
            Platform = PhysicalGamePlatform.PS4
        });
        await db.SaveChangesAsync();

        Assert.Equal("Preserved", (await db.UnifiedGames.SingleAsync()).Title);
        Assert.Equal(PhysicalGamePlatform.PS4, (await db.PhysicalGameCopies.SingleAsync()).Platform);
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "PhysicalGameCopies" ("TenantId", "UnifiedGameId", "Platform")
            VALUES ({TenantB}, {gameId}, {(int)PhysicalGamePlatform.PS5})
            """));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "PhysicalGameCopies" ("TenantId", "UnifiedGameId", "Platform")
            VALUES ({TenantA}, {gameId}, {99})
            """));
    }

    [Fact]
    public async Task PhysicalCopyMigration_SqliteFreshInstallCreatesUsableSchema()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CoreContext>()
            .UseSqlite(connection)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var db = new CoreContext(options, new FixedTenantContext(TenantA));

        await db.Database.MigrateAsync();
        var game = new UnifiedGame { Title = "Fresh", Slug = "fresh-physical-schema" };
        db.UnifiedGames.Add(game);
        await db.SaveChangesAsync();
        db.PhysicalGameCopies.Add(new PhysicalGameCopy
        {
            UnifiedGameId = game.Id,
            Platform = PhysicalGamePlatform.PS5
        });
        await db.SaveChangesAsync();

        Assert.Equal(PhysicalGamePlatform.PS5, (await db.PhysicalGameCopies.SingleAsync()).Platform);
    }

    private CoreContext CreateContext(Guid tenantId) => new(_options, new FixedTenantContext(tenantId));

    private async Task SeedPhysicalGameAsync(
        Guid tenantId,
        string title,
        string slug,
        PhysicalGamePlatform platform)
    {
        await using var db = CreateContext(tenantId);
        db.UnifiedGames.Add(new UnifiedGame
        {
            Title = title,
            Slug = slug,
            PhysicalCopies = [new PhysicalGameCopy { Platform = platform }]
        });
        await db.SaveChangesAsync();
    }

    private static LibraryController CreateController(CoreContext db, Guid tenantId) =>
        new(
            null!,
            null!,
            null!,
            db,
            NullLogger<LibraryController>.Instance,
            new SyncSchedulerStatus(),
            new FixedTenantContext(tenantId),
            new ActivityLogService(db),
            null!);

    private static T GetProperty<T>(object value, string propertyName)
    {
        var property = value.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        Assert.NotNull(property);
        return Assert.IsAssignableFrom<T>(property!.GetValue(value));
    }

    private sealed class FixedTenantContext(Guid tenantId) : ITenantContext
    {
        public Guid TenantId { get; } = tenantId;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
