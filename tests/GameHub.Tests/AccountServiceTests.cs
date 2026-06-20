using GameHub.Core.Data;
using GameHub.Core.Services;
using GameHub.Shared.Authentication;
using GameHub.Tests.Helpers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using Xunit;

namespace GameHub.Tests;

/// <summary>Tests local account creation, password verification, and JWT issuance.</summary>
public sealed class AccountServiceTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private static readonly DateTimeOffset FixedNow = new(2026, 6, 20, 10, 0, 0, TimeSpan.Zero);

    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly CoreContext _db;
    private readonly AccountService _accounts;

    public AccountServiceTests()
    {
        var (db, connection) = DatabaseFixture.CreateCoreContext();
        _db = db;
        _connection = connection;
        _accounts = new AccountService(
            _db,
            new PasswordHasher<UserAccount>(),
            new FixedTimeProvider(FixedNow));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Bootstrap_ValidCredentials_CreatesHashedTenantAccount()
    {
        var result = await _accounts.BootstrapAsync(
            TenantDefaults.DefaultTenantId,
            "Owner@Example.com",
            Password,
            "Owner");

        Assert.True(result.Created);
        var account = await _db.UserAccounts.SingleAsync();
        Assert.Equal(TenantDefaults.DefaultTenantId, account.TenantId);
        Assert.Equal("Owner@Example.com", account.Email);
        Assert.Equal("OWNER@EXAMPLE.COM", account.NormalizedEmail);
        Assert.Equal(FixedNow.UtcDateTime, account.CreatedAtUtc);
        Assert.NotEqual(Password, account.PasswordHash);
        Assert.DoesNotContain(Password, account.PasswordHash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bootstrap_ExistingTenant_ReturnsConflictResult()
    {
        await _accounts.BootstrapAsync(
            TenantDefaults.DefaultTenantId,
            "owner@example.com",
            Password,
            "Owner");

        var result = await _accounts.BootstrapAsync(
            TenantDefaults.DefaultTenantId,
            "other@example.com",
            Password,
            "Other");

        Assert.False(result.Created);
        Assert.Equal("account_exists", result.ErrorCode);
        Assert.Equal(1, await _db.UserAccounts.CountAsync());
    }

    [Theory]
    [InlineData("not-an-email", Password, "Owner", "invalid_email")]
    [InlineData("owner@example.com", "too-short", "Owner", "invalid_password")]
    [InlineData("owner@example.com", Password, "", "invalid_display_name")]
    public async Task Bootstrap_InvalidInput_ReturnsValidationError(
        string email,
        string password,
        string displayName,
        string expectedCode)
    {
        var result = await _accounts.BootstrapAsync(
            TenantDefaults.DefaultTenantId,
            email,
            password,
            displayName);

        Assert.False(result.Created);
        Assert.Equal(expectedCode, result.ErrorCode);
        Assert.Empty(_db.UserAccounts);
    }

    [Fact]
    public async Task Authenticate_EmailCaseDiffers_VerifiesPasswordAndRecordsLogin()
    {
        await _accounts.BootstrapAsync(
            TenantDefaults.DefaultTenantId,
            "owner@example.com",
            Password,
            "Owner");

        var account = await _accounts.AuthenticateAsync("OWNER@EXAMPLE.COM", Password);

        Assert.NotNull(account);
        Assert.Equal(FixedNow.UtcDateTime, account.LastLoginAtUtc);
        Assert.Null(await _accounts.AuthenticateAsync("owner@example.com", "incorrect password"));
    }

    [Fact]
    public async Task CreateToken_AuthenticatedAccount_ProducesValidatedTenantClaims()
    {
        var creation = await _accounts.BootstrapAsync(
            TenantDefaults.DefaultTenantId,
            "owner@example.com",
            Password,
            "Owner");
        var settings = CreateJwtSettings();
        var tokens = new JwtTokenService(settings, new FixedTimeProvider(FixedNow));

        var issued = tokens.CreateToken(creation.Account!);
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var principal = handler.ValidateToken(
            issued.AccessToken,
            new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = settings.CreateSecurityKey(),
                ValidateIssuer = true,
                ValidIssuer = settings.Issuer,
                ValidateAudience = true,
                ValidAudience = settings.Audience,
                ValidateLifetime = false,
                NameClaimType = System.Security.Claims.ClaimTypes.Name
            },
            out _);

        Assert.Equal("Owner", principal.Identity?.Name);
        Assert.Equal(
            TenantDefaults.DefaultTenantId.ToString("D"),
            principal.FindFirst(GameHubClaimTypes.TenantId)?.Value);
        Assert.Equal(FixedNow.AddMinutes(60), issued.ExpiresAtUtc);
    }

    [Fact]
    public void JwtSettings_ShortSigningKey_Throws()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Jwt:SigningKey"] = "too-short"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => JwtSettings.FromConfiguration(configuration));

        Assert.Contains("32 UTF-8 bytes", exception.Message);
    }

    internal static JwtSettings CreateJwtSettings() =>
        new(
            true,
            "GameHub.Tests",
            "GameHub.Tests.Clients",
            "test-signing-key-with-at-least-32-bytes",
            60);

    internal sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
