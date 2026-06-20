using GameHub.Core.Controllers;
using GameHub.Core.Data;
using GameHub.Core.Models.Auth;
using GameHub.Core.Services;
using GameHub.Shared.Authentication;
using GameHub.Tests.Helpers;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.IdentityModel.Tokens.Jwt;
using Xunit;

namespace GameHub.Tests;

/// <summary>Endpoint tests for account bootstrap and token exchange.</summary>
public sealed class AuthControllerTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private static readonly DateTimeOffset FixedNow = new(2026, 6, 20, 10, 0, 0, TimeSpan.Zero);

    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly CoreContext _db;
    private readonly AccountService _accounts;
    private readonly RefreshTokenService _refreshTokens;

    public AuthControllerTests()
    {
        var (db, connection) = DatabaseFixture.CreateCoreContext();
        _db = db;
        _connection = connection;
        _accounts = new AccountService(
            _db,
            new PasswordHasher<UserAccount>(),
            new AccountServiceTests.FixedTimeProvider(FixedNow));
        _refreshTokens = new RefreshTokenService(
            _db,
            AccountServiceTests.CreateJwtSettings(),
            new AccountServiceTests.FixedTimeProvider(FixedNow));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Bootstrap_ApiKeyNotConfigured_ReturnsServiceUnavailable()
    {
        var controller = CreateController(apiKey: "", jwtEnabled: true);

        var result = await controller.Bootstrap(
            new BootstrapAccountRequest("owner@example.com", Password, "Owner"),
            CancellationToken.None);

        var unavailable = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, unavailable.StatusCode);
        Assert.Equal("api_key_required", Assert.IsType<AuthErrorResponse>(unavailable.Value).Code);
    }

    [Fact]
    public async Task Bootstrap_ValidRequest_ReturnsCreatedAccountWithoutPassword()
    {
        var controller = CreateController(apiKey: "configured", jwtEnabled: true);

        var result = await controller.Bootstrap(
            new BootstrapAccountRequest("owner@example.com", Password, "Owner"),
            CancellationToken.None);

        var created = Assert.IsType<ObjectResult>(result);
        Assert.Equal(201, created.StatusCode);
        var response = Assert.IsType<AccountResponse>(created.Value);
        Assert.Equal("owner@example.com", response.Email);
        Assert.DoesNotContain(
            Password,
            System.Text.Json.JsonSerializer.Serialize(response),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProvisionTenant_ValidRequest_ReturnsServerGeneratedTenant()
    {
        var controller = CreateController(apiKey: "configured", jwtEnabled: true);

        var result = await controller.ProvisionTenant(
            new ProvisionTenantRequest(
                "Family Library",
                "family@example.com",
                Password,
                "Family Owner"),
            CancellationToken.None);

        var created = Assert.IsType<ObjectResult>(result);
        Assert.Equal(201, created.StatusCode);
        var response = Assert.IsType<TenantProvisioningResponse>(created.Value);
        Assert.NotEqual(Guid.Empty, response.TenantId);
        Assert.NotEqual(TenantDefaults.DefaultTenantId, response.TenantId);
        Assert.Equal(response.TenantId, response.Account.TenantId);
        Assert.False(response.PlatformSyncEnabled);
    }

    [Fact]
    public async Task Token_JwtNotConfigured_ReturnsServiceUnavailable()
    {
        var controller = CreateController(apiKey: "configured", jwtEnabled: false);

        var result = await controller.Token(
            new TokenRequest("owner@example.com", Password),
            CancellationToken.None);

        var unavailable = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, unavailable.StatusCode);
        Assert.Equal("jwt_not_configured", Assert.IsType<AuthErrorResponse>(unavailable.Value).Code);
    }

    [Fact]
    public async Task Token_InvalidPassword_ReturnsGenericUnauthorizedError()
    {
        await _accounts.BootstrapAsync(
            TenantDefaults.DefaultTenantId,
            "owner@example.com",
            Password,
            "Owner");
        var controller = CreateController(apiKey: "configured", jwtEnabled: true);

        var result = await controller.Token(
            new TokenRequest("owner@example.com", "incorrect password"),
            CancellationToken.None);

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        var error = Assert.IsType<AuthErrorResponse>(unauthorized.Value);
        Assert.Equal("invalid_credentials", error.Code);
        Assert.DoesNotContain("owner@example.com", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Token_ValidCredentials_ReturnsBearerToken()
    {
        await _accounts.BootstrapAsync(
            TenantDefaults.DefaultTenantId,
            "owner@example.com",
            Password,
            "Owner");
        var controller = CreateController(apiKey: "configured", jwtEnabled: true);

        var result = await controller.Token(
            new TokenRequest("owner@example.com", Password),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<TokenResponse>(ok.Value);
        Assert.Equal("Bearer", response.TokenType);
        Assert.NotEmpty(response.AccessToken);
        Assert.Equal(FixedNow.AddMinutes(60), response.ExpiresAtUtc);
        Assert.NotEmpty(response.RefreshToken);
        Assert.Equal(FixedNow.AddDays(30), response.RefreshTokenExpiresAtUtc);
        Assert.DoesNotContain(
            response.RefreshToken,
            (await _db.RefreshTokenSessions.SingleAsync()).TokenHash,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_ValidToken_RotatesPairAndRejectsReplay()
    {
        await _accounts.BootstrapAsync(
            TenantDefaults.DefaultTenantId,
            "owner@example.com",
            Password,
            "Owner");
        var controller = CreateController(apiKey: "configured", jwtEnabled: true);
        var login = Assert.IsType<OkObjectResult>(await controller.Token(
            new TokenRequest("owner@example.com", Password),
            CancellationToken.None));
        var original = Assert.IsType<TokenResponse>(login.Value);

        var refreshed = Assert.IsType<OkObjectResult>(await controller.Refresh(
            new RefreshTokenRequest(original.RefreshToken),
            CancellationToken.None));
        var replacement = Assert.IsType<TokenResponse>(refreshed.Value);

        Assert.NotEqual(original.AccessToken, replacement.AccessToken);
        Assert.NotEqual(original.RefreshToken, replacement.RefreshToken);
        var refreshedJwt = new JwtSecurityTokenHandler().ReadJwtToken(replacement.AccessToken);
        Assert.Equal(
            TenantDefaults.DefaultTenantId.ToString("D"),
            refreshedJwt.Claims.Single(claim => claim.Type == GameHubClaimTypes.TenantId).Value);
        var replay = Assert.IsType<UnauthorizedObjectResult>(await controller.Refresh(
            new RefreshTokenRequest(original.RefreshToken),
            CancellationToken.None));
        Assert.Equal(
            "invalid_refresh_token",
            Assert.IsType<AuthErrorResponse>(replay.Value).Code);
    }

    [Fact]
    public async Task Revoke_UnknownToken_ReturnsUniformNoContent()
    {
        var controller = CreateController(apiKey: "configured", jwtEnabled: true);

        var result = await controller.Revoke(
            new RefreshTokenRequest("unknown-token"),
            CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
    }

    private AuthController CreateController(string apiKey, bool jwtEnabled)
    {
        var jwtSettings = jwtEnabled
            ? AccountServiceTests.CreateJwtSettings()
            : new JwtSettings(false, "GameHub.Tests", "GameHub.Tests.Clients", "", 60, 30);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:ApiKey"] = apiKey
            })
            .Build();

        return new AuthController(
            _accounts,
            new JwtTokenService(jwtSettings, new AccountServiceTests.FixedTimeProvider(FixedNow)),
            _refreshTokens,
            new DefaultTenantContext(),
            configuration,
            NullLogger<AuthController>.Instance);
    }
}
