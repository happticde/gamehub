using System.Data.Common;
using GameHub.Core.Data;
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
            tenantA.Tenants.Add(new Tenant
            {
                Id = TenantB,
                Name = "Tenant B",
                CreatedAtUtc = new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc)
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
