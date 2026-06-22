using System.Text.Json;
using GameHub.Core.Controllers;
using GameHub.Core.Data;
using GameHub.Core.Models.Auth;
using GameHub.Core.Services;
using GameHub.Ingest.Controllers;
using GameHub.Ingest.Services;
using GameHub.Shared.Authentication;
using GameHub.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace GameHub.Tests;

/// <summary>Direct fail-closed configuration, readiness, and non-mutation tests.</summary>
public sealed class MultiUserEnablementTests : IDisposable
{
    private const string Issuer = "https://identity.example.test";
    private const string Password = "correct horse battery staple";
    private static readonly DateTimeOffset FixedNow =
        new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly CoreContext _db;
    private readonly TimeProvider _time = new AccountServiceTests.FixedTimeProvider(FixedNow);

    public MultiUserEnablementTests()
    {
        (_db, _connection) = DatabaseFixture.CreateCoreContext();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public void Mode_NewInstallationOrUpgradeWithoutSwitch_DefaultsDisabled()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();

        var mode = MultiUserMode.FromConfiguration(configuration);

        Assert.False(mode.IsEnabled);
    }

    [Fact]
    public void Mode_InvalidValue_ThrowsInsteadOfGuessing()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [MultiUserMode.ConfigurationKey] = "enabled"
            }).Build();

        Assert.Throws<InvalidOperationException>(
            () => MultiUserMode.FromConfiguration(configuration));
    }

    [Fact]
    public void Settings_OidcConfigurationAlone_DoesNotEnableMultiUserLogin()
    {
        var settings = MultiUserEnablementSettings.FromConfiguration(
            CreateConfiguration(enabled: false, ready: true),
            CreateJwtSettings(),
            CreateOidcSettings());
        var provisionedTenant = Guid.NewGuid();

        Assert.False(settings.Mode.IsEnabled);
        Assert.False(settings.AllowsOidcLogin(provisionedTenant));
        Assert.True(settings.AllowsOidcLogin(TenantDefaults.DefaultTenantId));
    }

    [Fact]
    public void Settings_EnablementSwitchWithoutPrerequisites_FailsClosed()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [MultiUserMode.ConfigurationKey] = "true",
                ["Authentication:Frontend:MultiUserEnabled"] = "true"
            }).Build();
        var disabledJwt = new JwtSettings(false, "GameHub", "GameHub.Clients", "", 60, 30);
        var disabledOidc = new OidcSettings(false, "", "", "", [], "");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            MultiUserEnablementSettings.FromConfiguration(
                configuration,
                disabledJwt,
                disabledOidc));

        Assert.Contains("blocked", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("POST", "/api/library", false)]
    [InlineData("GET", "/api/auth/oidc/link", false)]
    [InlineData("POST", "/api/auth/oidc/link/other", false)]
    [InlineData("POST", "/api/auth/oidc/link", true)]
    public void LinkOnlySession_RequestDataCannotWidenPurpose(
        string method,
        string path,
        bool expected)
    {
        Assert.Equal(expected, MultiUserSessionBoundary.AllowsOidcLinkOnlyRequest(method, path));
    }

    [Theory]
    [InlineData("POST", "/api/auth/account", false)]
    [InlineData("DELETE", "/api/library", false)]
    [InlineData("DELETE", "/api/auth/account/other", false)]
    [InlineData("DELETE", "/api/auth/account", true)]
    public void DeletionOnlySession_RequestDataCannotWidenPurpose(
        string method,
        string path,
        bool expected)
    {
        Assert.Equal(
            expected,
            MultiUserSessionBoundary.AllowsAccountDeletionOnlyRequest(method, path));
    }

    [Fact]
    public void Settings_FrontendAndCoreDisagree_FailsClosed()
    {
        var values = CreateConfiguration(enabled: true, ready: true)
            .AsEnumerable()
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        values["Authentication:Frontend:MultiUserEnabled"] = "false";
        var inconsistent = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        Assert.Throws<InvalidOperationException>(() =>
            MultiUserEnablementSettings.FromConfiguration(
                inconsistent,
                CreateJwtSettings(),
                CreateOidcSettings()));
    }

    [Fact]
    public void Settings_ReusedTrustSecret_FailsClosedWhenEnabled()
    {
        var values = CreateConfiguration(enabled: true, ready: true)
            .AsEnumerable()
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        values["Services:IngestServiceKey"] = new string('A', 32);
        var reused = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            MultiUserEnablementSettings.FromConfiguration(
                reused,
                CreateJwtSettings(),
                CreateOidcSettings()));

        Assert.Contains("trust_secrets_not_independent", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Readiness_UnlinkedAccountBlocksThenExplicitLinkSatisfiesWithoutDataMutation()
    {
        var accounts = new AccountService(_db, new PasswordHasher<UserAccount>(), _time);
        var created = await accounts.BootstrapAsync(
            TenantDefaults.DefaultTenantId,
            "owner@example.com",
            Password,
            "Owner");
        _db.UnifiedGames.Add(new UnifiedGame { Title = "Preserved", Slug = "preserved" });
        await _db.SaveChangesAsync();
        var refreshTokens = new RefreshTokenService(_db, CreateJwtSettings(), _time);
        await refreshTokens.IssueAsync(created.Account!);
        var disabledSettings = CreateEnablementSettings(enabled: false);
        var disabledService = new MultiUserEnablementService(
            _db,
            disabledSettings,
            CreateOidcSettings(),
            new TestMultiUserIngestReadinessProbe(enabled: false));

        var beforeLink = await disabledService.EvaluateAsync();
        Assert.Contains("active_account_missing_oidc_link", beforeLink.Blockers);

        var identities = new ExternalIdentityService(_db, _time);
        await identities.LinkAsync(
            created.Account!.Id,
            created.Account.TenantId,
            new ValidatedOidcIdentity(Issuer, "owner-subject", "ignored@example.com"));
        var baseline = await SnapshotAsync();

        var enabledService = new MultiUserEnablementService(
            _db,
            CreateEnablementSettings(enabled: true),
            CreateOidcSettings(),
            new TestMultiUserIngestReadinessProbe(enabled: true));
        var enabled = await enabledService.EvaluateAsync();
        await enabledService.ValidateStartupAsync();
        var disabledAgain = await disabledService.EvaluateAsync();

        Assert.True(enabled.MultiUserEnabled);
        Assert.True(enabled.ReadinessSatisfied);
        Assert.False(disabledAgain.MultiUserEnabled);
        Assert.True(disabledAgain.ReadinessSatisfied);
        Assert.Equal(baseline, await SnapshotAsync());
    }

    [Fact]
    public async Task Status_ContainsOnlySafeStatesAndBlockerCodes()
    {
        var configuration = CreateConfiguration(enabled: false, ready: true);
        var service = new MultiUserEnablementService(
            _db,
            MultiUserEnablementSettings.FromConfiguration(
                configuration,
                CreateJwtSettings(),
                CreateOidcSettings()),
            CreateOidcSettings(),
            new TestMultiUserIngestReadinessProbe(enabled: false));
        var controller = new MultiUserStatusController(service, configuration)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await controller.GetStatus(CancellationToken.None);

        var response = Assert.IsType<MultiUserStatusResponse>(
            Assert.IsType<OkObjectResult>(result).Value);
        var json = JsonSerializer.Serialize(response);
        Assert.Contains("no_active_account", response.Blockers);
        Assert.DoesNotContain(new string('A', 32), json, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('I', 32), json, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('B', 32), json, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("accountId", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true, true, false, "ingest_multi_user_mode_mismatch")]
    [InlineData(false, false, false, "ingest_credential_vault_not_ready")]
    [InlineData(false, true, true, "ingest_unavailable_or_trust_mismatch")]
    public async Task Readiness_IngestDriftVaultOrTrustFailure_ReportsSafeBlocker(
        bool ingestEnabled,
        bool vaultReady,
        bool unavailable,
        string expectedBlocker)
    {
        var service = new MultiUserEnablementService(
            _db,
            CreateEnablementSettings(enabled: false),
            CreateOidcSettings(),
            new TestMultiUserIngestReadinessProbe(
                ingestEnabled,
                vaultReady,
                unavailable));

        var readiness = await service.EvaluateAsync();

        Assert.Contains(expectedBlocker, readiness.Blockers);
    }

    [Fact]
    public void IngestReadiness_ResponseContainsOnlyModeAndVaultState()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Credentials:ActiveKeyVersion"] = "1",
                ["Credentials:Keys:1"] = Convert.ToBase64String(new byte[32])
            }).Build();
        using var keyRing = PlatformCredentialKeyRing.FromConfiguration(configuration);
        var controller = new MultiUserReadinessController(new MultiUserMode(false), keyRing)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var response = Assert.IsType<IngestMultiUserReadinessResponse>(
            Assert.IsType<OkObjectResult>(controller.GetReadiness()).Value);

        Assert.False(response.MultiUserEnabled);
        Assert.True(response.CredentialVaultReady);
        var json = JsonSerializer.Serialize(response);
        Assert.DoesNotContain("tenant", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credentialVaultKey", json, StringComparison.OrdinalIgnoreCase);
    }

    private MultiUserEnablementSettings CreateEnablementSettings(bool enabled) =>
        MultiUserEnablementSettings.FromConfiguration(
            CreateConfiguration(enabled, ready: true),
            CreateJwtSettings(),
            CreateOidcSettings());

    private static IConfigurationRoot CreateConfiguration(bool enabled, bool ready)
    {
        var values = new Dictionary<string, string?>
        {
            [MultiUserMode.ConfigurationKey] = enabled.ToString(),
            ["Authentication:Frontend:MultiUserEnabled"] = enabled.ToString(),
            ["Authentication:Frontend:Enabled"] = ready.ToString(),
            ["Authentication:Frontend:SecureCookies"] = ready.ToString(),
            ["Authentication:ApiKey"] = new string('A', 32),
            ["Services:IngestServiceKey"] = new string('I', 32)
        };
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static JwtSettings CreateJwtSettings() =>
        new(
            true,
            "GameHub.Tests",
            "GameHub.Tests.Clients",
            new string('J', 32),
            60,
            30);

    private static OidcSettings CreateOidcSettings() =>
        new(
            true,
            Issuer,
            "gamehub-client",
            $"{Issuer}/.well-known/openid-configuration",
            [SecurityAlgorithms.RsaSha256],
            new string('B', 32));

    private async Task<PreservationSnapshot> SnapshotAsync() =>
        new(
            await _db.Tenants.IgnoreQueryFilters().CountAsync(),
            await _db.UserAccounts.CountAsync(),
            await _db.ExternalIdentities.CountAsync(),
            await _db.RefreshTokenSessions.CountAsync(),
            await _db.UnifiedGames.IgnoreQueryFilters().CountAsync(),
            await _db.LibraryItems.IgnoreQueryFilters().CountAsync());

    private sealed record PreservationSnapshot(
        int Tenants,
        int Accounts,
        int ExternalIdentities,
        int RefreshSessions,
        int Games,
        int LibraryItems);
}

internal sealed class TestMultiUserIngestReadinessProbe(
    bool enabled,
    bool vaultReady = true,
    bool unavailable = false) : IMultiUserIngestReadinessProbe
{
    public Task<IngestMultiUserReadinessState> GetAsync(
        CancellationToken cancellationToken = default) =>
        unavailable
            ? Task.FromException<IngestMultiUserReadinessState>(
                new HttpRequestException("simulated unavailable"))
            : Task.FromResult(new IngestMultiUserReadinessState(enabled, vaultReady));
}
