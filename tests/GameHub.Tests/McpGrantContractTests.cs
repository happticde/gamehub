using System.Data.Common;
using GameHub.Core.Data;
using GameHub.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace GameHub.Tests;

/// <summary>Real database constraints and lifecycle contracts for independent MCP connections.</summary>
public sealed class McpGrantContractTests
{
    private static readonly DateTime Now = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Migration_FreshAndUpgrade_PreservesBrowserAndProviderState(bool postgres, bool upgrade)
    {
        await using var fixture = await Fixture.CreateAsync(postgres, upgrade);
        if (fixture is null) return;
        var db = fixture.Db;
        var grant = await fixture.AddGrantAsync(fixture.A);
        var second = await fixture.AddGrantAsync(fixture.A);
        Assert.NotEqual(grant.AccountSessionId, second.AccountSessionId);
        Assert.Equal(grant.OidcProviderSessionId, second.OidcProviderSessionId);
        Assert.NotEqual(fixture.A.BrowserId, grant.AccountSessionId);
        Assert.Equal(2, await db.UserAccounts.CountAsync());
        Assert.Equal((long)int.MaxValue + 2, grant.ApprovedSessionVersion);
        Assert.Equal("preserved-password-hash", (await db.UserAccounts.FindAsync(fixture.A.AccountId))!.PasswordHash);
        Assert.Equal(fixture.A.BrowserId, (await db.OidcProviderSessions.FindAsync(fixture.A.ProviderId))!.AccountSessionId);
        Assert.Equal(new string('A', 64), (await db.RefreshTokenSessions.SingleAsync()).TokenHash);
        Assert.Null((await db.RefreshTokenSessions.SingleAsync()).RevokedAtUtc);
        Assert.Null((await db.AccountSessions.FindAsync(fixture.A.BrowserId))!.RevokedAtUtc);
        Assert.Equal(2, await db.McpGrants.CountAsync());
        Assert.Equal(2, await db.Set<McpProtocolAuthorization>().CountAsync());
        // Nullable unmapped provider tombstones must survive adoption of the composite reference.
        Assert.Null((await db.OidcProviderSessions.SingleAsync(row => row.Id == fixture.TombstoneId)).TenantId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ownership_ForgedReferencesAndDuplicates_AreRejectedByDatabase(bool postgres)
    {
        await using var fixture = await Fixture.CreateAsync(postgres);
        if (fixture is null) return;
        var db = fixture.Db;
        var foreign = await fixture.AddGrantAsync(fixture.B);
        foreach (var corruption in new Action<McpGrant>[]
        {
            row => row.OidcProviderSessionId = fixture.B.ProviderId,
            row => row.OidcProviderSessionId = fixture.TombstoneId,
            row => row.AccountSessionId = foreign.AccountSessionId,
            row => row.ProtocolAuthorizationId = foreign.ProtocolAuthorizationId,
            row => row.TenantId = fixture.B.TenantId,
            row => row.UserAccountId = fixture.B.AccountId
        })
        {
            var grant = await fixture.NewGrantAsync(fixture.A);
            corruption(grant);
            db.McpGrants.Add(grant);
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();
        }
        var valid = await fixture.AddGrantAsync(fixture.A);
        var duplicate = Copy(valid);
        db.McpGrants.Add(duplicate);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        var unclaimedFamilyId = Guid.NewGuid();
        db.AccountSessions.Add(new AccountSession
        {
            Id = unclaimedFamilyId, TenantId = fixture.A.TenantId,
            UserAccountId = fixture.A.AccountId, CreatedAtUtc = Now
        });
        await db.SaveChangesAsync();
        db.Set<McpProtocolAuthorization>().Add(new McpProtocolAuthorization
        {
            Id = Guid.NewGuid().ToString(), TenantId = fixture.B.TenantId,
            UserAccountId = fixture.A.AccountId, AccountSessionId = unclaimedFamilyId
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        db.AccountSessions.Add(new AccountSession
        {
            Id = Guid.NewGuid(), TenantId = fixture.B.TenantId,
            UserAccountId = fixture.A.AccountId, CreatedAtUtc = Now
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Migration_DownAndUp_PreservesExistingBrowserData(bool postgres)
    {
        await using var fixture = await Fixture.CreateAsync(postgres);
        if (fixture is null) return;
        var migrator = fixture.Db.Database.GetService<IMigrator>();
        await migrator.MigrateAsync(postgres
            ? "20260730193116_DistinguishOidcProviderRevocationPostgres"
            : "20260730193115_DistinguishOidcProviderRevocationSqlite");
        await migrator.MigrateAsync();
        Assert.Equal(2, await fixture.Db.UserAccounts.CountAsync());
        Assert.Single(await fixture.Db.RefreshTokenSessions.ToListAsync());
        Assert.Equal(fixture.A.BrowserId,
            (await fixture.Db.OidcProviderSessions.FindAsync(fixture.A.ProviderId))!.AccountSessionId);
        Assert.Empty(await fixture.Db.McpGrants.ToListAsync());
        await fixture.AddGrantAsync(fixture.A);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Consent_InvalidValuesAndChanges_AreRejectedWithoutExtendingExpiry(bool postgres)
    {
        await using var fixture = await Fixture.CreateAsync(postgres);
        if (fixture is null) return;
        var db = fixture.Db;
        foreach (var corruption in new Action<McpGrant>[]
        {
            row => row.Scopes = "tracking:write",
            row => row.Scopes = "library:read admin",
            row => row.Resource = "https://core.example/api https://core.example/mcp",
            row => row.Resource = "http://core.example/api",
            row => row.ClientId = "",
            row => row.ExpiresAtUtc = row.CreatedAtUtc,
            row => row.ApprovedSessionVersion = -1,
            row => row.RevokedAtUtc = row.CreatedAtUtc.AddSeconds(-1)
        })
        {
            var grant = await fixture.NewGrantAsync(fixture.A);
            corruption(grant);
            db.McpGrants.Add(grant);
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();
        }
        var valid = await fixture.AddGrantAsync(fixture.A);
        await Assert.ThrowsAnyAsync<DbException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "McpGrants" SET "ExpiresAtUtc" = {Now.AddDays(60)} WHERE "Id" = {valid.Id};
            """));
        await Assert.ThrowsAnyAsync<DbException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "McpGrants" SET "Scopes" = {McpPermissions.ReadAndWrite} WHERE "Id" = {valid.Id};
            """));
        await Assert.ThrowsAnyAsync<DbException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "McpGrants" SET "ApprovedSessionVersion" = {0L} WHERE "Id" = {valid.Id};
            """));
        db.ChangeTracker.Clear();
        Assert.Equal(Now.AddDays(30), (await db.McpGrants.FindAsync(valid.Id))!.ExpiresAtUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Family_BrowserCredentialSubstitution_IsRejectedInBothDirections(bool postgres)
    {
        await using var fixture = await Fixture.CreateAsync(postgres);
        if (fixture is null) return;
        var db = fixture.Db;
        var grant = await fixture.AddGrantAsync(fixture.A);
        await Assert.ThrowsAnyAsync<DbException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "RefreshTokenSessions" SET "AccountSessionId" = {grant.AccountSessionId};
            """));
        await Assert.ThrowsAnyAsync<DbException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "OidcProviderSessions" SET "AccountSessionId" = {grant.AccountSessionId}
            WHERE "Id" = {fixture.A.ProviderId};
            """));
        var browserGrant = Copy(grant);
        browserGrant.AccountSessionId = fixture.A.BrowserId;
        db.McpGrants.Add(browserGrant);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        Assert.Equal(fixture.A.BrowserId, (await db.RefreshTokenSessions.SingleAsync()).AccountSessionId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revocation_LocalBrowserLogoutPreservesGrants_ProviderAndAccountRevokeOwnedProtocol(bool postgres)
    {
        await using var fixture = await Fixture.CreateAsync(postgres);
        if (fixture is null) return;
        var db = fixture.Db;
        var first = await fixture.AddGrantAsync(fixture.A);
        var second = await fixture.AddGrantAsync(fixture.A);
        var foreign = await fixture.AddGrantAsync(fixture.B);
        var when = Now.AddMinutes(1);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            Assert.False(await AccountSessionRevocation.RevokeFamilyAsync(
                db, foreign.AccountSessionId, fixture.A.AccountId, fixture.A.TenantId, when));
            await AccountSessionRevocation.RevokeFamilyAsync(
                db, fixture.A.BrowserId, fixture.A.AccountId, fixture.A.TenantId, when);
            await transaction.CommitAsync();
        }
        db.ChangeTracker.Clear();
        Assert.All(await db.McpGrants.ToListAsync(), row => Assert.Null(row.RevokedAtUtc));
        Assert.Null((await db.OidcProviderSessions.FindAsync(fixture.A.ProviderId))!.ReissueBlockedAtUtc);

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await AccountSessionRevocation.RevokeFamilyAsync(
                db, first.AccountSessionId, fixture.A.AccountId, fixture.A.TenantId, when);
            await transaction.CommitAsync();
        }
        db.ChangeTracker.Clear();
        Assert.Equal("revoked", (await db.Set<McpProtocolAuthorization>().FindAsync(first.ProtocolAuthorizationId))!.Status);
        Assert.Null((await db.McpGrants.FindAsync(second.Id))!.RevokedAtUtc);

        // Exercise the actual provider-logout path after the browser family already ended.
        var execution = new TenantExecutionContext();
        var logout = new OidcBackChannelLogoutService(db, execution,
            new AccountServiceTests.FixedTimeProvider(new DateTimeOffset(when)),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OidcBackChannelLogoutService>.Instance);
        await logout.ProcessAsync(new ValidatedOidcLogoutToken(
            "https://provider.example", null, "mcp-contract-logout", new DateTimeOffset(when), fixture.A.Sid));
        db.ChangeTracker.Clear();
        Assert.All(await db.McpGrants.Where(row => row.UserAccountId == fixture.A.AccountId).ToListAsync(),
            row => Assert.Equal(when, row.RevokedAtUtc));
        Assert.All(await db.Set<McpProtocolToken>().Where(row => row.Authorization!.UserAccountId == fixture.A.AccountId).ToListAsync(),
            row => Assert.Equal("revoked", row.Status));
        Assert.Equal("valid", (await db.Set<McpProtocolAuthorization>().FindAsync(foreign.ProtocolAuthorizationId))!.Status);
        Assert.Null((await db.McpGrants.FindAsync(foreign.Id))!.RevokedAtUtc);

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await AccountSessionRevocation.RevokeAllFamiliesAsync(db, fixture.B.AccountId, fixture.B.TenantId, when);
            await transaction.CommitAsync();
        }
        db.ChangeTracker.Clear();
        Assert.Equal("revoked", (await db.Set<McpProtocolAuthorization>().FindAsync(foreign.ProtocolAuthorizationId))!.Status);
        await Assert.ThrowsAnyAsync<DbException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "McpGrants" SET "RevokedAtUtc" = NULL WHERE "Id" = {foreign.Id};
            """));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deletion_OwnedProtocolRemovedBeforeTombstone_ForeignConnectionSurvives(bool postgres)
    {
        await using var fixture = await Fixture.CreateAsync(postgres);
        if (fixture is null) return;
        var db = fixture.Db;
        await fixture.AddGrantAsync(fixture.A);
        var foreign = await fixture.AddGrantAsync(fixture.B);
        await Assert.ThrowsAnyAsync<DbException>(() => ClearProviderAsync());
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await McpGrantLifecycle.DeleteOwnedAsync(db, fixture.A.AccountId, fixture.A.TenantId);
            await ClearProviderAsync();
            await transaction.CommitAsync();
        }
        db.ChangeTracker.Clear();
        Assert.Single(await db.McpGrants.ToListAsync());
        Assert.Equal(foreign.Id, (await db.McpGrants.SingleAsync()).Id);
        Assert.Single(await db.Set<McpProtocolAuthorization>().ToListAsync());
        Assert.Equal(2, await db.Set<McpProtocolToken>().CountAsync());
        var tombstone = await db.OidcProviderSessions.FindAsync(fixture.A.ProviderId);
        Assert.Null(tombstone!.TenantId);
        Assert.NotNull(tombstone.ReissueBlockedAtUtc);

        Task<int> ClearProviderAsync() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "OidcProviderSessions" SET "AccountSessionId" = NULL, "ExternalIdentityId" = NULL,
            "TenantId" = NULL, "UserAccountId" = NULL, "RevokedAtUtc" = {Now}, "ReissueBlockedAtUtc" = {Now}
            WHERE "Id" = {fixture.A.ProviderId};
            """);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevokeOthers_TransactionRollbackAndCommit_PreserveCurrentBrowserAndForeignOwner(bool postgres)
    {
        await using var fixture = await Fixture.CreateAsync(postgres);
        if (fixture is null) return;
        var db = fixture.Db;
        var grant = await fixture.AddGrantAsync(fixture.A);
        var foreign = await fixture.AddGrantAsync(fixture.B);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await RevokeAsync();
            await transaction.RollbackAsync();
        }
        db.ChangeTracker.Clear();
        Assert.Null((await db.McpGrants.FindAsync(grant.Id))!.RevokedAtUtc);
        Assert.Null((await db.AccountSessions.FindAsync(grant.AccountSessionId))!.RevokedAtUtc);
        Assert.Equal("valid", (await db.Set<McpProtocolAuthorization>().FindAsync(grant.ProtocolAuthorizationId))!.Status);
        Assert.All(await db.Set<McpProtocolToken>().ToListAsync(), row => Assert.Equal("valid", row.Status));
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await RevokeAsync();
            await transaction.CommitAsync();
        }
        db.ChangeTracker.Clear();
        Assert.NotNull((await db.McpGrants.FindAsync(grant.Id))!.RevokedAtUtc);
        Assert.Null((await db.AccountSessions.FindAsync(fixture.A.BrowserId))!.RevokedAtUtc);
        Assert.Null((await db.RefreshTokenSessions.SingleAsync()).RevokedAtUtc);
        Assert.Null((await db.McpGrants.FindAsync(foreign.Id))!.RevokedAtUtc);

        Task<int> RevokeAsync() => AccountSessionRevocation.RevokeOtherFamiliesAsync(
            db, fixture.A.BrowserId, fixture.A.AccountId, fixture.A.TenantId, Now.AddMinutes(1));
    }

    private static McpGrant Copy(McpGrant row) => new()
    {
        Id = Guid.NewGuid(), TenantId = row.TenantId, UserAccountId = row.UserAccountId,
        AccountSessionId = row.AccountSessionId, ProtocolAuthorizationId = row.ProtocolAuthorizationId,
        OidcProviderSessionId = row.OidcProviderSessionId, ClientId = row.ClientId,
        Resource = row.Resource, Scopes = row.Scopes, ApprovedSessionVersion = row.ApprovedSessionVersion,
        CreatedAtUtc = row.CreatedAtUtc, ExpiresAtUtc = row.ExpiresAtUtc
    };

    private sealed record Owner(Guid AccountId, Guid TenantId, Guid BrowserId, Guid ProviderId, string Sid);

    private sealed class Fixture(CoreContext db, SqliteConnection? connection, bool postgres) : IAsyncDisposable
    {
        public CoreContext Db { get; } = db;
        public Owner A { get; private set; } = null!;
        public Owner B { get; private set; } = null!;
        public Guid TombstoneId { get; } = Guid.NewGuid();

        public static async Task<Fixture?> CreateAsync(bool postgres, bool upgrade = false)
        {
            var options = new DbContextOptionsBuilder<CoreContext>()
                .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
            SqliteConnection? connection = null;
            if (postgres)
            {
                var configured = Environment.GetEnvironmentVariable("GAMEHUB_TEST_MCP_POSTGRES_CONNECTION");
                if (string.IsNullOrWhiteSpace(configured)) return null;
                var builder = new NpgsqlConnectionStringBuilder(configured)
                {
                    Database = "mcp_contract_" + Guid.NewGuid().ToString("N")
                };
                options.UseNpgsql(builder.ConnectionString);
            }
            else
            {
                connection = new SqliteConnection("Data Source=:memory:");
                await connection.OpenAsync();
                options.UseSqlite(connection);
            }
            var fixture = new Fixture(new CoreContext(options.Options, new DefaultTenantContext()), connection, postgres);
            try
            {
                var migrator = fixture.Db.Database.GetService<IMigrator>();
                if (upgrade)
                    await migrator.MigrateAsync(postgres
                        ? "20260730193116_DistinguishOidcProviderRevocationPostgres"
                        : "20260730193115_DistinguishOidcProviderRevocationSqlite");
                else
                    await migrator.MigrateAsync();
                fixture.A = await fixture.SeedOwnerAsync(TenantDefaults.DefaultTenantId, "a");
                fixture.B = await fixture.SeedOwnerAsync(Guid.NewGuid(), "b");
                fixture.Db.OidcProviderSessions.Add(new OidcProviderSession
                {
                    Id = fixture.TombstoneId, SessionKeyHash = new string('C', 64), CreatedAtUtc = Now,
                    RevokedAtUtc = Now, ReissueBlockedAtUtc = Now
                });
                fixture.Db.RefreshTokenSessions.Add(new RefreshTokenSession
                {
                    Id = Guid.NewGuid(), UserAccountId = fixture.A.AccountId,
                    AccountSessionId = fixture.A.BrowserId, TokenHash = new string('A', 64),
                    CreatedAtUtc = Now, ExpiresAtUtc = Now.AddDays(30), SessionVersion = (long)int.MaxValue + 2
                });
                await fixture.Db.SaveChangesAsync();
                if (upgrade) await migrator.MigrateAsync();
                fixture.Db.ChangeTracker.Clear();
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        private async Task<Owner> SeedOwnerAsync(Guid tenantId, string name)
        {
            if (tenantId != TenantDefaults.DefaultTenantId)
                Db.Tenants.Add(new Tenant { Id = tenantId, Name = name, CreatedAtUtc = Now });
            var accountId = Guid.NewGuid();
            var identityId = Guid.NewGuid();
            var browserId = Guid.NewGuid();
            var providerId = Guid.NewGuid();
            var sid = "contract-sid-" + name;
            Db.UserAccounts.Add(new UserAccount
            {
                Id = accountId, TenantId = tenantId, Email = name + "@example.test",
                NormalizedEmail = name.ToUpperInvariant() + "@EXAMPLE.TEST", DisplayName = name,
                PasswordHash = "preserved-password-hash", CreatedAtUtc = Now,
                SessionVersion = (long)int.MaxValue + 2
            });
            Db.ExternalIdentities.Add(new ExternalIdentity
            {
                Id = identityId, UserAccountId = accountId, TenantId = tenantId,
                Issuer = "https://provider.example", Subject = name, CreatedAtUtc = Now
            });
            Db.AccountSessions.Add(new AccountSession
            {
                Id = browserId, UserAccountId = accountId, TenantId = tenantId, CreatedAtUtc = Now
            });
            Db.OidcProviderSessions.Add(new OidcProviderSession
            {
                Id = providerId, ExternalIdentityId = identityId, AccountSessionId = browserId,
                UserAccountId = accountId, TenantId = tenantId,
                SessionKeyHash = OidcProviderSessionKey.Hash("https://provider.example", sid), CreatedAtUtc = Now
            });
            await Db.SaveChangesAsync();
            return new Owner(accountId, tenantId, browserId, providerId, sid);
        }

        public async Task<McpGrant> NewGrantAsync(Owner owner)
        {
            var familyId = Guid.NewGuid();
            var authorizationId = Guid.NewGuid().ToString();
            Db.AccountSessions.Add(new AccountSession
            {
                Id = familyId, TenantId = owner.TenantId, UserAccountId = owner.AccountId, CreatedAtUtc = Now
            });
            Db.Set<McpProtocolAuthorization>().Add(new McpProtocolAuthorization
            {
                Id = authorizationId, TenantId = owner.TenantId, UserAccountId = owner.AccountId,
                AccountSessionId = familyId, Status = "valid", CreationDate = Now, Type = "permanent"
            });
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return new McpGrant
            {
                Id = Guid.NewGuid(), TenantId = owner.TenantId, UserAccountId = owner.AccountId,
                AccountSessionId = familyId, ProtocolAuthorizationId = authorizationId,
                OidcProviderSessionId = owner.ProviderId, ClientId = "gamehub-docker",
                Resource = "https://core.example/api", Scopes = McpPermissions.LibraryRead,
                ApprovedSessionVersion = (long)int.MaxValue + 2,
                CreatedAtUtc = Now, ExpiresAtUtc = Now.AddDays(30)
            };
        }

        public async Task<McpGrant> AddGrantAsync(Owner owner)
        {
            var grant = await NewGrantAsync(owner);
            Db.McpGrants.Add(grant);
            await Db.SaveChangesAsync();
            var authorization = await Db.Set<McpProtocolAuthorization>().FindAsync(grant.ProtocolAuthorizationId);
            foreach (var type in new[] { "access_token", "refresh_token" })
                Db.Set<McpProtocolToken>().Add(new McpProtocolToken
                {
                    Id = Guid.NewGuid().ToString(), Authorization = authorization,
                    Type = type, Status = "valid", CreationDate = Now, ExpirationDate = Now.AddMinutes(10)
                });
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return grant;
        }

        public async ValueTask DisposeAsync()
        {
            if (postgres) await Db.Database.EnsureDeletedAsync();
            await Db.DisposeAsync();
            if (connection is not null) await connection.DisposeAsync();
        }
    }
}
