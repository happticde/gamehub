using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using GameHub.Core.Data;
using GameHub.Core.Services;
using GameHub.Shared.Authentication;
using GameHub.Tests.Helpers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace GameHub.Tests;

/// <summary>Direct production-OIDC validation, mapping, and tenant-isolation tests.</summary>
public sealed class OidcAuthenticationTests : IDisposable
{
    private const string Issuer = "https://identity.example.test";
    private const string Audience = "gamehub-client";
    private const string Nonce = "0123456789012345678901234567890123456789012";
    private const string Password = "correct horse battery staple";
    private static readonly DateTimeOffset FixedNow = new(2026, 6, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly CoreContext _db;
    private readonly AccountService _accounts;
    private readonly TimeProvider _time = new AccountServiceTests.FixedTimeProvider(FixedNow);

    public OidcAuthenticationTests()
    {
        var (db, connection) = DatabaseFixture.CreateCoreContext();
        _db = db;
        _connection = connection;
        _accounts = new AccountService(_db, new PasswordHasher<UserAccount>(), _time);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Theory]
    [InlineData("https://wrong-issuer.example.test", Audience, Nonce)]
    [InlineData(Issuer, "wrong-audience", Nonce)]
    [InlineData(Issuer, Audience, "wrong-nonce-which-is-still-long-enough-for-validation")]
    public async Task ValidateAsync_WrongIssuerAudienceOrNonce_IsRejected(
        string tokenIssuer,
        string tokenAudience,
        string tokenNonce)
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "test-key" };
        var validator = CreateValidator(key);
        var token = CreateToken(key, tokenIssuer, tokenAudience, tokenNonce, "subject-a");

        var result = await validator.ValidateAsync(token, Nonce);

        Assert.Equal(OidcTokenValidationStatus.Invalid, result.Status);
        Assert.Null(result.Identity);
    }

    [Fact]
    public async Task ValidateAsync_ExactTrustValues_ReturnsOnlyStableIdentity()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "test-key" };
        var validator = CreateValidator(key);

        var result = await validator.ValidateAsync(
            CreateToken(key, Issuer, Audience, Nonce, "subject-a"),
            Nonce);

        Assert.Equal(OidcTokenValidationStatus.Succeeded, result.Status);
        Assert.Equal(Issuer, result.Identity!.Issuer);
        Assert.Equal("subject-a", result.Identity.Subject);
        Assert.Equal("shared@example.com", result.Identity.Email);
    }

    [Fact]
    public async Task ValidateAsync_AlgorithmOutsideAllowlist_IsRejected()
    {
        var key = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)) { KeyId = "hmac-key" };
        var validator = CreateValidator(key);
        var token = CreateToken(key, Issuer, Audience, Nonce, "subject-a", SecurityAlgorithms.HmacSha256);

        var result = await validator.ValidateAsync(token, Nonce);

        Assert.Equal(OidcTokenValidationStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task ValidateAsync_ProviderUnavailable_FailsClosed()
    {
        var settings = CreateSettings();
        var validator = new OidcTokenValidator(
            settings,
            new ThrowingMetadataProvider(),
            NullLogger<OidcTokenValidator>.Instance);

        var result = await validator.ValidateAsync("header.payload.signature", Nonce);

        Assert.Equal(OidcTokenValidationStatus.ProviderUnavailable, result.Status);
    }

    [Fact]
    public void Settings_EnabledWithMissingOrUnsafeConfiguration_Throws()
    {
        var jwt = AccountServiceTests.CreateJwtSettings();
        var missingAudience = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Oidc:Enabled"] = "true",
            ["Authentication:Oidc:Issuer"] = Issuer,
            ["Authentication:Oidc:AllowedAlgorithms"] = "RS256",
            ["Authentication:Oidc:BffServiceKey"] = new string('B', 32)
        }).Build();
        var symmetricAlgorithm = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Oidc:Enabled"] = "true",
            ["Authentication:Oidc:Issuer"] = Issuer,
            ["Authentication:Oidc:Audience"] = Audience,
            ["Authentication:Oidc:AllowedAlgorithms"] = "HS256",
            ["Authentication:Oidc:BffServiceKey"] = new string('B', 32)
        }).Build();
        var missingBffKey = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Oidc:Enabled"] = "true",
            ["Authentication:Oidc:Issuer"] = Issuer,
            ["Authentication:Oidc:Audience"] = Audience,
            ["Authentication:Oidc:AllowedAlgorithms"] = "RS256"
        }).Build();

        Assert.Throws<InvalidOperationException>(() => OidcSettings.FromConfiguration(missingAudience, jwt));
        Assert.Throws<InvalidOperationException>(() => OidcSettings.FromConfiguration(symmetricAlgorithm, jwt));
        Assert.Throws<InvalidOperationException>(() => OidcSettings.FromConfiguration(missingBffKey, jwt));
    }

    [Fact]
    public async Task AuthenticateAsync_ExactSubjectCannotBeRedirectedByEmailOrTenantClaims()
    {
        var (accountA, accountB) = await CreateTwoAccountsAsync();
        var service = new ExternalIdentityService(_db, _time);
        var identityA = new ValidatedOidcIdentity(Issuer, "subject-a", accountB.Email);
        var identityB = new ValidatedOidcIdentity(Issuer, "subject-b", accountA.Email);
        Assert.Equal(
            ExternalIdentityLinkResult.Linked,
            await service.LinkAsync(accountA.Id, accountA.TenantId, identityA));
        Assert.Equal(
            ExternalIdentityLinkResult.Linked,
            await service.LinkAsync(accountB.Id, accountB.TenantId, identityB));

        var authenticatedA = await service.AuthenticateAsync(identityA);
        var authenticatedB = await service.AuthenticateAsync(identityB);

        Assert.Equal(accountA.Id, authenticatedA!.Id);
        Assert.Equal(accountA.TenantId, authenticatedA.TenantId);
        Assert.Equal(accountB.Id, authenticatedB!.Id);
        Assert.Equal(accountB.TenantId, authenticatedB.TenantId);
    }

    [Fact]
    public async Task AuthenticateAsync_SameEmailWithDifferentSubjectOrIssuer_DoesNotAutoLink()
    {
        var (accountA, _) = await CreateTwoAccountsAsync();
        var service = new ExternalIdentityService(_db, _time);
        var linked = new ValidatedOidcIdentity(Issuer, "subject-a", accountA.Email);
        await service.LinkAsync(accountA.Id, accountA.TenantId, linked);

        Assert.Null(await service.AuthenticateAsync(
            new ValidatedOidcIdentity(Issuer, "different-subject", accountA.Email)));
        Assert.Null(await service.AuthenticateAsync(
            new ValidatedOidcIdentity("https://other.example.test", "subject-a", accountA.Email)));
    }

    [Fact]
    public async Task LinkAsync_IdentityOwnedByTenantB_CannotBeClaimedByTenantA()
    {
        var (accountA, accountB) = await CreateTwoAccountsAsync();
        var service = new ExternalIdentityService(_db, _time);
        var identity = new ValidatedOidcIdentity(Issuer, "subject-b", accountB.Email);
        await service.LinkAsync(accountB.Id, accountB.TenantId, identity);

        var result = await service.LinkAsync(accountA.Id, accountA.TenantId, identity);

        Assert.Equal(ExternalIdentityLinkResult.IdentityAlreadyLinked, result);
        var mapping = await _db.ExternalIdentities.SingleAsync();
        Assert.Equal(accountB.Id, mapping.UserAccountId);
        Assert.Equal(accountB.TenantId, mapping.TenantId);
    }

    [Fact]
    public async Task Database_ForeignTenantMapping_IsRejected()
    {
        var (accountA, accountB) = await CreateTwoAccountsAsync();
        _db.ExternalIdentities.Add(new ExternalIdentity
        {
            Id = Guid.NewGuid(),
            Issuer = Issuer,
            Subject = "tampered-subject",
            UserAccountId = accountA.Id,
            TenantId = accountB.TenantId,
            CreatedAtUtc = FixedNow.UtcDateTime
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task AuthenticateAsync_PendingDeletionAndDeletedAccountsRemainBlocked()
    {
        var (accountA, _) = await CreateTwoAccountsAsync();
        var service = new ExternalIdentityService(_db, _time);
        var identity = new ValidatedOidcIdentity(Issuer, "subject-a", accountA.Email);
        await service.LinkAsync(accountA.Id, accountA.TenantId, identity);
        var tenant = await _db.Tenants.SingleAsync(candidate => candidate.Id == accountA.TenantId);
        tenant.DeletionRequestedAtUtc = FixedNow.UtcDateTime;
        await _db.SaveChangesAsync();

        Assert.Null(await service.AuthenticateAsync(identity));

        _db.UserAccounts.Remove(accountA);
        await _db.SaveChangesAsync();
        Assert.Empty(_db.ExternalIdentities);
        Assert.Null(await service.AuthenticateAsync(identity));
    }

    private async Task<(UserAccount AccountA, UserAccount AccountB)> CreateTwoAccountsAsync()
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
        return (accountA, accountB);
    }

    private static OidcTokenValidator CreateValidator(SecurityKey key)
    {
        var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
        configuration.SigningKeys.Add(key);
        return new OidcTokenValidator(
            CreateSettings(),
            new StaticMetadataProvider(configuration),
            NullLogger<OidcTokenValidator>.Instance);
    }

    private static OidcSettings CreateSettings() => new(
        true,
        Issuer,
        Audience,
        $"{Issuer}/.well-known/openid-configuration",
        [SecurityAlgorithms.RsaSha256],
        new string('B', 32));

    private static string CreateToken(
        SecurityKey key,
        string issuer,
        string audience,
        string nonce,
        string subject,
        string algorithm = SecurityAlgorithms.RsaSha256)
    {
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer,
            audience,
            [
                new Claim(JwtRegisteredClaimNames.Sub, subject),
                new Claim(JwtRegisteredClaimNames.Email, "shared@example.com"),
                new Claim("nonce", nonce),
                new Claim(GameHubClaimTypes.TenantId, Guid.NewGuid().ToString("D")),
                new Claim(ClaimTypes.Role, "Administrator")
            ],
            notBefore: now.AddMinutes(-1),
            expires: now.AddMinutes(5),
            signingCredentials: new SigningCredentials(key, algorithm));
        return new JwtSecurityTokenHandler().WriteToken(token);
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

    private sealed class ThrowingMetadataProvider : IOidcMetadataProvider
    {
        public Task<OpenIdConnectConfiguration> GetAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("provider unavailable");

        public void RequestRefresh()
        {
        }
    }
}
