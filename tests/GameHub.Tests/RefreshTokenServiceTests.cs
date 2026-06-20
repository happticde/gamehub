using GameHub.Core.Data;
using GameHub.Core.Services;
using GameHub.Tests.Helpers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GameHub.Tests;

/// <summary>Tests opaque refresh-token hashing, rotation, replay defense, and revocation.</summary>
public sealed class RefreshTokenServiceTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private static readonly DateTimeOffset FixedNow = new(2026, 6, 20, 10, 0, 0, TimeSpan.Zero);

    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly CoreContext _db;
    private readonly MutableTimeProvider _timeProvider = new(FixedNow);
    private readonly AccountService _accounts;
    private readonly RefreshTokenService _refreshTokens;

    public RefreshTokenServiceTests()
    {
        var (db, connection) = DatabaseFixture.CreateCoreContext();
        _db = db;
        _connection = connection;
        var settings = AccountServiceTests.CreateJwtSettings();
        _accounts = new AccountService(_db, new PasswordHasher<UserAccount>(), _timeProvider);
        _refreshTokens = new RefreshTokenService(_db, settings, _timeProvider);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Issue_AuthenticatedAccount_PersistsOnlyHash()
    {
        var account = await CreateAccountAsync();

        var issued = await _refreshTokens.IssueAsync(account);
        var stored = await _db.RefreshTokenSessions.SingleAsync();

        Assert.NotEmpty(issued.RefreshToken);
        Assert.Equal(FixedNow.AddDays(30), issued.ExpiresAtUtc);
        Assert.Equal(64, stored.TokenHash.Length);
        Assert.NotEqual(issued.RefreshToken, stored.TokenHash);
        Assert.DoesNotContain(issued.RefreshToken, stored.TokenHash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rotate_ActiveToken_ConsumesOldTokenAndReturnsReplacement()
    {
        var account = await CreateAccountAsync();
        var original = await _refreshTokens.IssueAsync(account);

        var rotation = await _refreshTokens.RotateAsync(original.RefreshToken);

        Assert.True(rotation.Rotated);
        Assert.Equal(account.Id, rotation.Account!.Id);
        Assert.NotEqual(original.RefreshToken, rotation.RefreshToken!.RefreshToken);
        var sessions = await _db.RefreshTokenSessions
            .AsNoTracking()
            .OrderBy(session => session.CreatedAtUtc)
            .ThenBy(session => session.Id)
            .ToListAsync();
        Assert.Equal(2, sessions.Count);
        Assert.Single(sessions, session => session.ConsumedAtUtc == FixedNow.UtcDateTime);
        Assert.Single(sessions, session => session.ConsumedAtUtc is null && session.RevokedAtUtc is null);
    }

    [Fact]
    public async Task Rotate_ConsumedToken_RejectsReplayAndRevokesReplacement()
    {
        var account = await CreateAccountAsync();
        var original = await _refreshTokens.IssueAsync(account);
        var firstRotation = await _refreshTokens.RotateAsync(original.RefreshToken);

        var replay = await _refreshTokens.RotateAsync(original.RefreshToken);

        Assert.False(replay.Rotated);
        var replacement = await _db.RefreshTokenSessions
            .AsNoTracking()
            .SingleAsync(session => session.ConsumedAtUtc == null);
        Assert.Equal(FixedNow.UtcDateTime, replacement.RevokedAtUtc);
        Assert.False((await _refreshTokens.RotateAsync(firstRotation.RefreshToken!.RefreshToken)).Rotated);
    }

    [Fact]
    public async Task Rotate_ReplayedTenantToken_DoesNotRevokeAnotherTenantSession()
    {
        var accountA = await CreateAccountAsync();
        var tenantB = await _accounts.ProvisionTenantAsync(
            "Tenant B",
            "tenant-b@example.com",
            Password,
            "Tenant B Owner");
        var tokenA = await _refreshTokens.IssueAsync(accountA);
        var tokenB = await _refreshTokens.IssueAsync(tenantB.Account!);

        await _refreshTokens.RotateAsync(tokenA.RefreshToken);
        await _refreshTokens.RotateAsync(tokenA.RefreshToken);
        var tenantBRotation = await _refreshTokens.RotateAsync(tokenB.RefreshToken);

        Assert.True(tenantBRotation.Rotated);
        Assert.Equal(tenantB.Tenant!.Id, tenantBRotation.Account!.TenantId);
    }

    [Fact]
    public async Task Rotate_ExpiredToken_ReturnsInvalidWithoutConsumingIt()
    {
        var account = await CreateAccountAsync();
        var issued = await _refreshTokens.IssueAsync(account);
        _timeProvider.UtcNow = FixedNow.AddDays(31);

        var rotation = await _refreshTokens.RotateAsync(issued.RefreshToken);

        Assert.False(rotation.Rotated);
        Assert.Null((await _db.RefreshTokenSessions.AsNoTracking().SingleAsync()).ConsumedAtUtc);
    }

    [Fact]
    public async Task Revoke_ActiveToken_PreventsFutureRotation()
    {
        var account = await CreateAccountAsync();
        var issued = await _refreshTokens.IssueAsync(account);

        await _refreshTokens.RevokeAsync(issued.RefreshToken);

        Assert.False((await _refreshTokens.RotateAsync(issued.RefreshToken)).Rotated);
        Assert.Equal(
            FixedNow.UtcDateTime,
            (await _db.RefreshTokenSessions.AsNoTracking().SingleAsync()).RevokedAtUtc);
    }

    private async Task<UserAccount> CreateAccountAsync()
    {
        var result = await _accounts.BootstrapAsync(
            TenantDefaults.DefaultTenantId,
            "owner@example.com",
            Password,
            "Owner");
        return result.Account!;
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
