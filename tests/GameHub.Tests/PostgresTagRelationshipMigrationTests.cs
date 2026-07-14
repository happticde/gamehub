using System.Data.Common;
using GameHub.Core.Data;
using GameHub.Core.Services;
using GameHub.Shared;
using GameHub.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace GameHub.Tests;

/// <summary>Runs the tenant-safe tag upgrade against a real PostgreSQL database when configured.</summary>
public sealed class PostgresTagRelationshipMigrationTests
{
    private const string ValidGtin = "00012345000058";
    private static readonly Guid TenantA = TenantDefaults.DefaultTenantId;
    private static readonly Guid TenantB = Guid.Parse("00000000-0000-0000-0000-000000000002");

    [Fact]
    public async Task PhysicalCopyMigration_PostgresUpgradePreservesGamesAndEnforcesBoundaries()
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
            await migrator.MigrateAsync("20260712073612_CategorizeCustomTagDefinitionsPostgres");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Tenants" ("Id", "Name", "PlatformSyncEnabled", "CreatedAtUtc", "DeletionRequestedAtUtc")
                VALUES ({TenantB}, {"Tenant B"}, {false}, {new DateTime(2026, 7, 13, 20, 0, 0, DateTimeKind.Utc)}, NULL);
                INSERT INTO "UnifiedGames" ("TenantId", "Title", "Slug", "PlayStatus", "IsPlayStatusManual", "IsIgnored")
                VALUES ({TenantA}, {"Preserved"}, {"preserved-physical-postgres"}, {0}, {false}, {false});
                """);

            await migrator.MigrateAsync();
            var gameId = await db.UnifiedGames.Select(game => game.Id).SingleAsync();
            db.PhysicalGameCopies.Add(new PhysicalGameCopy
            {
                UnifiedGameId = gameId,
                Platform = PhysicalGamePlatform.PS4,
                NormalizedGtin = ValidGtin
            });
            db.PhysicalGameCopies.Add(new PhysicalGameCopy
            {
                UnifiedGameId = gameId,
                Platform = PhysicalGamePlatform.PS4,
                NormalizedGtin = ValidGtin
            });
            await db.SaveChangesAsync();

            Assert.Equal("Preserved", (await db.UnifiedGames.SingleAsync()).Title);
            Assert.Equal(2, await db.PhysicalGameCopies.CountAsync(copy => copy.NormalizedGtin == ValidGtin));
            await Assert.ThrowsAnyAsync<DbException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "PhysicalGameCopies" ("TenantId", "UnifiedGameId", "Platform")
                VALUES ({TenantB}, {gameId}, {(int)PhysicalGamePlatform.PS5})
                """));
            await Assert.ThrowsAnyAsync<DbException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "PhysicalGameCopies" ("TenantId", "UnifiedGameId", "Platform")
                VALUES ({TenantA}, {gameId}, {99})
                """));
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task PhysicalCopyMigration_PostgresFreshInstallCreatesUsableSchema()
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
            await db.Database.MigrateAsync();
            var game = new UnifiedGame { Title = "Fresh", Slug = "fresh-physical-postgres" };
            db.UnifiedGames.Add(game);
            await db.SaveChangesAsync();
            db.PhysicalGameCopies.Add(new PhysicalGameCopy
            {
                UnifiedGameId = game.Id,
                Platform = PhysicalGamePlatform.PS5,
                NormalizedGtin = ValidGtin
            });
            await db.SaveChangesAsync();

            Assert.Equal(PhysicalGamePlatform.PS5, (await db.PhysicalGameCopies.SingleAsync()).Platform);
            Assert.Equal(ValidGtin, (await db.PhysicalGameCopies.SingleAsync()).NormalizedGtin);
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task CustomTagDefinitionMigration_PostgresExistingCustomAssignment_BackfillsDefinitionOrigin()
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
            await migrator.MigrateAsync("20260712071829_AddCustomTagAssignmentsPostgres");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "UnifiedGames"
                    ("Id", "TenantId", "Title", "Slug", "PlayStatus", "IsPlayStatusManual", "IsIgnored")
                VALUES
                    ({7001}, {TenantA}, {"Migration tag game"}, {"migration-tag-game"}, {0}, {false}, {false});
                INSERT INTO "Tags" ("Id", "TenantId", "Name")
                VALUES
                    ({7001}, {TenantA}, {"Legacy custom"}),
                    ({7002}, {TenantA}, {"Legacy system"});
                INSERT INTO "TagUnifiedGame" ("TenantId", "GamesId", "TagsId", "IsCustom")
                VALUES ({TenantA}, {7001}, {7001}, {true});
                """);

            await migrator.MigrateAsync();
            db.ChangeTracker.Clear();

            var tags = await db.Tags.OrderBy(tag => tag.Id).ToListAsync();
            Assert.True(tags[0].IsCustomDefinition);
            Assert.False(tags[1].IsCustomDefinition);
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task AccountTypeMigration_PostgresFreshInstallEnforcesExternalCredentialBoundary()
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
            await db.Database.MigrateAsync();
            var externalTenantId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Tenants"
                    ("Id", "Name", "PlatformSyncEnabled", "DeletionRequestedAtUtc", "CreatedAtUtc")
                VALUES
                    ({externalTenantId}, {"External"}, {false}, NULL, {new DateTime(2026, 6, 24, 12, 0, 0, DateTimeKind.Utc)});
                INSERT INTO "UserAccounts"
                    ("Id", "TenantId", "AccountType", "Email", "NormalizedEmail", "DisplayName", "PasswordHash", "CreatedAtUtc", "LastLoginAtUtc")
                VALUES
                    ({Guid.NewGuid()}, {externalTenantId}, {1}, NULL, NULL, {"OIDC Account"}, NULL, {new DateTime(2026, 6, 24, 12, 0, 0, DateTimeKind.Utc)}, NULL);
                """);

            var invalidTenantId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Tenants"
                    ("Id", "Name", "PlatformSyncEnabled", "DeletionRequestedAtUtc", "CreatedAtUtc")
                VALUES
                    ({invalidTenantId}, {"Invalid"}, {false}, NULL, {new DateTime(2026, 6, 24, 12, 0, 0, DateTimeKind.Utc)});
                """);
            await Assert.ThrowsAnyAsync<DbException>(() =>
                db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "UserAccounts"
                        ("Id", "TenantId", "AccountType", "Email", "NormalizedEmail", "DisplayName", "PasswordHash", "CreatedAtUtc", "LastLoginAtUtc")
                    VALUES
                        ({Guid.NewGuid()}, {invalidTenantId}, {1}, {"profile@example.com"}, {"PROFILE@EXAMPLE.COM"}, {"Invalid"}, {"forbidden-password-hash"}, {new DateTime(2026, 6, 24, 12, 0, 0, DateTimeKind.Utc)}, NULL);
                    """));

            Assert.Equal(3, await db.Tenants.CountAsync());
            var account = await db.UserAccounts.SingleAsync();
            Assert.Equal(UserAccountType.ExternalOidc, account.AccountType);
            Assert.Null(account.Email);
            Assert.Null(account.PasswordHash);
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task ExternalIdentityMigration_PostgresUpgradePreservesLocalAccountSessionAndLibrary()
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
            await migrator.MigrateAsync("20260621183449_AddExternalIdentities");
            var accountId = Guid.NewGuid();
            var pendingAtUtc = new DateTime(2026, 6, 21, 11, 30, 0, DateTimeKind.Utc);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "UserAccounts"
                    ("Id", "TenantId", "Email", "NormalizedEmail", "DisplayName", "PasswordHash", "CreatedAtUtc", "LastLoginAtUtc")
                VALUES
                    ({accountId}, {TenantA}, {"legacy@example.com"}, {"LEGACY@EXAMPLE.COM"}, {"Legacy Owner"}, {"preserved-password-hash"}, {new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc)}, NULL);
                INSERT INTO "RefreshTokenSessions"
                    ("Id", "UserAccountId", "TokenHash", "CreatedAtUtc", "ExpiresAtUtc", "ConsumedAtUtc", "RevokedAtUtc")
                VALUES
                    ({Guid.NewGuid()}, {accountId}, {new string('A', 64)}, {new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc)}, {new DateTime(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc)}, NULL, NULL);
                INSERT INTO "ExternalIdentities"
                    ("Id", "Issuer", "Subject", "UserAccountId", "TenantId", "CreatedAtUtc", "LastLoginAtUtc")
                VALUES
                    ({Guid.NewGuid()}, {"https://identity.example.test"}, {"legacy-owner"}, {accountId}, {TenantA}, {new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc)}, NULL);
                UPDATE "Tenants"
                SET "DeletionRequestedAtUtc" = {pendingAtUtc}
                WHERE "Id" = {TenantA};
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "UnifiedGames"
                    ("TenantId", "Title", "Slug", "PlayStatus", "IsPlayStatusManual", "IsIgnored")
                VALUES ({TenantA}, {"Preserved game"}, {"preserved-game"}, {0}, {false}, {false});
                INSERT INTO "LibraryItems" ("TenantId", "UnifiedGameId", "Platform", "ExternalId", "LastSynced")
                SELECT {TenantA}, "Id", {(int)GameHub.Shared.Platform.Steam}, {"preserved-item"}, {new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc)}
                FROM "UnifiedGames"
                WHERE "TenantId" = {TenantA} AND "Slug" = {"preserved-game"};
                """);

            await migrator.MigrateAsync();
            db.ChangeTracker.Clear();

            var enablement = new MultiUserEnablementService(
                db,
                new MultiUserEnablementSettings(new MultiUserMode(true), []),
                new OidcSettings(
                    true,
                    "https://identity.example.test",
                    "gamehub-client",
                    "https://identity.example.test/.well-known/openid-configuration",
                    ["RS256"],
                    new string('B', 32),
                    OidcProvisioningMode.PersonalTenantJit),
                new TestMultiUserIngestReadinessProbe(enabled: true));
            await enablement.ValidateStartupAsync();
            db.ChangeTracker.Clear();

            var upgradedAccount = await db.UserAccounts.SingleAsync();
            Assert.Equal(UserAccountType.Local, upgradedAccount.AccountType);
            Assert.Equal("legacy@example.com", upgradedAccount.Email);
            Assert.Equal("LEGACY@EXAMPLE.COM", upgradedAccount.NormalizedEmail);
            Assert.Equal("preserved-password-hash", upgradedAccount.PasswordHash);
            Assert.Equal(new string('A', 64), (await db.RefreshTokenSessions.SingleAsync()).TokenHash);
            Assert.Equal("Preserved game", (await db.UnifiedGames.SingleAsync()).Title);
            var upgradedLibraryItem = await db.LibraryItems.SingleAsync();
            Assert.Equal("preserved-item", upgradedLibraryItem.ExternalId);
            Assert.Null(upgradedLibraryItem.SourceContentHash);
            var upgradedTenant = await db.Tenants.SingleAsync();
            Assert.Equal(TenantA, upgradedTenant.Id);
            Assert.Equal(pendingAtUtc, upgradedTenant.DeletionRequestedAtUtc);
            Assert.Equal("legacy-owner", (await db.ExternalIdentities.SingleAsync()).Subject);
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }

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
            Assert.Null(metadata.LastSuccessfulProviderRetrievalAtUtc);
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
                VALUES ({TenantB}, {"Tenant B"}, {false}, {new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc)});
                INSERT INTO "Tags" ("Id", "TenantId", "Name")
                VALUES
                    ({7001}, {TenantA}, {"Local tag"}),
                    ({7002}, {TenantB}, {"Foreign legacy tag"});
                """);
            var game = new UnifiedGame { Title = "Existing game", Slug = "existing-game" };
            tenantA.UnifiedGames.Add(game);
            await tenantA.SaveChangesAsync();

            const int localTagId = 7001;
            const int foreignTagId = 7002;

            await tenantA.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "TagUnifiedGame" ("GamesId", "TagsId")
                VALUES ({game.Id}, {localTagId}), ({game.Id}, {foreignTagId})
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
            Assert.All(upgradedGame.Tags, tag => Assert.False(tag.IsCustomDefinition));
            var upgradedAssignments = await tenantA.Set<TagAssignment>().ToListAsync();
            Assert.Equal(2, upgradedAssignments.Count);
            Assert.All(upgradedAssignments, assignment => Assert.False(assignment.IsCustom));

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
