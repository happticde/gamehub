using GameHub.Core.Data;
using GameHub.Core.Services;
using GameHub.Shared.Authentication;
using GameHub.Tests.Helpers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace GameHub.Tests;

/// <summary>
/// Tests provider-neutral listing, targeted revocation, tenant isolation, and refresh races.
/// </summary>
public sealed class AccountSessionManagementTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private const string Issuer = "https://identity.example.test";
    private static readonly DateTimeOffset FixedNow =
        new(2026, 7, 30, 10, 0, 0, TimeSpan.Zero);

    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly CoreContext _db;
    private readonly MutableTimeProvider _time = new(FixedNow);
    private readonly AccountService _accounts;
    private readonly RefreshTokenService _refreshTokens;
    private readonly AccountSessionManagementService _sessions;

    public AccountSessionManagementTests()
    {
        (_db, _connection) = DatabaseFixture.CreateCoreContext();
        _accounts = new AccountService(_db, new PasswordHasher<UserAccount>(), _time);
        _refreshTokens = new RefreshTokenService(
            _db,
            AccountServiceTests.CreateJwtSettings(),
            _time);
        _sessions = new AccountSessionManagementService(_db, _time);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task ListActive_OwnSessions_MarksCurrentFirstThenNewest()
    {
        var account = await CreateAccountAsync();
        var current = await _refreshTokens.IssueAsync(account);
        _time.UtcNow = FixedNow.AddMinutes(1);
        var newest = await _refreshTokens.IssueAsync(account);
        var tenantB = await _accounts.ProvisionTenantAsync(
            "Tenant B",
            "tenant-b@example.com",
            Password,
            "Tenant B Owner");
        await _refreshTokens.IssueAsync(tenantB.Account!);

        var sessions = await _sessions.ListActiveAsync(
            account.Id,
            account.TenantId,
            current.AccountSessionId);

        Assert.NotNull(sessions);
        Assert.Equal(2, sessions.Count);
        Assert.Equal(current.AccountSessionId, sessions[0].SessionId);
        Assert.True(sessions[0].IsCurrent);
        Assert.Equal(FixedNow, sessions[0].CreatedAtUtc);
        Assert.Equal(newest.AccountSessionId, sessions[1].SessionId);
        Assert.False(sessions[1].IsCurrent);
        Assert.Equal(FixedNow.AddMinutes(1), sessions[1].CreatedAtUtc);
    }

    [Fact]
    public async Task Revoke_LocalAndOidcFamilies_InvalidatesOnlyEachTarget()
    {
        var account = await CreateAccountAsync();
        var current = await _refreshTokens.IssueAsync(account);
        var localTarget = await _refreshTokens.IssueAsync(account);
        var oidcTarget = await IssueOidcSessionAsync(account);

        await _sessions.RevokeAsync(
            account.Id,
            account.TenantId,
            localTarget.AccountSessionId);

        var accessValidation = new AccountSessionValidationService(_db);
        Assert.False(await accessValidation.IsActiveAsync(
            account.Id,
            account.TenantId,
            account.SessionVersion,
            localTarget.AccountSessionId));
        Assert.True(await accessValidation.IsActiveAsync(
            account.Id,
            account.TenantId,
            account.SessionVersion,
            current.AccountSessionId));
        Assert.False((await _refreshTokens.RotateAsync(localTarget.RefreshToken)).Rotated);
        Assert.True((await _refreshTokens.RotateAsync(current.RefreshToken)).Rotated);

        await _sessions.RevokeAsync(
            account.Id,
            account.TenantId,
            oidcTarget.AccountSessionId);

        Assert.False((await _refreshTokens.RotateAsync(oidcTarget.RefreshToken)).Rotated);
        Assert.False(await accessValidation.IsActiveAsync(
            account.Id,
            account.TenantId,
            account.SessionVersion,
            oidcTarget.AccountSessionId));
        var providerState = await _db.OidcProviderSessions.AsNoTracking().SingleAsync();
        Assert.Equal(oidcTarget.AccountSessionId, providerState.AccountSessionId);
        Assert.NotNull(providerState.RevokedAtUtc);
        Assert.Null(providerState.ReissueBlockedAtUtc);
        Assert.Single(
            await _db.AccountSessions
                .Where(session => session.RevokedAtUtc == null)
                .ToListAsync());
    }

    [Fact]
    public async Task Revoke_OidcFamily_FreshLoginWithSameProviderSidCreatesNewFamily()
    {
        var account = await CreateAccountAsync();
        var revoked = await IssueOidcSessionAsync(account);

        await _sessions.RevokeAsync(
            account.Id,
            account.TenantId,
            revoked.AccountSessionId);
        _time.UtcNow = FixedNow.AddMinutes(1);

        var replacement = await IssueOidcSessionAsync(account);

        Assert.NotEqual(revoked.AccountSessionId, replacement.AccountSessionId);
        Assert.False((await _refreshTokens.RotateAsync(revoked.RefreshToken)).Rotated);
        Assert.True((await _refreshTokens.RotateAsync(replacement.RefreshToken)).Rotated);

        var providerState = await _db.OidcProviderSessions.AsNoTracking().SingleAsync();
        Assert.Equal(replacement.AccountSessionId, providerState.AccountSessionId);
        Assert.Null(providerState.RevokedAtUtc);
        Assert.Null(providerState.ReissueBlockedAtUtc);
        Assert.NotNull((await _db.AccountSessions.AsNoTracking().SingleAsync(
            session => session.Id == revoked.AccountSessionId)).RevokedAtUtc);
        Assert.Null((await _db.AccountSessions.AsNoTracking().SingleAsync(
            session => session.Id == replacement.AccountSessionId)).RevokedAtUtc);
    }

    [Fact]
    public async Task RevokeOthers_CurrentFamilySurvivesWithoutEpochChange()
    {
        var account = await CreateAccountAsync();
        var current = await _refreshTokens.IssueAsync(account);
        var localOther = await _refreshTokens.IssueAsync(account);
        var oidcOther = await IssueOidcSessionAsync(account);
        var originalSessionVersion = account.SessionVersion;

        Assert.True(await _sessions.RevokeOthersAsync(
            account.Id,
            account.TenantId,
            current.AccountSessionId));

        _db.ChangeTracker.Clear();
        var remaining = await _db.AccountSessions
            .AsNoTracking()
            .Where(session => session.RevokedAtUtc == null)
            .SingleAsync();
        Assert.Equal(current.AccountSessionId, remaining.Id);
        Assert.Equal(
            originalSessionVersion,
            (await _db.UserAccounts.AsNoTracking().SingleAsync()).SessionVersion);
        Assert.True((await _refreshTokens.RotateAsync(current.RefreshToken)).Rotated);
        Assert.False((await _refreshTokens.RotateAsync(localOther.RefreshToken)).Rotated);
        Assert.False((await _refreshTokens.RotateAsync(oidcOther.RefreshToken)).Rotated);
        Assert.NotNull(
            (await _db.OidcProviderSessions.AsNoTracking().SingleAsync()).RevokedAtUtc);
        Assert.Null(
            (await _db.OidcProviderSessions.AsNoTracking().SingleAsync()).ReissueBlockedAtUtc);
    }

    [Fact]
    public async Task Revoke_ForeignUnknownAndRepeatedIdentifiers_HaveNoSideEffects()
    {
        var accountA = await CreateAccountAsync();
        var accountACurrent = await _refreshTokens.IssueAsync(accountA);
        var accountAOther = await _refreshTokens.IssueAsync(accountA);
        var tenantB = await _accounts.ProvisionTenantAsync(
            "Tenant B",
            "tenant-b@example.com",
            Password,
            "Tenant B Owner");
        var accountBSession = await _refreshTokens.IssueAsync(tenantB.Account!);

        await _sessions.RevokeAsync(
            accountA.Id,
            accountA.TenantId,
            accountBSession.AccountSessionId);
        await _sessions.RevokeAsync(accountA.Id, accountA.TenantId, Guid.NewGuid());
        await _sessions.RevokeAsync(
            accountA.Id,
            tenantB.Tenant!.Id,
            accountAOther.AccountSessionId);

        Assert.True((await _refreshTokens.RotateAsync(accountACurrent.RefreshToken)).Rotated);
        Assert.True((await _refreshTokens.RotateAsync(accountAOther.RefreshToken)).Rotated);
        Assert.True((await _refreshTokens.RotateAsync(accountBSession.RefreshToken)).Rotated);

        await _sessions.RevokeAsync(
            accountA.Id,
            accountA.TenantId,
            accountAOther.AccountSessionId);
        var beforeRepeat = await _db.RefreshTokenSessions
            .AsNoTracking()
            .Where(session => session.AccountSessionId == accountAOther.AccountSessionId)
            .Select(session => session.RevokedAtUtc)
            .ToListAsync();
        _time.UtcNow = FixedNow.AddHours(1);
        await _sessions.RevokeAsync(
            accountA.Id,
            accountA.TenantId,
            accountAOther.AccountSessionId);
        var afterRepeat = await _db.RefreshTokenSessions
            .AsNoTracking()
            .Where(session => session.AccountSessionId == accountAOther.AccountSessionId)
            .Select(session => session.RevokedAtUtc)
            .ToListAsync();

        Assert.Equal(beforeRepeat, afterRepeat);
    }

    [Fact]
    public async Task RefreshAndTargetedRevoke_ParallelWriters_CannotLeaveReplacementActive()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"gamehub-session-management-race-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Default Timeout=30";
        try
        {
            Guid accountId;
            Guid tenantId;
            Guid currentSessionId;
            Guid targetSessionId;
            string currentRefreshToken;
            string targetRefreshToken;
            await using (var setup = CreateFileContext(connectionString))
            {
                await setup.Database.MigrateAsync();
                var accounts = new AccountService(
                    setup,
                    new PasswordHasher<UserAccount>(),
                    _time);
                var account = (await accounts.BootstrapAsync(
                    TenantDefaults.DefaultTenantId,
                    "race@example.com",
                    Password,
                    "Race")).Account!;
                var refresh = new RefreshTokenService(
                    setup,
                    AccountServiceTests.CreateJwtSettings(),
                    _time);
                var current = await refresh.IssueAsync(account);
                var target = await refresh.IssueAsync(account);
                accountId = account.Id;
                tenantId = account.TenantId;
                currentSessionId = current.AccountSessionId;
                targetSessionId = target.AccountSessionId;
                currentRefreshToken = current.RefreshToken;
                targetRefreshToken = target.RefreshToken;
            }

            var start = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<RefreshTokenRotationResult> RefreshAsync()
            {
                await using var refreshDb = CreateFileContext(connectionString);
                await start.Task;
                return await new RefreshTokenService(
                    refreshDb,
                    AccountServiceTests.CreateJwtSettings(),
                    _time).RotateAsync(targetRefreshToken);
            }

            async Task RevokeAsync()
            {
                await using var revokeDb = CreateFileContext(connectionString);
                await start.Task;
                await new AccountSessionManagementService(revokeDb, _time).RevokeAsync(
                    accountId,
                    tenantId,
                    targetSessionId);
            }

            var refreshTask = RefreshAsync();
            var revokeTask = RevokeAsync();
            start.SetResult();
            await Task.WhenAll(refreshTask, revokeTask);
            var refreshResult = await refreshTask;

            await using var verification = CreateFileContext(connectionString);
            Assert.NotNull((await verification.AccountSessions
                .SingleAsync(session => session.Id == targetSessionId)).RevokedAtUtc);
            Assert.All(
                await verification.RefreshTokenSessions
                    .Where(session => session.AccountSessionId == targetSessionId)
                    .ToListAsync(),
                session => Assert.NotNull(session.RevokedAtUtc));
            Assert.Null((await verification.AccountSessions
                .SingleAsync(session => session.Id == currentSessionId)).RevokedAtUtc);
            Assert.True((await new RefreshTokenService(
                verification,
                AccountServiceTests.CreateJwtSettings(),
                _time).RotateAsync(currentRefreshToken)).Rotated);
            if (refreshResult.RefreshToken is { } replacement)
            {
                Assert.False((await new RefreshTokenService(
                    verification,
                    AccountServiceTests.CreateJwtSettings(),
                    _time).RotateAsync(replacement.RefreshToken)).Rotated);
            }
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    private async Task<UserAccount> CreateAccountAsync() =>
        (await _accounts.BootstrapAsync(
            TenantDefaults.DefaultTenantId,
            "owner@example.com",
            Password,
            "Owner")).Account!;

    private async Task<IssuedRefreshToken> IssueOidcSessionAsync(UserAccount account)
    {
        var identities = new ExternalIdentityService(_db, _time);
        Assert.Equal(
            ExternalIdentityLinkResult.Linked,
            await identities.LinkAsync(
                account.Id,
                account.TenantId,
                new ValidatedOidcIdentity(Issuer, "owner-subject", null)));
        var authentication = await identities.AuthenticateForSessionAsync(
            new ValidatedOidcIdentity(
                Issuer,
                "owner-subject",
                null,
                ProviderSessionId: "provider-session"));
        Assert.NotNull(authentication);
        var issued = await _refreshTokens.IssueOidcAsync(
            authentication!,
            "provider-session");
        Assert.True(issued.Issued);
        return issued.RefreshToken!;
    }

    private static CoreContext CreateFileContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<CoreContext>()
            .UseSqlite(connectionString)
            .ConfigureWarnings(warnings =>
                warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new CoreContext(options, new DefaultTenantContext());
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
