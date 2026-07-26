using GameHub.Core.Data;
using GameHub.Core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameHub.Tests;

/// <summary>Direct personal-tenant JIT, credential-boundary, and concurrency tests.</summary>
public sealed class OidcJitProvisioningTests
{
    private const string Issuer = "https://identity.example.test";
    private static readonly DateTimeOffset FixedNow =
        new(2026, 6, 24, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ProvisionAsync_OidcOrProvisioningWithoutActiveGate_CreatesNothing()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var identity = new ValidatedOidcIdentity(
            Issuer,
            "subject-a",
            "shared@example.com",
            ProviderSessionId: "provider-session-a");

        var gateDisabled = await fixture.CreateService(multiUserEnabled: false).ProvisionAsync(identity);
        var provisioningDisabled = await fixture.CreateService(
            multiUserEnabled: true,
            OidcProvisioningMode.Disabled).ProvisionAsync(identity);

        Assert.False(gateDisabled.Succeeded);
        Assert.False(provisioningDisabled.Succeeded);
        Assert.Equal(0, await fixture.Db.UserAccounts.CountAsync());
        Assert.Equal(0, await fixture.Db.ExternalIdentities.CountAsync());
        Assert.Equal(0, await fixture.Db.RefreshTokenSessions.CountAsync());
        Assert.Equal(0, await fixture.Db.ActivityLogs.IgnoreQueryFilters().CountAsync());
        Assert.Equal(1, await fixture.Db.Tenants.CountAsync());
    }

    [Fact]
    public async Task ProvisionAsync_ActivePersonalJit_CreatesOneServerOwnedPersonalTenantAtomically()
    {
        await using var fixture = await TestFixture.CreateAsync();

        var result = await fixture.CreateService().ProvisionAsync(
            new ValidatedOidcIdentity(
                Issuer,
                "subject-a",
                null,
                ProviderSessionId: "provider-session-a"));

        Assert.True(result.Succeeded);
        var account = await fixture.Db.UserAccounts.Include(candidate => candidate.Tenant).SingleAsync();
        Assert.Equal(UserAccountType.ExternalOidc, account.AccountType);
        Assert.Null(account.Email);
        Assert.Null(account.NormalizedEmail);
        Assert.Null(account.PasswordHash);
        Assert.NotEqual(Guid.Empty, account.Id);
        Assert.NotEqual(TenantDefaults.DefaultTenantId, account.TenantId);
        Assert.False(account.Tenant!.PlatformSyncEnabled);
        Assert.Single(await fixture.Db.ExternalIdentities.ToListAsync());
        Assert.Single(await fixture.Db.RefreshTokenSessions.ToListAsync());
        var activity = await fixture.Db.ActivityLogs.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(account.TenantId, activity.TenantId);
        Assert.Equal("OidcJitProvisioned", activity.EventType);
        Assert.DoesNotContain("subject-a", activity.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProvisionAsync_EqualEmailClaimsAndProviderAuthorizationClaims_DoNotSelectOrMergeTenant()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var service = fixture.CreateService();

        var first = await service.ProvisionAsync(
            new ValidatedOidcIdentity(
                Issuer,
                "subject-a",
                "shared@example.com",
                ProviderSessionId: "provider-session-a"));
        var second = await service.ProvisionAsync(
            new ValidatedOidcIdentity(
                Issuer,
                "subject-b",
                "shared@example.com",
                ProviderSessionId: "provider-session-b"));

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.NotEqual(first.Account!.TenantId, second.Account!.TenantId);
        Assert.Equal(2, await fixture.Db.UserAccounts.CountAsync());
        Assert.Equal(2, await fixture.Db.ExternalIdentities.CountAsync());
        Assert.All(await fixture.Db.UserAccounts.ToListAsync(), account => Assert.Null(account.Email));
    }

    [Fact]
    public async Task ProvisionAsync_DeletedIdentityTombstone_RemainsBlocked()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Db.DeletedExternalIdentities.Add(new DeletedExternalIdentity
        {
            Id = Guid.NewGuid(),
            Issuer = Issuer,
            Subject = "deleted-subject",
            DeletedAtUtc = FixedNow.UtcDateTime
        });
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.CreateService().ProvisionAsync(
            new ValidatedOidcIdentity(
                Issuer,
                "deleted-subject",
                null,
                ProviderSessionId: "provider-session-deleted"));

        Assert.False(result.Succeeded);
        Assert.Empty(fixture.Db.UserAccounts);
        Assert.Single(fixture.Db.DeletedExternalIdentities);
    }

    [Fact]
    public async Task ExternalAccount_LocalAuthenticationPasswordChangeAndUnconfirmedDeletionAreRejected()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var provisioned = await fixture.CreateService().ProvisionAsync(
            new ValidatedOidcIdentity(
                Issuer,
                "subject-a",
                "shared@example.com",
                ProviderSessionId: "provider-session-a"));
        var accounts = new AccountService(
            fixture.Db,
            new PasswordHasher<UserAccount>(),
            fixture.Time);

        Assert.Null(await accounts.AuthenticateAsync("shared@example.com", "not-a-local-password"));
        var passwordChange = await accounts.ChangePasswordAsync(
            provisioned.Account!.Id,
            provisioned.Account.TenantId,
            "anything",
            "another long password");
        var unconfirmedDeletion = await accounts.PrepareDeletionAsync(
            provisioned.Account.Id,
            provisioned.Account.TenantId,
            "dummy password");
        var confirmedDeletion = await accounts.PrepareDeletionAsync(
            provisioned.Account.Id,
            provisioned.Account.TenantId,
            null,
            authorization: AccountDeletionAuthorization.FreshExternalOidc);

        Assert.Equal("local_password_not_available", passwordChange.ErrorCode);
        Assert.Equal(AccountDeletionPreparationResult.InvalidCurrentPassword, unconfirmedDeletion);
        Assert.Equal(AccountDeletionPreparationResult.Prepared, confirmedDeletion);
    }

    [Fact]
    public async Task Database_AccountTypeCredentialConstraintsRejectInvalidRows()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Invalid",
            PlatformSyncEnabled = false,
            CreatedAtUtc = FixedNow.UtcDateTime
        };
        fixture.Db.UserAccounts.Add(new UserAccount
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Tenant = tenant,
            AccountType = UserAccountType.ExternalOidc,
            Email = "profile@example.com",
            NormalizedEmail = "PROFILE@EXAMPLE.COM",
            DisplayName = "Invalid",
            PasswordHash = "must-not-exist",
            CreatedAtUtc = FixedNow.UtcDateTime
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task Readiness_PersonalJitAllowsEmptyDatabaseButDisabledModeStillRequiresAccount()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var probe = new TestMultiUserIngestReadinessProbe(enabled: true);
        var jit = new MultiUserEnablementService(
            fixture.Db,
            new MultiUserEnablementSettings(new GameHub.Shared.Authentication.MultiUserMode(true), []),
            TestFixture.CreateOidcSettings(OidcProvisioningMode.PersonalTenantJit),
            probe);
        var disabled = new MultiUserEnablementService(
            fixture.Db,
            new MultiUserEnablementSettings(new GameHub.Shared.Authentication.MultiUserMode(true), []),
            TestFixture.CreateOidcSettings(OidcProvisioningMode.Disabled),
            probe);

        Assert.True((await jit.EvaluateAsync()).ReadinessSatisfied);
        Assert.Contains("no_active_account", (await disabled.EvaluateAsync()).Blockers);
        Assert.Empty(fixture.Db.UserAccounts);
    }

    [Fact]
    public async Task ProvisionAsync_ParallelFirstLogins_CreateNoDuplicateOrOrphanRows()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"gamehub-jit-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Default Timeout=30";
        try
        {
            await using (var migrationDb = CreateFileContext(connectionString, out _))
            {
                await migrationDb.Database.MigrateAsync();
            }

            async Task<OidcJitProvisioningResult> ProvisionAsync()
            {
                var db = CreateFileContext(connectionString, out var execution);
                await using (db)
                {
                    var refresh = new RefreshTokenService(
                        db,
                        AccountServiceTests.CreateJwtSettings(),
                        new AccountServiceTests.FixedTimeProvider(FixedNow));
                    var service = new OidcJitProvisioningService(
                        db,
                        refresh,
                        execution,
                        TestFixture.CreateOidcSettings(OidcProvisioningMode.PersonalTenantJit),
                        new MultiUserEnablementSettings(
                            new GameHub.Shared.Authentication.MultiUserMode(true),
                            []),
                        new AccountServiceTests.FixedTimeProvider(FixedNow),
                        NullLogger<OidcJitProvisioningService>.Instance);
                    return await service.ProvisionAsync(
                        new ValidatedOidcIdentity(
                            Issuer,
                            "parallel-subject",
                            "shared@example.com",
                            ProviderSessionId: "parallel-provider-session"));
                }
            }

            var results = await Task.WhenAll(ProvisionAsync(), ProvisionAsync());

            await using var verification = CreateFileContext(connectionString, out _);
            Assert.Contains(results, result => result.Succeeded);
            Assert.Equal(2, await verification.Tenants.CountAsync());
            Assert.Single(await verification.UserAccounts.ToListAsync());
            Assert.Single(await verification.ExternalIdentities.ToListAsync());
            Assert.Single(await verification.ActivityLogs.IgnoreQueryFilters().ToListAsync());
            Assert.InRange(await verification.RefreshTokenSessions.CountAsync(), 1, 2);
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    private static CoreContext CreateFileContext(
        string connectionString,
        out TenantExecutionContext tenantExecution)
    {
        tenantExecution = new TenantExecutionContext();
        var tenantContext = new AuthenticatedTenantContext(
            new HttpContextAccessor(),
            tenantExecution);
        var options = new DbContextOptionsBuilder<CoreContext>()
            .UseSqlite(connectionString)
            .ConfigureWarnings(warnings =>
                warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new CoreContext(options, tenantContext);
    }

    private sealed class TestFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly TenantExecutionContext _tenantExecution;

        private TestFixture(
            SqliteConnection connection,
            CoreContext db,
            TenantExecutionContext tenantExecution)
        {
            _connection = connection;
            Db = db;
            _tenantExecution = tenantExecution;
        }

        public CoreContext Db { get; }

        public TimeProvider Time { get; } = new AccountServiceTests.FixedTimeProvider(FixedNow);

        public static async Task<TestFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var tenantExecution = new TenantExecutionContext();
            var tenantContext = new AuthenticatedTenantContext(
                new HttpContextAccessor(),
                tenantExecution);
            var options = new DbContextOptionsBuilder<CoreContext>()
                .UseSqlite(connection)
                .ConfigureWarnings(warnings =>
                    warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options;
            var db = new CoreContext(options, tenantContext);
            await db.Database.EnsureCreatedAsync();
            return new TestFixture(connection, db, tenantExecution);
        }

        public OidcJitProvisioningService CreateService(
            bool multiUserEnabled = true,
            OidcProvisioningMode provisioningMode = OidcProvisioningMode.PersonalTenantJit)
        {
            var oidc = CreateOidcSettings(provisioningMode);
            var refresh = new RefreshTokenService(
                Db,
                AccountServiceTests.CreateJwtSettings(),
                Time);
            return new OidcJitProvisioningService(
                Db,
                refresh,
                _tenantExecution,
                oidc,
                new MultiUserEnablementSettings(
                    new GameHub.Shared.Authentication.MultiUserMode(multiUserEnabled),
                    []),
                Time,
                NullLogger<OidcJitProvisioningService>.Instance);
        }

        public static OidcSettings CreateOidcSettings(OidcProvisioningMode provisioningMode) =>
            new(
                true,
                Issuer,
                "gamehub-client",
                $"{Issuer}/.well-known/openid-configuration",
                ["RS256"],
                new string('B', 32),
                provisioningMode);

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
