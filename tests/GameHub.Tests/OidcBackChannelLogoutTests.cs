using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using GameHub.Core.Data;
using GameHub.Core.Services;
using GameHub.Shared.Authentication;
using GameHub.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameHub.Tests;

/// <summary>Protocol, replay, immediate-revocation, and tenant-boundary tests.</summary>
public sealed class OidcBackChannelLogoutTests : IDisposable
{
    private const string Issuer = "https://identity.example.test";
    private const string Audience = "gamehub-client";
    private const string Password = "correct horse battery staple";
    private static readonly DateTimeOffset FixedNow =
        new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly CoreContext _db;
    private readonly TenantExecutionContext _tenantExecution = new();
    private readonly AccountService _accounts;
    private readonly RefreshTokenService _refreshTokens;
    private readonly TimeProvider _time = new AccountServiceTests.FixedTimeProvider(FixedNow);

    public OidcBackChannelLogoutTests()
    {
        var tenantContext = new AuthenticatedTenantContext(
            new HttpContextAccessor(),
            _tenantExecution);
        (_db, _connection) = DatabaseFixture.CreateCoreContext(tenantContext);
        _accounts = new AccountService(_db, new PasswordHasher<UserAccount>(), _time);
        _refreshTokens = new RefreshTokenService(
            _db,
            AccountServiceTests.CreateJwtSettings(),
            _time);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidateAsync_ExactLogoutToken_ReturnsOnlyStableLogoutIdentity(
        bool omitRecommendedType)
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "logout-key" };
        var validator = CreateValidator(key);

        var result = await validator.ValidateAsync(CreateLogoutToken(
            key,
            omitRecommendedType ? "missing_type" : null));

        Assert.Equal(OidcLogoutTokenValidationStatus.Succeeded, result.Status);
        Assert.Equal(Issuer, result.Token!.Issuer);
        Assert.Equal("subject-a", result.Token.Subject);
        Assert.Null(result.Token.ProviderSessionId);
        Assert.Equal("logout-event-a", result.Token.JwtId);
        Assert.Equal(FixedNow, result.Token.IssuedAtUtc);
    }

    [Theory]
    [InlineData("sid_only")]
    [InlineData("subject_and_sid")]
    public async Task ValidateAsync_StandardSessionClaimShapes_AreAccepted(string variant)
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "logout-key" };

        var result = await CreateValidator(key).ValidateAsync(
            CreateLogoutToken(key, variant));

        Assert.Equal(OidcLogoutTokenValidationStatus.Succeeded, result.Status);
        Assert.Equal("provider-session-a", result.Token!.ProviderSessionId);
        Assert.Equal(
            variant == "sid_only" ? null : "subject-a",
            result.Token.Subject);
    }

    [Theory]
    [InlineData("wrong_type")]
    [InlineData("wrong_issuer")]
    [InlineData("wrong_audience")]
    [InlineData("missing_event")]
    [InlineData("nonce")]
    [InlineData("missing_subject")]
    [InlineData("stale")]
    public async Task ValidateAsync_ProtocolViolation_IsRejected(string variant)
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "logout-key" };
        var validator = CreateValidator(key);

        var result = await validator.ValidateAsync(CreateLogoutToken(key, variant));

        Assert.Equal(OidcLogoutTokenValidationStatus.Invalid, result.Status);
        Assert.Null(result.Token);
    }

    [Fact]
    public async Task ProcessAsync_MappedTenant_InvalidatesAccessAndRefreshSessionsOnlyForOwner()
    {
        var (accountA, accountB) = await CreateLinkedAccountsAsync();
        var accountASession = await _refreshTokens.IssueAsync(accountA);
        await _refreshTokens.IssueAsync(accountB);
        var jwtService = new JwtTokenService(AccountServiceTests.CreateJwtSettings(), _time);
        var oldAccessToken = new JwtSecurityTokenHandler().ReadJwtToken(
            jwtService.CreateToken(accountA, accountASession.AccountSessionId).AccessToken);
        Assert.Equal(
            "0",
            oldAccessToken.Claims.Single(
                claim => claim.Type == GameHubClaimTypes.SessionVersion).Value);

        var service = CreateLogoutService();
        var result = await service.ProcessAsync(new ValidatedOidcLogoutToken(
            Issuer,
            "subject-a",
            "logout-event-a",
            FixedNow));

        // Simulate a login that loaded the account before logout but persists its refresh row
        // afterward. The captured epoch keeps that race from reopening the account session.
        var staleInFlightRefresh = await _refreshTokens.IssueAsync(accountA);

        _db.ChangeTracker.Clear();
        Assert.Equal(OidcBackChannelLogoutResult.Invalidated, result);
        var reloadedA = await _db.UserAccounts.SingleAsync(account => account.Id == accountA.Id);
        var reloadedB = await _db.UserAccounts.SingleAsync(account => account.Id == accountB.Id);
        Assert.Equal(1, reloadedA.SessionVersion);
        Assert.Equal(0, reloadedB.SessionVersion);
        var accountASessions = await _db.RefreshTokenSessions
            .Where(session => session.UserAccountId == accountA.Id)
            .ToListAsync();
        Assert.Equal(2, accountASessions.Count);
        Assert.Single(accountASessions, session => session.RevokedAtUtc is not null);
        Assert.Single(accountASessions, session => session.RevokedAtUtc is null);
        Assert.All(accountASessions, session => Assert.Equal(0, session.SessionVersion));
        Assert.Null((await _db.RefreshTokenSessions.SingleAsync(
            session => session.UserAccountId == accountB.Id)).RevokedAtUtc);
        Assert.False((await _refreshTokens.RotateAsync(
            staleInFlightRefresh.RefreshToken)).Rotated);

        var validation = new AccountSessionValidationService(_db);
        Assert.False(await validation.IsActiveAsync(accountA.Id, accountA.TenantId, 0));
        Assert.True(await validation.IsActiveAsync(accountA.Id, accountA.TenantId, 1));
        Assert.True(await validation.IsActiveAsync(accountB.Id, accountB.TenantId, 0));
        Assert.False(await validation.IsActiveAsync(accountA.Id, accountB.TenantId, 1));

        var activity = await _db.ActivityLogs.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(accountA.TenantId, activity.TenantId);
        Assert.Equal("OidcBackChannelLogout", activity.EventType);
        Assert.DoesNotContain("subject-a", activity.Message, StringComparison.Ordinal);
        var replayMarker = await _db.ProcessedOidcLogoutTokens.SingleAsync();
        Assert.Equal(64, replayMarker.TokenHash.Length);
        Assert.DoesNotContain("logout-event-a", replayMarker.TokenHash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessAsync_RepeatedOrUnknownLogout_IsIdempotentAndNonEnumerating()
    {
        var (accountA, _) = await CreateLinkedAccountsAsync();
        await _refreshTokens.IssueAsync(accountA);
        var service = CreateLogoutService();
        var token = new ValidatedOidcLogoutToken(
            Issuer,
            "subject-a",
            "repeated-event",
            FixedNow);

        Assert.Equal(OidcBackChannelLogoutResult.Invalidated, await service.ProcessAsync(token));
        Assert.Equal(
            OidcBackChannelLogoutResult.AlreadyProcessed,
            await service.ProcessAsync(token));
        Assert.Equal(
            OidcBackChannelLogoutResult.NoLiveMapping,
            await service.ProcessAsync(new ValidatedOidcLogoutToken(
                Issuer,
                "unknown-subject",
                "unknown-event",
                FixedNow)));

        _db.ChangeTracker.Clear();
        Assert.Equal(1, (await _db.UserAccounts.SingleAsync(
            account => account.Id == accountA.Id)).SessionVersion);
        Assert.Single(await _db.ActivityLogs.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(2, await _db.ProcessedOidcLogoutTokens.CountAsync());
    }

    [Fact]
    public async Task ProcessAsync_TwoProviderSessions_RevokesOnlyNamedFamilyThenSubjectRevokesAll()
    {
        var (accountA, accountB) = await CreateLinkedAccountsAsync();
        var sessionA = await CreateOidcSessionAsync(
            accountA,
            "subject-a",
            "provider-session-a");
        var sessionB = await CreateOidcSessionAsync(
            accountA,
            "subject-a",
            "provider-session-b");
        var tenantBSession = await CreateOidcSessionAsync(
            accountB,
            "subject-b",
            "provider-session-tenant-b");
        var localSession = await _refreshTokens.IssueAsync(accountA);
        var sessionValidation = new AccountSessionValidationService(_db);

        var scopedResult = await CreateLogoutService().ProcessAsync(
            new ValidatedOidcLogoutToken(
                Issuer,
                Subject: null,
                "sid-only-event",
                FixedNow,
                "provider-session-a"));

        Assert.Equal(OidcBackChannelLogoutResult.SessionInvalidated, scopedResult);
        Assert.False((await _refreshTokens.RotateAsync(sessionA.RefreshToken)).Rotated);
        var sessionBRotation = await _refreshTokens.RotateAsync(sessionB.RefreshToken);
        var localRotation = await _refreshTokens.RotateAsync(localSession.RefreshToken);
        var tenantBRotation = await _refreshTokens.RotateAsync(tenantBSession.RefreshToken);
        Assert.True(sessionBRotation.Rotated);
        Assert.True(localRotation.Rotated);
        Assert.True(tenantBRotation.Rotated);
        Assert.False(await sessionValidation.IsActiveAsync(
            accountA.Id,
            accountA.TenantId,
            accountA.SessionVersion,
            sessionA.AccountSessionId));
        Assert.True(await sessionValidation.IsActiveAsync(
            accountA.Id,
            accountA.TenantId,
            accountA.SessionVersion,
            sessionB.AccountSessionId));

        var accountWideResult = await CreateLogoutService().ProcessAsync(
            new ValidatedOidcLogoutToken(
                Issuer,
                "subject-a",
                "subject-only-event",
                FixedNow));

        Assert.Equal(OidcBackChannelLogoutResult.AccountInvalidated, accountWideResult);
        Assert.False((await _refreshTokens.RotateAsync(
            sessionBRotation.RefreshToken!.RefreshToken)).Rotated);
        Assert.False((await _refreshTokens.RotateAsync(
            localRotation.RefreshToken!.RefreshToken)).Rotated);
        Assert.True((await _refreshTokens.RotateAsync(
            tenantBRotation.RefreshToken!.RefreshToken)).Rotated);
        Assert.Equal(
            1,
            (await _db.UserAccounts.AsNoTracking().SingleAsync(
                account => account.Id == accountA.Id)).SessionVersion);
        Assert.Equal(
            0,
            (await _db.UserAccounts.AsNoTracking().SingleAsync(
                account => account.Id == accountB.Id)).SessionVersion);
    }

    [Fact]
    public async Task ProcessAsync_SubjectAndSessionFromDifferentTenants_IsRejectedWithoutRevocation()
    {
        var (accountA, accountB) = await CreateLinkedAccountsAsync();
        var sessionA = await CreateOidcSessionAsync(
            accountA,
            "subject-a",
            "provider-session-a");
        var sessionB = await CreateOidcSessionAsync(
            accountB,
            "subject-b",
            "provider-session-b");

        var result = await CreateLogoutService().ProcessAsync(
            new ValidatedOidcLogoutToken(
                Issuer,
                "subject-b",
                "contradictory-event",
                FixedNow,
                "provider-session-a"));

        Assert.Equal(OidcBackChannelLogoutResult.SubjectSessionMismatch, result);
        Assert.True((await _refreshTokens.RotateAsync(sessionA.RefreshToken)).Rotated);
        Assert.True((await _refreshTokens.RotateAsync(sessionB.RefreshToken)).Rotated);
        Assert.Empty(await _db.ProcessedOidcLogoutTokens.ToListAsync());
        Assert.Empty(await _db.ActivityLogs.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task ProcessAsync_UnknownSid_IsHashedIdempotentTombstone()
    {
        var service = CreateLogoutService();
        var token = new ValidatedOidcLogoutToken(
            Issuer,
            Subject: null,
            "unknown-sid-event",
            FixedNow,
            "unknown-provider-session");

        Assert.Equal(
            OidcBackChannelLogoutResult.NoLiveMapping,
            await service.ProcessAsync(token));
        Assert.Equal(
            OidcBackChannelLogoutResult.AlreadyProcessed,
            await service.ProcessAsync(token));

        var tombstone = await _db.OidcProviderSessions.SingleAsync();
        Assert.Null(tombstone.AccountSessionId);
        Assert.NotNull(tombstone.RevokedAtUtc);
        Assert.NotNull(tombstone.ReissueBlockedAtUtc);
        Assert.Equal(64, tombstone.SessionKeyHash.Length);
        Assert.DoesNotContain(
            "unknown-provider-session",
            tombstone.SessionKeyHash,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessAsync_LocallyRevokedSid_BecomesPermanentProviderTombstone()
    {
        var (accountA, _) = await CreateLinkedAccountsAsync();
        var original = await CreateOidcSessionAsync(
            accountA,
            "subject-a",
            "provider-session-a");
        await new AccountSessionManagementService(_db, _time).RevokeAsync(
            accountA.Id,
            accountA.TenantId,
            original.AccountSessionId);

        var result = await CreateLogoutService().ProcessAsync(
            new ValidatedOidcLogoutToken(
                Issuer,
                Subject: null,
                "provider-after-local-event",
                FixedNow,
                "provider-session-a"));

        Assert.Equal(OidcBackChannelLogoutResult.NoLiveMapping, result);
        var providerState = await _db.OidcProviderSessions.AsNoTracking().SingleAsync();
        Assert.NotNull(providerState.RevokedAtUtc);
        Assert.NotNull(providerState.ReissueBlockedAtUtc);

        var authentication = await new ExternalIdentityService(_db, _time)
            .AuthenticateForSessionAsync(new ValidatedOidcIdentity(
                Issuer,
                "subject-a",
                null,
                ProviderSessionId: "provider-session-a"));
        Assert.NotNull(authentication);
        Assert.False((await _refreshTokens.IssueOidcAsync(
            authentication!,
            "provider-session-a")).Issued);
    }

    [Fact]
    public async Task LoginAndSidLogout_ParallelFirstWriters_CannotLeaveSessionActive()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"gamehub-oidc-logout-race-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Default Timeout=30";
        try
        {
            await using (var setup = CreateFileContext(connectionString, out _))
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
                Assert.Equal(
                    ExternalIdentityLinkResult.Linked,
                    await new ExternalIdentityService(setup, _time).LinkAsync(
                        account.Id,
                        account.TenantId,
                        new ValidatedOidcIdentity(Issuer, "race-subject", null)));
            }

            var start = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<OidcRefreshTokenIssueResult> LoginAsync()
            {
                await using var loginDb = CreateFileContext(connectionString, out _);
                var mapping = await loginDb.ExternalIdentities
                    .Include(identity => identity.UserAccount)
                    .SingleAsync();
                await start.Task;
                return await new RefreshTokenService(
                    loginDb,
                    AccountServiceTests.CreateJwtSettings(),
                    _time).IssueOidcAsync(
                    new OidcAuthenticatedAccount(mapping.UserAccount!, mapping),
                    "race-provider-session");
            }

            async Task<OidcBackChannelLogoutResult> LogoutAsync()
            {
                await using var logoutDb = CreateFileContext(
                    connectionString,
                    out var execution);
                await start.Task;
                return await new OidcBackChannelLogoutService(
                    logoutDb,
                    execution,
                    _time,
                    NullLogger<OidcBackChannelLogoutService>.Instance).ProcessAsync(
                    new ValidatedOidcLogoutToken(
                        Issuer,
                        Subject: null,
                        "race-logout-event",
                        FixedNow,
                        "race-provider-session"));
            }

            var loginTask = LoginAsync();
            var logoutTask = LogoutAsync();
            start.SetResult();
            await Task.WhenAll(loginTask, logoutTask);
            var loginResult = await loginTask;
            Assert.NotEqual(
                OidcBackChannelLogoutResult.SubjectSessionMismatch,
                await logoutTask);

            await using var verification = CreateFileContext(connectionString, out _);
            var providerState = await verification.OidcProviderSessions.SingleAsync();
            Assert.NotNull(providerState.RevokedAtUtc);
            Assert.All(
                await verification.AccountSessions.ToListAsync(),
                session => Assert.NotNull(session.RevokedAtUtc));
            Assert.All(
                await verification.RefreshTokenSessions.ToListAsync(),
                session => Assert.NotNull(session.RevokedAtUtc));
            if (loginResult.RefreshToken is { } refreshToken)
            {
                Assert.False((await new RefreshTokenService(
                    verification,
                    AccountServiceTests.CreateJwtSettings(),
                    _time).RotateAsync(refreshToken.RefreshToken)).Rotated);
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

    [Fact]
    public async Task ReloginAndSidLogout_ParallelWriters_CannotReopenLocallyRevokedSession()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"gamehub-oidc-relogin-logout-race-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Default Timeout=30";
        try
        {
            await using (var setup = CreateFileContext(connectionString, out _))
            {
                await setup.Database.MigrateAsync();
                var accounts = new AccountService(
                    setup,
                    new PasswordHasher<UserAccount>(),
                    _time);
                var account = (await accounts.BootstrapAsync(
                    TenantDefaults.DefaultTenantId,
                    "relogin-race@example.com",
                    Password,
                    "Relogin Race")).Account!;
                var identities = new ExternalIdentityService(setup, _time);
                Assert.Equal(
                    ExternalIdentityLinkResult.Linked,
                    await identities.LinkAsync(
                        account.Id,
                        account.TenantId,
                        new ValidatedOidcIdentity(Issuer, "relogin-race-subject", null)));
                var authentication = await identities.AuthenticateForSessionAsync(
                    new ValidatedOidcIdentity(
                        Issuer,
                        "relogin-race-subject",
                        null,
                        ProviderSessionId: "relogin-race-provider-session"));
                Assert.NotNull(authentication);
                var original = await new RefreshTokenService(
                    setup,
                    AccountServiceTests.CreateJwtSettings(),
                    _time).IssueOidcAsync(
                    authentication!,
                    "relogin-race-provider-session");
                Assert.True(original.Issued);
                await new AccountSessionManagementService(setup, _time).RevokeAsync(
                    account.Id,
                    account.TenantId,
                    original.RefreshToken!.AccountSessionId);
            }

            var start = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<OidcRefreshTokenIssueResult> ReloginAsync()
            {
                await using var loginDb = CreateFileContext(connectionString, out _);
                var mapping = await loginDb.ExternalIdentities
                    .Include(identity => identity.UserAccount)
                    .SingleAsync();
                await start.Task;
                return await new RefreshTokenService(
                    loginDb,
                    AccountServiceTests.CreateJwtSettings(),
                    _time).IssueOidcAsync(
                    new OidcAuthenticatedAccount(mapping.UserAccount!, mapping),
                    "relogin-race-provider-session");
            }

            async Task<OidcBackChannelLogoutResult> LogoutAsync()
            {
                await using var logoutDb = CreateFileContext(
                    connectionString,
                    out var execution);
                await start.Task;
                return await new OidcBackChannelLogoutService(
                    logoutDb,
                    execution,
                    _time,
                    NullLogger<OidcBackChannelLogoutService>.Instance).ProcessAsync(
                    new ValidatedOidcLogoutToken(
                        Issuer,
                        Subject: null,
                        "relogin-race-logout-event",
                        FixedNow,
                        "relogin-race-provider-session"));
            }

            var loginTask = ReloginAsync();
            var logoutTask = LogoutAsync();
            start.SetResult();
            await Task.WhenAll(loginTask, logoutTask);
            var loginResult = await loginTask;
            Assert.NotEqual(
                OidcBackChannelLogoutResult.SubjectSessionMismatch,
                await logoutTask);

            await using var verification = CreateFileContext(connectionString, out _);
            var providerState = await verification.OidcProviderSessions.SingleAsync();
            Assert.NotNull(providerState.RevokedAtUtc);
            Assert.NotNull(providerState.ReissueBlockedAtUtc);
            Assert.All(
                await verification.AccountSessions.ToListAsync(),
                session => Assert.NotNull(session.RevokedAtUtc));
            Assert.All(
                await verification.RefreshTokenSessions.ToListAsync(),
                session => Assert.NotNull(session.RevokedAtUtc));
            if (loginResult.RefreshToken is { } replacement)
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

    [Fact]
    public async Task RefreshAndSidLogout_ParallelWriters_CannotLeaveReplacementActive()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"gamehub-oidc-refresh-logout-race-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Default Timeout=30";
        try
        {
            string refreshToken;
            await using (var setup = CreateFileContext(connectionString, out _))
            {
                await setup.Database.MigrateAsync();
                var accounts = new AccountService(
                    setup,
                    new PasswordHasher<UserAccount>(),
                    _time);
                var account = (await accounts.BootstrapAsync(
                    TenantDefaults.DefaultTenantId,
                    "refresh-race@example.com",
                    Password,
                    "Refresh Race")).Account!;
                var identities = new ExternalIdentityService(setup, _time);
                Assert.Equal(
                    ExternalIdentityLinkResult.Linked,
                    await identities.LinkAsync(
                        account.Id,
                        account.TenantId,
                        new ValidatedOidcIdentity(
                            Issuer,
                            "refresh-race-subject",
                            null)));
                var authentication = await identities.AuthenticateForSessionAsync(
                    new ValidatedOidcIdentity(
                        Issuer,
                        "refresh-race-subject",
                        null,
                        ProviderSessionId: "refresh-race-provider-session"));
                Assert.NotNull(authentication);
                var issued = await new RefreshTokenService(
                    setup,
                    AccountServiceTests.CreateJwtSettings(),
                    _time).IssueOidcAsync(
                    authentication!,
                    "refresh-race-provider-session");
                Assert.True(issued.Issued);
                refreshToken = issued.RefreshToken!.RefreshToken;
            }

            var start = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<RefreshTokenRotationResult> RefreshAsync()
            {
                await using var refreshDb = CreateFileContext(connectionString, out _);
                await start.Task;
                return await new RefreshTokenService(
                    refreshDb,
                    AccountServiceTests.CreateJwtSettings(),
                    _time).RotateAsync(refreshToken);
            }

            async Task<OidcBackChannelLogoutResult> LogoutAsync()
            {
                await using var logoutDb = CreateFileContext(
                    connectionString,
                    out var execution);
                await start.Task;
                return await new OidcBackChannelLogoutService(
                    logoutDb,
                    execution,
                    _time,
                    NullLogger<OidcBackChannelLogoutService>.Instance).ProcessAsync(
                    new ValidatedOidcLogoutToken(
                        Issuer,
                        Subject: null,
                        "refresh-race-logout-event",
                        FixedNow,
                        "refresh-race-provider-session"));
            }

            var refreshTask = RefreshAsync();
            var logoutTask = LogoutAsync();
            start.SetResult();
            await Task.WhenAll(refreshTask, logoutTask);
            var refreshResult = await refreshTask;
            Assert.Equal(
                OidcBackChannelLogoutResult.SessionInvalidated,
                await logoutTask);

            await using var verification = CreateFileContext(connectionString, out _);
            var providerState = await verification.OidcProviderSessions.SingleAsync();
            Assert.NotNull(providerState.RevokedAtUtc);
            Assert.All(
                await verification.AccountSessions.ToListAsync(),
                session => Assert.NotNull(session.RevokedAtUtc));
            Assert.All(
                await verification.RefreshTokenSessions.ToListAsync(),
                session => Assert.NotNull(session.RevokedAtUtc));
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

    [Fact]
    public async Task Migration_SqliteUpgradePreservesAccountAndAddsReplayBoundary()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CoreContext>()
            .UseSqlite(connection)
            .ConfigureWarnings(warnings =>
                warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var db = new CoreContext(options, new DefaultTenantContext());
        var migrator = db.Database.GetService<IMigrator>();
        await migrator.MigrateAsync("20260714175858_AddNormalizedGtinToPhysicalCopiesSqlite");
        var accountId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "UserAccounts"
                ("Id", "TenantId", "AccountType", "Email", "NormalizedEmail", "DisplayName",
                 "PasswordHash", "CreatedAtUtc", "LastLoginAtUtc")
            VALUES
                ({accountId}, {TenantDefaults.DefaultTenantId}, {0}, {"legacy@example.com"},
                 {"LEGACY@EXAMPLE.COM"}, {"Legacy"}, {"preserved-hash"},
                 {FixedNow.UtcDateTime}, NULL);
            INSERT INTO "RefreshTokenSessions"
                ("Id", "UserAccountId", "TokenHash", "CreatedAtUtc", "ExpiresAtUtc",
                 "ConsumedAtUtc", "RevokedAtUtc")
            VALUES
                ({Guid.NewGuid()}, {accountId}, {new string('C', 64)}, {FixedNow.UtcDateTime},
                 {FixedNow.AddDays(30).UtcDateTime}, NULL, NULL);
            """);

        await migrator.MigrateAsync();
        db.ChangeTracker.Clear();

        var account = await db.UserAccounts.SingleAsync(candidate => candidate.Id == accountId);
        Assert.Equal(0, account.SessionVersion);
        var legacyRefresh = await db.RefreshTokenSessions.SingleAsync();
        Assert.Null(legacyRefresh.AccountSessionId);
        Assert.NotNull(legacyRefresh.RevokedAtUtc);
        db.ProcessedOidcLogoutTokens.Add(new ProcessedOidcLogoutToken
        {
            Id = Guid.NewGuid(),
            TokenHash = new string('A', 64),
            ProcessedAtUtc = FixedNow.UtcDateTime,
            ExpiresAtUtc = FixedNow.AddMinutes(10).UtcDateTime
        });
        await db.SaveChangesAsync();
        db.ProcessedOidcLogoutTokens.Add(new ProcessedOidcLogoutToken
        {
            Id = Guid.NewGuid(),
            TokenHash = new string('A', 64),
            ProcessedAtUtc = FixedNow.UtcDateTime,
            ExpiresAtUtc = FixedNow.AddMinutes(10).UtcDateTime
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Migration_SqliteHistoricalRevocation_RemainsProviderBlocked()
    {
        await using var connection =
            new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CoreContext>()
            .UseSqlite(connection)
            .ConfigureWarnings(warnings =>
                warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var db = new CoreContext(options, new DefaultTenantContext());
        var migrator = db.Database.GetService<IMigrator>();
        await migrator.MigrateAsync("20260725084924_AddOidcSessionFamiliesPostgres");

        var accountId = Guid.NewGuid();
        var identityId = Guid.NewGuid();
        var accountSessionId = Guid.NewGuid();
        var providerSessionId = Guid.NewGuid();
        var revokedAtUtc = FixedNow.UtcDateTime;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "UserAccounts"
                ("Id", "TenantId", "AccountType", "Email", "NormalizedEmail", "DisplayName",
                 "PasswordHash", "CreatedAtUtc", "LastLoginAtUtc")
            VALUES
                ({accountId}, {TenantDefaults.DefaultTenantId}, {0}, {"legacy-oidc@example.com"},
                 {"LEGACY-OIDC@EXAMPLE.COM"}, {"Legacy OIDC"}, {"preserved-hash"},
                 {FixedNow.UtcDateTime}, NULL);
            INSERT INTO "ExternalIdentities"
                ("Id", "Issuer", "Subject", "UserAccountId", "TenantId", "CreatedAtUtc",
                 "LastLoginAtUtc")
            VALUES
                ({identityId}, {Issuer}, {"legacy-subject"}, {accountId},
                 {TenantDefaults.DefaultTenantId}, {FixedNow.UtcDateTime}, NULL);
            INSERT INTO "AccountSessions"
                ("Id", "UserAccountId", "TenantId", "CreatedAtUtc", "RevokedAtUtc")
            VALUES
                ({accountSessionId}, {accountId}, {TenantDefaults.DefaultTenantId},
                 {FixedNow.UtcDateTime}, {revokedAtUtc});
            INSERT INTO "OidcProviderSessions"
                ("Id", "SessionKeyHash", "ExternalIdentityId", "AccountSessionId",
                 "UserAccountId", "TenantId", "CreatedAtUtc", "RevokedAtUtc")
            VALUES
                ({providerSessionId}, {new string('D', 64)}, {identityId}, {accountSessionId},
                 {accountId}, {TenantDefaults.DefaultTenantId}, {FixedNow.UtcDateTime},
                 {revokedAtUtc});
            """);

        await migrator.MigrateAsync();
        db.ChangeTracker.Clear();

        var providerSession = await db.OidcProviderSessions.SingleAsync();
        Assert.Equal(revokedAtUtc, providerSession.RevokedAtUtc);
        Assert.Equal(revokedAtUtc, providerSession.ReissueBlockedAtUtc);
    }

    private OidcBackChannelLogoutService CreateLogoutService() => new(
        _db,
        _tenantExecution,
        _time,
        NullLogger<OidcBackChannelLogoutService>.Instance);

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

    private async Task<(UserAccount AccountA, UserAccount AccountB)> CreateLinkedAccountsAsync()
    {
        var accountA = (await _accounts.BootstrapAsync(
            TenantDefaults.DefaultTenantId,
            "tenant-a@example.com",
            Password,
            "Tenant A")).Account!;
        var accountB = (await _accounts.ProvisionTenantAsync(
            "Tenant B",
            "tenant-b@example.com",
            Password,
            "Tenant B")).Account!;
        var identities = new ExternalIdentityService(_db, _time);
        Assert.Equal(
            ExternalIdentityLinkResult.Linked,
            await identities.LinkAsync(
                accountA.Id,
                accountA.TenantId,
                new ValidatedOidcIdentity(Issuer, "subject-a", null)));
        Assert.Equal(
            ExternalIdentityLinkResult.Linked,
            await identities.LinkAsync(
                accountB.Id,
                accountB.TenantId,
                new ValidatedOidcIdentity(Issuer, "subject-b", null)));
        return (accountA, accountB);
    }

    private async Task<IssuedRefreshToken> CreateOidcSessionAsync(
        UserAccount account,
        string subject,
        string providerSessionId)
    {
        var identities = new ExternalIdentityService(_db, _time);
        var authentication = await identities.AuthenticateForSessionAsync(
            new ValidatedOidcIdentity(
                Issuer,
                subject,
                null,
                ProviderSessionId: providerSessionId));
        Assert.NotNull(authentication);
        var issued = await _refreshTokens.IssueOidcAsync(
            authentication!,
            providerSessionId);
        Assert.True(issued.Issued);
        return issued.RefreshToken!;
    }

    private OidcLogoutTokenValidator CreateValidator(SecurityKey key)
    {
        var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
        configuration.SigningKeys.Add(key);
        return new OidcLogoutTokenValidator(
            CreateSettings(),
            new StaticMetadataProvider(configuration),
            NullLogger<OidcLogoutTokenValidator>.Instance,
            _time);
    }

    private static OidcSettings CreateSettings() => new(
        true,
        Issuer,
        Audience,
        $"{Issuer}/.well-known/openid-configuration",
        [SecurityAlgorithms.RsaSha256],
        new string('B', 32));

    private static string CreateLogoutToken(SecurityKey key, string? variant = null)
    {
        var issuedAt = variant == "stale" ? FixedNow.AddMinutes(-10) : FixedNow;
        var header = new JwtHeader(new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
        if (variant == "missing_type")
        {
            header.Remove(JwtHeaderParameterNames.Typ);
        }
        else
        {
            header[JwtHeaderParameterNames.Typ] = variant == "wrong_type" ? "JWT" : "logout+jwt";
        }
        var payload = new JwtPayload
        {
            [JwtRegisteredClaimNames.Iss] = variant == "wrong_issuer"
                ? "https://wrong.example.test"
                : Issuer,
            [JwtRegisteredClaimNames.Aud] = variant == "wrong_audience"
                ? "wrong-client"
                : Audience,
            [JwtRegisteredClaimNames.Iat] = issuedAt.ToUnixTimeSeconds(),
            [JwtRegisteredClaimNames.Jti] = "logout-event-a",
            ["events"] = variant == "missing_event"
                ? new Dictionary<string, object>
                {
                    ["different-event"] = new Dictionary<string, object>()
                }
                : new Dictionary<string, object>
                {
                    ["http://schemas.openid.net/event/backchannel-logout"] =
                        new Dictionary<string, object>()
                }
        };
        if (variant is not ("missing_subject" or "sid_only"))
        {
            payload[JwtRegisteredClaimNames.Sub] = "subject-a";
        }
        if (variant is "sid_only" or "subject_and_sid")
        {
            payload["sid"] = "provider-session-a";
        }
        if (variant == "nonce")
        {
            payload["nonce"] = "prohibited";
        }

        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(header, payload));
    }

    private sealed class StaticMetadataProvider(OpenIdConnectConfiguration configuration)
        : IOidcMetadataProvider
    {
        public Task<OpenIdConnectConfiguration> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(configuration);

        public void RequestRefresh()
        {
        }
    }
}
