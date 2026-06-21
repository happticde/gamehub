using System.Data.Common;
using GameHub.Core.Data;
using GameHub.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace GameHub.Tests;

/// <summary>Runs the tenant-safe tag upgrade against a real PostgreSQL database when configured.</summary>
public sealed class PostgresTagRelationshipMigrationTests
{
    private static readonly Guid TenantA = TenantDefaults.DefaultTenantId;
    private static readonly Guid TenantB = Guid.Parse("00000000-0000-0000-0000-000000000002");

    [Fact]
    public async Task TenantFoundationMigration_OperationalHistoryIsPreservedForDefaultTenant()
    {
        var connectionString = Environment.GetEnvironmentVariable("GAMEHUB_TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var options = new DbContextOptionsBuilder<CoreContext>()
            .UseNpgsql(connectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var db = new CoreContext(options, new FixedTenantContext(TenantA));

        await db.Database.EnsureDeletedAsync();
        try
        {
            var migrator = db.Database.GetService<IMigrator>();
            await migrator.MigrateAsync("20260324123258_AddActivityLog");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "ActivityLogs" ("Timestamp", "EventType", "Message", "Details")
                VALUES (TIMESTAMPTZ '2026-06-19 10:00:00+00', 'LegacyActivity', 'Legacy activity preserved', 'legacy-details');
                INSERT INTO "SyncAuditLogs"
                    ("Timestamp", "Platform", "Status", "ItemsProcessed", "NewGamesCreated", "ErrorMessage")
                VALUES (TIMESTAMPTZ '2026-06-19 11:00:00+00', 0, 'Error', 12, 3, 'Legacy sync audit preserved');
                INSERT INTO "SyncMetadata" ("Platform", "LastSyncedAt")
                VALUES (0, TIMESTAMPTZ '2026-06-19 12:00:00+00');
                """);

            await migrator.MigrateAsync();
            db.ChangeTracker.Clear();

            var activity = await db.ActivityLogs.SingleAsync();
            Assert.Equal(TenantA, activity.TenantId);
            Assert.Equal("Legacy activity preserved", activity.Message);
            var audit = await db.SyncAuditLogs.SingleAsync();
            Assert.Equal(TenantA, audit.TenantId);
            Assert.Equal("Legacy sync audit preserved", audit.ErrorMessage);
            var metadata = await db.SyncMetadata.SingleAsync();
            Assert.Equal(TenantA, metadata.TenantId);
            Assert.Equal(Platform.Steam, metadata.Platform);
            var defaultTenant = await db.Tenants.SingleAsync();
            Assert.Null(defaultTenant.DeletionRequestedAtUtc);
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task TenantTagRelationshipMigration_ExistingAssignmentsPreservedAndCrossTenantRejected()
    {
        var connectionString = Environment.GetEnvironmentVariable("GAMEHUB_TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var options = new DbContextOptionsBuilder<CoreContext>()
            .UseNpgsql(connectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var tenantA = new CoreContext(options, new FixedTenantContext(TenantA));

        await tenantA.Database.EnsureDeletedAsync();
        try
        {
            var migrator = tenantA.Database.GetService<IMigrator>();
            await migrator.MigrateAsync("20260621110000_EnforceTenantGameRelationshipsPostgres");
            // The test intentionally stops before the lifecycle migration. Insert with the
            // historical tenant shape so the current EF model does not reference the future
            // nullable DeletionRequestedAtUtc column prematurely.
            await tenantA.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Tenants" ("Id", "Name", "PlatformSyncEnabled", "CreatedAtUtc")
                VALUES ({TenantB}, {"Tenant B"}, {false}, {new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc)})
                """);
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

            await Assert.ThrowsAnyAsync<DbException>(() => tenantA.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "TagUnifiedGame" ("TenantId", "GamesId", "TagsId")
                VALUES ({TenantA}, {game.Id}, {foreignTagId})
                """));
        }
        finally
        {
            await tenantA.Database.EnsureDeletedAsync();
        }
    }

    private sealed class FixedTenantContext(Guid tenantId) : ITenantContext
    {
        public Guid TenantId { get; } = tenantId;
    }
}
