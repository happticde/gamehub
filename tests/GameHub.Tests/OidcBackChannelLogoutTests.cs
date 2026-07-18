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
        Assert.Equal("logout-event-a", result.Token.JwtId);
        Assert.Equal(FixedNow, result.Token.IssuedAtUtc);
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
        await _refreshTokens.IssueAsync(accountA);
        await _refreshTokens.IssueAsync(accountB);
        var jwtService = new JwtTokenService(AccountServiceTests.CreateJwtSettings(), _time);
        var oldAccessToken = new JwtSecurityTokenHandler().ReadJwtToken(
            jwtService.CreateToken(accountA).AccessToken);
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
            """);

        await migrator.MigrateAsync();
        db.ChangeTracker.Clear();

        var account = await db.UserAccounts.SingleAsync(candidate => candidate.Id == accountId);
        Assert.Equal(0, account.SessionVersion);
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

    private OidcBackChannelLogoutService CreateLogoutService() => new(
        _db,
        _tenantExecution,
        _time,
        NullLogger<OidcBackChannelLogoutService>.Instance);

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
        if (variant != "missing_subject")
        {
            payload[JwtRegisteredClaimNames.Sub] = "subject-a";
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
