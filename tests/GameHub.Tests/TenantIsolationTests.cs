using GameHub.Core.Controllers;
using GameHub.Core.Data;
using GameHub.Core.Models.Backup;
using GameHub.Core.Services;
using GameHub.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using Xunit;

namespace GameHub.Tests;

/// <summary>Verifies the tenant boundary before authenticated tenant selection is enabled.</summary>
public sealed class TenantIsolationTests : IDisposable
{
    private static readonly Guid TenantA = TenantDefaults.DefaultTenantId;
    private static readonly Guid TenantB = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset FixedNow = new(2026, 6, 19, 18, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreContext> _options;

    public TenantIsolationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<CoreContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = CreateContext(TenantA);
        db.Database.EnsureCreated();
        db.Tenants.Add(new Tenant
        {
            Id = TenantB,
            Name = "Tenant B",
            CreatedAtUtc = FixedNow.UtcDateTime
        });
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task TenantScopedData_SameNaturalKeys_CoexistWithoutLeaking()
    {
        await using (var tenantA = CreateContext(TenantA))
        {
            await SeedCompleteGameAsync(tenantA, "Tenant A game");
        }

        await using (var tenantB = CreateContext(TenantB))
        {
            await SeedCompleteGameAsync(tenantB, "Tenant B game");
        }

        await using var verifyA = CreateContext(TenantA);
        await using var verifyB = CreateContext(TenantB);

        Assert.Equal("Tenant A game", (await verifyA.UnifiedGames.SingleAsync()).Title);
        Assert.Equal("Tenant B game", (await verifyB.UnifiedGames.SingleAsync()).Title);
        Assert.Equal(TenantA, (await verifyA.Tags.SingleAsync()).TenantId);
        Assert.Equal(TenantB, (await verifyB.Tags.SingleAsync()).TenantId);
        Assert.Equal(2, await verifyA.UnifiedGames.IgnoreQueryFilters().CountAsync());
        Assert.Equal(2, await verifyA.ManualLinks.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task OperationalHistoryReads_ActiveTenant_DoNotExposeOtherTenant()
    {
        await SeedOperationalHistoryAsync(TenantA, "Tenant A", new DateTime(2026, 6, 21, 10, 0, 0, DateTimeKind.Utc));
        await SeedOperationalHistoryAsync(TenantB, "Tenant B secret", new DateTime(2026, 6, 21, 11, 0, 0, DateTimeKind.Utc));

        await using var tenantA = CreateContext(TenantA);
        var controller = CreateLibraryController(tenantA, TenantA);

        var activityResult = Assert.IsType<OkObjectResult>(await controller.GetActivity());
        var activity = Assert.Single(Assert.IsAssignableFrom<IEnumerable<object>>(activityResult.Value));
        Assert.Equal("Tenant A activity", GetProperty<string>(activity, "Message"));

        var syncResult = Assert.IsType<OkObjectResult>(await controller.GetSyncHistory(null));
        var syncLog = Assert.Single(Assert.IsAssignableFrom<IEnumerable<SyncAuditLog>>(syncResult.Value));
        Assert.Equal("Tenant A sync error", syncLog.ErrorMessage);
        Assert.Equal(TenantA, syncLog.TenantId);

        var metadata = await tenantA.SyncMetadata.SingleAsync();
        Assert.Equal(Platform.Steam, metadata.Platform);
        Assert.Equal(new DateTime(2026, 6, 21, 10, 0, 0, DateTimeKind.Utc), metadata.LastSyncedAt);
        Assert.Equal(TenantA, metadata.TenantId);
    }

    [Fact]
    public async Task SyncStatus_ActiveTenant_DoesNotExposeOtherTenantResult()
    {
        var status = new SyncSchedulerStatus { IsEnabled = true };
        status.MarkCompleted(TenantA, 7);
        status.MarkFailed(TenantB, "Tenant B secret failure");

        await using var tenantADb = CreateContext(TenantA);
        await using var tenantBDb = CreateContext(TenantB);
        var tenantAResult = Assert.IsType<OkObjectResult>(
            CreateLibraryController(tenantADb, TenantA, status).GetSyncStatus());
        var tenantBResult = Assert.IsType<OkObjectResult>(
            CreateLibraryController(tenantBDb, TenantB, status).GetSyncStatus());
        var tenantASnapshot = Assert.IsType<SyncSchedulerSnapshot>(tenantAResult.Value);
        var tenantBSnapshot = Assert.IsType<SyncSchedulerSnapshot>(tenantBResult.Value);

        Assert.Equal("Success", tenantASnapshot.LastResult);
        Assert.Equal(7, tenantASnapshot.ItemsProcessed);
        Assert.Equal("Tenant B secret failure", tenantBSnapshot.LastResult);
        Assert.Equal(0, tenantBSnapshot.ItemsProcessed);
    }

    [Theory]
    [InlineData("activity")]
    [InlineData("sync-audit")]
    [InlineData("sync-metadata")]
    public async Task OperationalRecordWrite_ExplicitDifferentTenant_IsRejected(string recordType)
    {
        await using var tenantA = CreateContext(TenantA);
        tenantA.Add(recordType switch
        {
            "activity" => new ActivityLog { TenantId = TenantB, EventType = "Test", Message = "Foreign" },
            "sync-audit" => new SyncAuditLog { TenantId = TenantB, Platform = Platform.Steam, Status = "Success" },
            "sync-metadata" => new SyncMetadata
            {
                TenantId = TenantB,
                Platform = Platform.Steam,
                LastSyncedAt = FixedNow.UtcDateTime
            },
            _ => throw new ArgumentOutOfRangeException(nameof(recordType), recordType, null)
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => tenantA.SaveChangesAsync());
    }

    [Fact]
    public async Task SaveChanges_ExplicitDifferentTenant_RejectsWrite()
    {
        await using var tenantA = CreateContext(TenantA);
        tenantA.UnifiedGames.Add(new UnifiedGame
        {
            TenantId = TenantB,
            Title = "Wrong owner",
            Slug = "wrong-owner"
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tenantA.SaveChangesAsync());

        Assert.Equal("Cannot create a record for a different tenant.", exception.Message);
    }

    [Theory]
    [InlineData("library-item")]
    [InlineData("manual-link")]
    [InlineData("enrichment-audit")]
    public async Task TenantGameRelationship_ForeignTenantGame_RejectsWrite(string relationship)
    {
        int foreignGameId;
        await using (var tenantB = CreateContext(TenantB))
        {
            var game = new UnifiedGame
            {
                Title = "Tenant B relation target",
                Slug = "tenant-b-relation-target"
            };
            tenantB.UnifiedGames.Add(game);
            await tenantB.SaveChangesAsync();
            foreignGameId = game.Id;
        }

        await using var tenantA = CreateContext(TenantA);
        tenantA.Add(CreateTenantGameDependent(relationship, foreignGameId));

        await Assert.ThrowsAsync<DbUpdateException>(() => tenantA.SaveChangesAsync());
    }

    [Fact]
    public async Task TenantTagRelationship_ForeignTenantTag_RejectsWrite()
    {
        int gameId;
        await using (var tenantA = CreateContext(TenantA))
        {
            var game = new UnifiedGame
            {
                Title = "Tenant A tag target",
                Slug = "tenant-a-tag-target"
            };
            tenantA.UnifiedGames.Add(game);
            await tenantA.SaveChangesAsync();
            gameId = game.Id;
        }

        int foreignTagId;
        await using (var tenantB = CreateContext(TenantB))
        {
            var tag = new Tag { Name = "Tenant B tag" };
            tenantB.Tags.Add(tag);
            await tenantB.SaveChangesAsync();
            foreignTagId = tag.Id;
        }

        await using var verifyBoundary = CreateContext(TenantA);
        await Assert.ThrowsAsync<SqliteException>(() => verifyBoundary.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "TagUnifiedGame" ("TenantId", "GamesId", "TagsId")
            VALUES ({TenantA}, {gameId}, {foreignTagId})
            """));
    }

    [Fact]
    public async Task UpdatePersonalTracking_ForeignTenantGame_ReturnsNotFoundWithoutMutation()
    {
        int foreignGameId;
        await using (var tenantB = CreateContext(TenantB))
        {
            var game = new UnifiedGame
            {
                Title = "Tenant B personal game",
                Slug = "tenant-b-personal",
                PlayStatus = PlayStatus.Unplayed
            };
            tenantB.UnifiedGames.Add(game);
            await tenantB.SaveChangesAsync();
            foreignGameId = game.Id;
        }

        await using (var tenantA = CreateContext(TenantA))
        {
            var controller = CreateLibraryController(tenantA);

            var result = await controller.UpdatePersonalTracking(
                foreignGameId,
                new PersonalUpdateRequest
                {
                    PlayStatus = PlayStatus.Completed,
                    UserRating = 5,
                    IsIgnored = true
                });

            Assert.IsType<NotFoundObjectResult>(result);
            Assert.Empty(tenantA.ActivityLogs);
        }

        await using var verifyB = CreateContext(TenantB);
        var unchanged = await verifyB.UnifiedGames.SingleAsync(game => game.Id == foreignGameId);
        Assert.Equal(PlayStatus.Unplayed, unchanged.PlayStatus);
        Assert.Null(unchanged.UserRating);
        Assert.False(unchanged.IsIgnored);
        Assert.False(unchanged.IsPlayStatusManual);
        Assert.Empty(verifyB.ActivityLogs);
    }

    [Fact]
    public async Task MergeGames_ForeignTenantSource_ReturnsNotFoundWithoutMutation()
    {
        int targetGameId;
        await using (var tenantA = CreateContext(TenantA))
        {
            var target = CreateGameWithItem(
                "Tenant A target",
                "tenant-a-target",
                Platform.Steam,
                "tenant-a-steam");
            tenantA.UnifiedGames.Add(target);
            await tenantA.SaveChangesAsync();
            targetGameId = target.Id;
        }

        int foreignSourceId;
        await using (var tenantB = CreateContext(TenantB))
        {
            var source = CreateGameWithItem(
                "Tenant B source",
                "tenant-b-source",
                Platform.Gog,
                "tenant-b-gog");
            tenantB.UnifiedGames.Add(source);
            await tenantB.SaveChangesAsync();
            foreignSourceId = source.Id;
        }

        await using (var tenantA = CreateContext(TenantA))
        {
            var controller = CreateLibraryController(tenantA);

            var result = await controller.MergeGames(new MergeRequest
            {
                SourceGameId = foreignSourceId,
                TargetGameId = targetGameId
            });

            Assert.IsType<NotFoundObjectResult>(result);
        }

        await AssertTenantLibrariesUnchangedAsync(
            targetGameId,
            "tenant-a-steam",
            foreignSourceId,
            "tenant-b-gog");
    }

    [Fact]
    public async Task MergeGames_ForeignTenantTarget_ReturnsNotFoundWithoutMutation()
    {
        int sourceGameId;
        await using (var tenantA = CreateContext(TenantA))
        {
            var source = CreateGameWithItem(
                "Tenant A source",
                "tenant-a-source",
                Platform.Gog,
                "tenant-a-gog");
            tenantA.UnifiedGames.Add(source);
            await tenantA.SaveChangesAsync();
            sourceGameId = source.Id;
        }

        int foreignTargetId;
        await using (var tenantB = CreateContext(TenantB))
        {
            var target = CreateGameWithItem(
                "Tenant B target",
                "tenant-b-target",
                Platform.Steam,
                "tenant-b-steam");
            tenantB.UnifiedGames.Add(target);
            await tenantB.SaveChangesAsync();
            foreignTargetId = target.Id;
        }

        await using (var tenantA = CreateContext(TenantA))
        {
            var controller = CreateLibraryController(tenantA);

            var result = await controller.MergeGames(new MergeRequest
            {
                SourceGameId = sourceGameId,
                TargetGameId = foreignTargetId
            });

            Assert.IsType<NotFoundObjectResult>(result);
        }

        await AssertTenantLibrariesUnchangedAsync(
            sourceGameId,
            "tenant-a-gog",
            foreignTargetId,
            "tenant-b-steam");
    }

    [Fact]
    public async Task SplitGame_ForeignTenantItem_ReturnsNotFoundWithoutMutation()
    {
        int foreignGameId;
        int foreignItemId;
        await using (var tenantB = CreateContext(TenantB))
        {
            var game = CreateGameWithItem(
                "Tenant B split game",
                "tenant-b-split",
                Platform.Psn,
                "tenant-b-psn");
            tenantB.UnifiedGames.Add(game);
            await tenantB.SaveChangesAsync();
            foreignGameId = game.Id;
            foreignItemId = Assert.Single(game.Items).Id;
        }

        await using (var tenantA = CreateContext(TenantA))
        {
            var controller = CreateLibraryController(tenantA);

            var result = await controller.SplitGame(new SplitRequest
            {
                LibraryItemId = foreignItemId
            });

            Assert.IsType<NotFoundObjectResult>(result);
            Assert.Empty(tenantA.UnifiedGames);
            Assert.Empty(tenantA.ManualLinks);
            Assert.Empty(tenantA.ActivityLogs);
        }

        await using var verifyB = CreateContext(TenantB);
        var unchanged = await verifyB.UnifiedGames
            .Include(game => game.Items)
            .SingleAsync(game => game.Id == foreignGameId);
        Assert.Equal("tenant-b-psn", Assert.Single(unchanged.Items).ExternalId);
        Assert.Empty(verifyB.ManualLinks);
        Assert.Empty(verifyB.ActivityLogs);
    }

    [Fact]
    public async Task BackupExport_ActiveTenant_DoesNotIncludeOtherTenant()
    {
        await using (var tenantA = CreateContext(TenantA))
        {
            tenantA.UnifiedGames.Add(new UnifiedGame { Title = "Tenant A game", Slug = "tenant-a" });
            await tenantA.SaveChangesAsync();
        }

        await using (var tenantB = CreateContext(TenantB))
        {
            tenantB.UnifiedGames.Add(new UnifiedGame { Title = "Tenant B game", Slug = "tenant-b" });
            await tenantB.SaveChangesAsync();
        }

        await using var exportContext = CreateContext(TenantA);
        var exporter = new BackupExportService(exportContext, new FixedTimeProvider(FixedNow));

        var backup = await exporter.CreateBackupAsync();

        Assert.Equal("Tenant A game", Assert.Single(backup.Games).Title);
    }

    [Fact]
    public async Task BackupRestore_OtherTenantHasData_RestoresOnlyActiveTenant()
    {
        await using (var tenantB = CreateContext(TenantB))
        {
            tenantB.UnifiedGames.Add(new UnifiedGame { Title = "Tenant B game", Slug = "shared" });
            await tenantB.SaveChangesAsync();
        }

        await using (var tenantA = CreateContext(TenantA))
        {
            var importer = new BackupImportService(
                tenantA,
                new BackupJsonSerializer(),
                new FixedTimeProvider(FixedNow));

            var result = await importer.RestoreAsync(CreateBackup("Tenant A game", "shared"));

            Assert.True(result.Restored);
            Assert.Equal("Tenant A game", (await tenantA.UnifiedGames.SingleAsync()).Title);
            Assert.All(
                tenantA.ChangeTracker.Entries<ITenantOwned>().Select(entry => entry.Entity),
                record => Assert.Equal(TenantA, record.TenantId));
        }

        await using var verifyB = CreateContext(TenantB);
        Assert.Equal("Tenant B game", (await verifyB.UnifiedGames.SingleAsync()).Title);
        Assert.Empty(verifyB.ActivityLogs);
    }

    [Fact]
    public async Task TenantMigration_ExistingLibrary_AssignsDefaultTenant()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CoreContext>()
            .UseSqlite(connection)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var db = new CoreContext(options, new FixedTenantContext(TenantA));
        var migrator = db.Database.GetService<IMigrator>();

        await migrator.MigrateAsync("20260324123259_AddActivityLog");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO UnifiedGames (Title, Slug) VALUES ('Legacy game', 'legacy-game')");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO ActivityLogs (Timestamp, EventType, Message, Details)
            VALUES ('2026-06-19 10:00:00', 'LegacyActivity', 'Legacy activity preserved', 'legacy-details');
            INSERT INTO SyncAuditLogs
                (Timestamp, Platform, Status, ItemsProcessed, NewGamesCreated, ErrorMessage)
            VALUES ('2026-06-19 11:00:00', 0, 'Error', 12, 3, 'Legacy sync audit preserved');
            INSERT INTO SyncMetadata (Platform, LastSyncedAt)
            VALUES (0, '2026-06-19 12:00:00');
            """);

        await migrator.MigrateAsync();
        db.ChangeTracker.Clear();

        var game = await db.UnifiedGames.SingleAsync();
        Assert.Equal(TenantA, game.TenantId);
        var defaultTenant = await db.Tenants.SingleAsync();
        Assert.Equal("Default", defaultTenant.Name);
        Assert.True(defaultTenant.PlatformSyncEnabled);
        Assert.Empty(db.UserAccounts);
        Assert.Empty(db.RefreshTokenSessions);
        var activity = await db.ActivityLogs.SingleAsync();
        Assert.Equal(TenantA, activity.TenantId);
        Assert.Equal("Legacy activity preserved", activity.Message);
        var audit = await db.SyncAuditLogs.SingleAsync();
        Assert.Equal(TenantA, audit.TenantId);
        Assert.Equal("Legacy sync audit preserved", audit.ErrorMessage);
        var metadata = await db.SyncMetadata.SingleAsync();
        Assert.Equal(TenantA, metadata.TenantId);
        Assert.Equal(Platform.Steam, metadata.Platform);
    }

    [Theory]
    [InlineData("library-item")]
    [InlineData("manual-link")]
    [InlineData("enrichment-audit")]
    public async Task TenantRelationshipMigration_ForeignTenantGame_RejectsWrite(string relationship)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CoreContext>()
            .UseSqlite(connection)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var tenantA = new CoreContext(options, new FixedTenantContext(TenantA));
        var migrator = tenantA.Database.GetService<IMigrator>();
        await migrator.MigrateAsync("20260620094428_AddRefreshTokenSessions");
        tenantA.Tenants.Add(new Tenant
        {
            Id = TenantB,
            Name = "Tenant B",
            CreatedAtUtc = FixedNow.UtcDateTime
        });
        await tenantA.SaveChangesAsync();

        int foreignGameId;
        await using (var tenantB = new CoreContext(options, new FixedTenantContext(TenantB)))
        {
            var game = new UnifiedGame
            {
                Title = "Tenant B migration target",
                Slug = "tenant-b-migration-target"
            };
            tenantB.UnifiedGames.Add(game);
            await tenantB.SaveChangesAsync();
            foreignGameId = game.Id;
        }

        await migrator.MigrateAsync();
        tenantA.ChangeTracker.Clear();
        tenantA.Add(CreateTenantGameDependent(relationship, foreignGameId));

        await Assert.ThrowsAsync<DbUpdateException>(() => tenantA.SaveChangesAsync());
    }

    [Fact]
    public async Task TenantTagRelationshipMigration_ExistingAssignmentsPreservedAndCrossTenantRejected()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CoreContext>()
            .UseSqlite(connection)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var tenantA = new CoreContext(options, new FixedTenantContext(TenantA));
        var migrator = tenantA.Database.GetService<IMigrator>();
        await migrator.MigrateAsync("20260621110001_EnforceTenantGameRelationships");
        tenantA.Tenants.Add(new Tenant
        {
            Id = TenantB,
            Name = "Tenant B",
            CreatedAtUtc = FixedNow.UtcDateTime
        });
        var game = new UnifiedGame { Title = "Existing game", Slug = "existing-game" };
        var localTag = new Tag { Name = "Local tag" };
        tenantA.AddRange(game, localTag);
        await tenantA.SaveChangesAsync();

        int foreignTagId;
        await using (var tenantB = new CoreContext(options, new FixedTenantContext(TenantB)))
        {
            var foreignTag = new Tag { Name = "Foreign legacy tag" };
            tenantB.Tags.Add(foreignTag);
            await tenantB.SaveChangesAsync();
            foreignTagId = foreignTag.Id;
        }

        await tenantA.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "TagUnifiedGame" ("GamesId", "TagsId")
            VALUES ({game.Id}, {localTag.Id}), ({game.Id}, {foreignTagId})
            """);

        await migrator.MigrateAsync();
        tenantA.ChangeTracker.Clear();

        var upgradedGame = await tenantA.UnifiedGames
            .Include(candidate => candidate.Tags)
            .SingleAsync(candidate => candidate.Id == game.Id);
        Assert.Equal(
            ["Foreign legacy tag", "Local tag"],
            upgradedGame.Tags.Select(tag => tag.Name).Order().ToArray());
        Assert.All(upgradedGame.Tags, tag => Assert.Equal(TenantA, tag.TenantId));
        Assert.Equal(2, await tenantA.Set<TagAssignment>().CountAsync());

        await Assert.ThrowsAsync<SqliteException>(() => tenantA.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "TagUnifiedGame" ("TenantId", "GamesId", "TagsId")
            VALUES ({TenantA}, {game.Id}, {foreignTagId})
            """));
    }

    private CoreContext CreateContext(Guid tenantId) =>
        new(_options, new FixedTenantContext(tenantId));

    private static LibraryController CreateLibraryController(
        CoreContext db,
        Guid? tenantId = null,
        SyncSchedulerStatus? syncStatus = null) =>
        new(
            null!,
            null!,
            db,
            NullLogger<LibraryController>.Instance,
            syncStatus ?? new SyncSchedulerStatus(),
            new FixedTenantContext(tenantId ?? TenantA),
            new ActivityLogService(db),
            null!);

    private async Task SeedOperationalHistoryAsync(Guid tenantId, string marker, DateTime lastSyncedAt)
    {
        await using var db = CreateContext(tenantId);
        db.ActivityLogs.Add(new ActivityLog
        {
            EventType = "TenantAudit",
            Message = $"{marker} activity"
        });
        db.SyncAuditLogs.Add(new SyncAuditLog
        {
            Platform = Platform.Steam,
            Status = "Error",
            ErrorMessage = $"{marker} sync error"
        });
        db.SyncMetadata.Add(new SyncMetadata
        {
            Platform = Platform.Steam,
            LastSyncedAt = lastSyncedAt
        });
        await db.SaveChangesAsync();
    }

    private static T GetProperty<T>(object instance, string propertyName) =>
        (T)(instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)
            ?.GetValue(instance)
            ?? throw new InvalidOperationException($"Property '{propertyName}' was not found."));

    private async Task AssertTenantLibrariesUnchangedAsync(
        int tenantAGameId,
        string tenantAExternalId,
        int tenantBGameId,
        string tenantBExternalId)
    {
        await using (var verifyA = CreateContext(TenantA))
        {
            var game = await verifyA.UnifiedGames
                .Include(candidate => candidate.Items)
                .SingleAsync(candidate => candidate.Id == tenantAGameId);
            Assert.Equal(tenantAExternalId, Assert.Single(game.Items).ExternalId);
            Assert.Empty(verifyA.ManualLinks);
            Assert.Empty(verifyA.ActivityLogs);
        }

        await using var verifyB = CreateContext(TenantB);
        var foreignGame = await verifyB.UnifiedGames
            .Include(candidate => candidate.Items)
            .SingleAsync(candidate => candidate.Id == tenantBGameId);
        Assert.Equal(tenantBExternalId, Assert.Single(foreignGame.Items).ExternalId);
        Assert.Empty(verifyB.ManualLinks);
        Assert.Empty(verifyB.ActivityLogs);
    }

    private static UnifiedGame CreateGameWithItem(
        string title,
        string slug,
        Platform platform,
        string externalId) =>
        new()
        {
            Title = title,
            Slug = slug,
            Items =
            [
                new LibraryItem
                {
                    Platform = platform,
                    ExternalId = externalId,
                    LastSynced = FixedNow.UtcDateTime
                }
            ]
        };

    private static object CreateTenantGameDependent(string relationship, int unifiedGameId) =>
        relationship switch
        {
            "library-item" => new LibraryItem
            {
                UnifiedGameId = unifiedGameId,
                Platform = Platform.Steam,
                ExternalId = "foreign-game"
            },
            "manual-link" => new ManualLink
            {
                UnifiedGameId = unifiedGameId,
                Platform = Platform.Steam,
                ExternalId = "foreign-game"
            },
            "enrichment-audit" => new EnrichmentAuditLog
            {
                UnifiedGameId = unifiedGameId,
                ProviderName = "Test",
                Status = "Success"
            },
            _ => throw new ArgumentOutOfRangeException(nameof(relationship), relationship, null)
        };

    private static UnifiedGame CreateGame(string title)
    {
        return new UnifiedGame
        {
            Title = title,
            Slug = "shared",
            Tags = [new Tag { Name = "Shared tag" }],
            Items =
            [
                new LibraryItem
                {
                    Platform = Platform.Steam,
                    ExternalId = "42",
                    LastSynced = FixedNow.UtcDateTime
                }
            ]
        };
    }

    private static async Task SeedCompleteGameAsync(CoreContext db, string title)
    {
        var game = CreateGame(title);
        db.UnifiedGames.Add(game);
        await db.SaveChangesAsync();
        db.ManualLinks.Add(new ManualLink
        {
            Platform = Platform.Steam,
            ExternalId = "42",
            UnifiedGameId = game.Id
        });
        await db.SaveChangesAsync();
    }

    private static GameHubBackupV1 CreateBackup(string title, string slug) =>
        new(
            BackupExportService.FormatName,
            BackupExportService.CurrentFormatVersion,
            FixedNow,
            [
                new BackupGameV1(
                    title,
                    slug,
                    new BackupPersonalTrackingV1(PlayStatus.Unplayed, null, false, false),
                    new BackupMetadataV1(
                        null, null, null, null, null, null, null, null, null, null, null, null, null),
                    ["Shared tag"],
                    [new BackupLibraryItemV1(Platform.Steam, "42", FixedNow.UtcDateTime)],
                    [new BackupManualLinkV1(Platform.Steam, "42")])
            ]);

    private sealed class FixedTenantContext(Guid tenantId) : ITenantContext
    {
        public Guid TenantId { get; } = tenantId;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
