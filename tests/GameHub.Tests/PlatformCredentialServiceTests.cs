using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameHub.Ingest.Controllers;
using GameHub.Ingest.Data;
using GameHub.Ingest.Models;
using GameHub.Ingest.Services;
using GameHub.Shared;
using GameHub.Shared.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameHub.Tests;

/// <summary>Persistence, API, and cross-tenant tests for encrypted platform credentials.</summary>
public sealed class PlatformCredentialServiceTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset Now = new(2026, 6, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<IngestContext> _options;
    private readonly PlatformCredentialProtector _protector = CreateProtector();
    private readonly TimeProvider _timeProvider = new FixedTimeProvider(Now);

    public PlatformCredentialServiceTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<IngestContext>()
            .UseSqlite(_connection)
            .Options;
        using var db = CreateContext(TenantA);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Upsert_ValidCredential_StoresOnlyAuthenticatedCiphertext()
    {
        await using var db = CreateContext(TenantA);
        var service = CreateService(db, TenantA);
        var payload = ParseCredentials("{\"apiKey\":\"top-secret\",\"steamId\":\"123\"}");

        var status = await service.UpsertAsync(Platform.Steam, 1, payload);
        var stored = await db.PlatformCredentials.SingleAsync();
        var encryptedText = Encoding.UTF8.GetString(stored.EncryptedPayload);
        var plaintext = _protector.Unprotect(stored);

        Assert.True(status.IsConfigured);
        Assert.Equal(1, status.SchemaVersion);
        Assert.Equal(Now.UtcDateTime, status.UpdatedAtUtc);
        Assert.DoesNotContain("top-secret", encryptedText, StringComparison.Ordinal);
        Assert.Contains("top-secret", Encoding.UTF8.GetString(plaintext), StringComparison.Ordinal);
        Assert.Equal(TenantA, stored.TenantId);
        CryptographicOperations.ZeroMemory(plaintext);
    }

    [Fact]
    public async Task Upsert_ExistingPlatform_ReencryptsSingleRecordWithFreshNonce()
    {
        await using var db = CreateContext(TenantA);
        var service = CreateService(db, TenantA);
        await service.UpsertAsync(
            Platform.Gog,
            1,
            ParseCredentials("{\"cookie\":\"first\",\"user\":\"player\"}"));
        var firstNonce = (await db.PlatformCredentials.SingleAsync()).Nonce.ToArray();
        db.ChangeTracker.Clear();

        await service.UpsertAsync(
            Platform.Gog,
            1,
            ParseCredentials("{\"cookie\":\"second\",\"user\":\"player\"}"));
        var stored = await db.PlatformCredentials.SingleAsync();

        Assert.Equal(1, await db.PlatformCredentials.CountAsync());
        Assert.Equal(1, stored.CredentialSchemaVersion);
        Assert.NotEqual(firstNonce, stored.Nonce);
    }

    [Fact]
    public async Task CredentialOperations_TwoTenants_CannotObserveOrDeleteEachOther()
    {
        await using (var dbA = CreateContext(TenantA))
        {
            await CreateService(dbA, TenantA).UpsertAsync(
                Platform.Psn,
                1,
                ParseCredentials("{\"cookies\":{\"session\":\"tenant-a\"}}"));
        }
        await using (var dbB = CreateContext(TenantB))
        {
            await CreateService(dbB, TenantB).UpsertAsync(
                Platform.Psn,
                1,
                ParseCredentials("{\"cookies\":{\"session\":\"tenant-b\"}}"));
        }

        await using (var dbA = CreateContext(TenantA))
        {
            var serviceA = CreateService(dbA, TenantA);
            Assert.True((await serviceA.ListStatusAsync()).Single(
                status => status.Platform == Platform.Psn).IsConfigured);
            Assert.True(await serviceA.DeleteAsync(Platform.Psn));
        }
        await using var dbBVerify = CreateContext(TenantB);
        var serviceB = CreateService(dbBVerify, TenantB);

        Assert.True((await serviceB.ListStatusAsync()).Single(
            status => status.Platform == Platform.Psn).IsConfigured);
        Assert.Equal(1, await dbBVerify.PlatformCredentials.IgnoreQueryFilters().CountAsync());
        Assert.Equal(TenantB, (await dbBVerify.PlatformCredentials.SingleAsync()).TenantId);
    }

    [Fact]
    public async Task Upsert_EmptyCredentialObject_RejectsWithoutWriting()
    {
        await using var db = CreateContext(TenantA);
        var service = CreateService(db, TenantA);

        await Assert.ThrowsAsync<PlatformCredentialValidationException>(
            () => service.UpsertAsync(Platform.Epic, 1, ParseCredentials("{}")));

        Assert.Empty(await db.PlatformCredentials.ToListAsync());
    }

    [Theory]
    [InlineData(Platform.Steam, "{\"apiKey\":\"secret\"}")]
    [InlineData(Platform.Gog, "{\"cookie\":\"secret\"}")]
    [InlineData(Platform.Psn, "{\"cookies\":{}}")]
    [InlineData(Platform.Epic, "{\"token\":\"secret\"}")]
    public async Task Upsert_InvalidOrUnsupportedPlatformSchema_RejectsWithoutWriting(
        Platform platform,
        string json)
    {
        await using var db = CreateContext(TenantA);
        var service = CreateService(db, TenantA);

        await Assert.ThrowsAsync<PlatformCredentialValidationException>(
            () => service.UpsertAsync(platform, 1, ParseCredentials(json)));

        Assert.Empty(await db.PlatformCredentials.ToListAsync());
    }

    [Fact]
    public async Task Upsert_EpicRefreshToken_StoresVaultCredential()
    {
        await using var db = CreateContext(TenantA);
        var service = CreateService(db, TenantA);

        var status = await service.UpsertAsync(
            Platform.Epic,
            1,
            ParseCredentials(
                "{\"refreshToken\":\"epic-refresh\",\"accountId\":\"account-1\",\"displayName\":\"Player\"}"));

        var credential = await service.GetEpicAsync();
        Assert.True(status.IsConfigured);
        Assert.NotNull(credential);
        Assert.Equal("epic-refresh", credential.RefreshToken);
        Assert.Equal("account-1", credential.AccountId);
        Assert.Equal("Player", credential.DisplayName);
    }

    [Fact]
    public async Task Upsert_EpicAuthorizationCode_StoresRefreshTokenWithoutPersistingCode()
    {
        await using var db = CreateContext(TenantA);
        var service = CreateService(
            db,
            TenantA,
            new FakeEpicAuthenticationClient(new EpicPlatformCredential(
                "exchanged-refresh-token",
                "account-1",
                "Player")));

        await service.UpsertAsync(
            Platform.Epic,
            1,
            ParseCredentials("{\"authorizationCode\":\"short-lived-code\"}"));

        var stored = await db.PlatformCredentials.SingleAsync();
        var plaintext = _protector.Unprotect(stored);
        var plaintextJson = Encoding.UTF8.GetString(plaintext);
        Assert.Contains("exchanged-refresh-token", plaintextJson, StringComparison.Ordinal);
        Assert.DoesNotContain("short-lived-code", plaintextJson, StringComparison.Ordinal);
        CryptographicOperations.ZeroMemory(plaintext);
    }

    [Fact]
    public async Task PersistEpicRefreshTokenRotation_SameRefreshToken_LeavesVaultUnchanged()
    {
        await using var db = CreateContext(TenantA);
        var service = CreateService(db, TenantA);
        await service.UpsertAsync(
            Platform.Epic,
            1,
            ParseCredentials(
                "{\"refreshToken\":\"epic-refresh\",\"accountId\":\"account-1\",\"displayName\":\"Player\"}"));
        var before = await db.PlatformCredentials.AsNoTracking().SingleAsync();
        db.ChangeTracker.Clear();

        await CreateRotationStore(db).PersistIfRotatedAsync(
            new EpicPlatformCredential("epic-refresh", "account-1", "Player"),
            new EpicPlatformCredential("epic-refresh", "account-2", "Renamed Player"));

        var after = await db.PlatformCredentials.AsNoTracking().SingleAsync();
        Assert.Equal(before.UpdatedAtUtc, after.UpdatedAtUtc);
        Assert.Equal(before.Nonce, after.Nonce);
        Assert.Equal(before.AuthenticationTag, after.AuthenticationTag);
        Assert.Equal(before.EncryptedPayload, after.EncryptedPayload);
    }

    [Fact]
    public async Task PersistEpicRefreshTokenRotation_NewRefreshToken_StoresEncryptedToken()
    {
        await using var db = CreateContext(TenantA);
        var service = CreateService(db, TenantA);
        await service.UpsertAsync(
            Platform.Epic,
            1,
            ParseCredentials(
                "{\"refreshToken\":\"old-refresh\",\"accountId\":\"account-1\",\"displayName\":\"Player\"}"));
        db.ChangeTracker.Clear();

        await CreateRotationStore(db).PersistIfRotatedAsync(
            new EpicPlatformCredential("old-refresh", "account-1", "Player"),
            new EpicPlatformCredential("new-refresh", "account-2", "Renamed Player"));
        db.ChangeTracker.Clear();

        var stored = await db.PlatformCredentials.SingleAsync();
        var credential = await service.GetEpicAsync();
        var encryptedText = Convert.ToBase64String(stored.EncryptedPayload);
        Assert.Equal(Platform.Epic, stored.Platform);
        Assert.Equal(TenantA, stored.TenantId);
        Assert.Equal("new-refresh", credential?.RefreshToken);
        Assert.Equal("account-2", credential?.AccountId);
        Assert.Equal("Renamed Player", credential?.DisplayName);
        Assert.DoesNotContain("new-refresh", encryptedText, StringComparison.Ordinal);
        Assert.DoesNotContain("old-refresh", encryptedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PersistEpicRefreshTokenRotation_TwoTenants_UpdatesOnlyActiveTenantEpicCredential()
    {
        await using (var dbA = CreateContext(TenantA))
        {
            await CreateService(dbA, TenantA).UpsertAsync(
                Platform.Epic,
                1,
                ParseCredentials("{\"refreshToken\":\"tenant-a-old\"}"));
            await CreateService(dbA, TenantA).UpsertAsync(
                Platform.Steam,
                1,
                ParseCredentials("{\"apiKey\":\"steam-secret-a\",\"steamId\":\"111\"}"));
        }
        await using (var dbB = CreateContext(TenantB))
        {
            await CreateService(dbB, TenantB).UpsertAsync(
                Platform.Epic,
                1,
                ParseCredentials("{\"refreshToken\":\"tenant-b-old\"}"));
        }

        await using (var dbA = CreateContext(TenantA))
        {
            var steamBefore = await dbA.PlatformCredentials
                .AsNoTracking()
                .SingleAsync(credential => credential.Platform == Platform.Steam);

            await CreateRotationStore(dbA).PersistIfRotatedAsync(
                new EpicPlatformCredential("tenant-a-old"),
                new EpicPlatformCredential("tenant-a-new"));

            var serviceA = CreateService(dbA, TenantA);
            var epicA = await serviceA.GetEpicAsync();
            var steamAfter = await dbA.PlatformCredentials
                .AsNoTracking()
                .SingleAsync(credential => credential.Platform == Platform.Steam);

            Assert.Equal("tenant-a-new", epicA?.RefreshToken);
            Assert.Equal(steamBefore.EncryptedPayload, steamAfter.EncryptedPayload);
            Assert.Equal(steamBefore.Nonce, steamAfter.Nonce);
            Assert.Equal(steamBefore.AuthenticationTag, steamAfter.AuthenticationTag);
        }

        await using var dbBVerify = CreateContext(TenantB);
        var epicB = await CreateService(dbBVerify, TenantB).GetEpicAsync();
        Assert.Equal("tenant-b-old", epicB?.RefreshToken);
    }

    [Fact]
    public async Task PersistEpicRefreshTokenRotation_ChangedStoredCredential_FailsWithSanitizedMessage()
    {
        await using var db = CreateContext(TenantA);
        await CreateService(db, TenantA).UpsertAsync(
            Platform.Epic,
            1,
            ParseCredentials("{\"refreshToken\":\"manually-replaced-refresh\"}"));

        var exception = await Assert.ThrowsAsync<PlatformCredentialRotationException>(
            () => CreateRotationStore(db).PersistIfRotatedAsync(
                new EpicPlatformCredential("old-refresh-secret"),
                new EpicPlatformCredential("new-refresh-secret")));

        Assert.Equal("Epic refresh token rotation could not be persisted.", exception.Message);
        Assert.DoesNotContain("old-refresh-secret", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("new-refresh-secret", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("manually-replaced-refresh", exception.Message, StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> UnsupportedSchemas()
    {
        (Platform Platform, string Json)[] payloads =
        [
            (Platform.Steam, "{\"apiKey\":\"secret\",\"steamId\":\"123\"}"),
            (Platform.Gog, "{\"cookie\":\"secret\",\"user\":\"player\"}"),
            (Platform.Psn, "{\"cookies\":{\"session\":\"secret\"}}"),
            (Platform.Epic, "{\"refreshToken\":\"epic-refresh\"}"),
            (Platform.Epic, "{\"authorizationCode\":\"short-lived-code\"}")
        ];
        foreach (var payload in payloads)
        foreach (var version in new[] { int.MinValue, -1, 0, 2, 100, 101, int.MaxValue })
            yield return [payload.Platform, version, payload.Json];
    }

    [Theory]
    [MemberData(nameof(UnsupportedSchemas))]
    public async Task Upsert_UnsupportedSchema_RejectsNewAndExistingRowsWithoutExchange(
        Platform platform, int schemaVersion, string json)
    {
        var storedPayload = platform == Platform.Epic
            ? ParseCredentials("{\"refreshToken\":\"existing-refresh\"}")
            : ParseCredentials(json);
        await using (var other = CreateContext(TenantB))
            await CreateService(other, TenantB).UpsertAsync(platform, 1, storedPayload);
        var epic = new FakeEpicAuthenticationClient(new EpicPlatformCredential("exchanged-refresh"));

        foreach (var existing in new[] { false, true })
        {
            await using var db = CreateContext(TenantA);
            var service = CreateService(db, TenantA, epic);
            if (existing)
                await service.UpsertAsync(platform, 1, storedPayload);
            db.ChangeTracker.Clear();
            var before = JsonSerializer.Serialize(await db.PlatformCredentials.IgnoreQueryFilters()
                .AsNoTracking().OrderBy(row => row.Id).ToListAsync());

            var exception = await Assert.ThrowsAsync<PlatformCredentialValidationException>(
                () => service.UpsertAsync(platform, schemaVersion, ParseCredentials(json)));

            Assert.Contains("SchemaVersion", exception.Message, StringComparison.Ordinal);
            if (schemaVersion is > 0 and <= PlatformCredentialService.MaximumSchemaVersion)
                Assert.Equal($"SchemaVersion {schemaVersion} is unsupported for {platform}.", exception.Message);
            Assert.Empty(epic.AuthorizationCodes);
            Assert.Equal(before, JsonSerializer.Serialize(await db.PlatformCredentials.IgnoreQueryFilters()
                .AsNoTracking().OrderBy(row => row.Id).ToListAsync()));
            Assert.Equal(existing ? 1 : 0, await db.PlatformCredentials.CountAsync());
        }
    }

    [Theory]
    [InlineData("123", "authorizationCode must be a string.")]
    [InlineData("true", "authorizationCode must be a string.")]
    [InlineData("{\"private\":\"payload-secret\"}", "authorizationCode must be a string.")]
    [InlineData("[]", "authorizationCode must be a string.")]
    [InlineData("null", "authorizationCode is required.")]
    [InlineData("\"\"", "authorizationCode is required.")]
    public async Task Upsert_InvalidEpicCodeShape_ReturnsSafeBadRequestWithoutExchange(string codeJson, string expectedMessage)
    {
        await using var db = CreateContext(TenantA);
        var epic = new FakeEpicAuthenticationClient(new EpicPlatformCredential("unused-refresh"));
        var controller = new PlatformCredentialsController(CreateService(db, TenantA, epic),
            NullLogger<PlatformCredentialsController>.Instance);

        var action = await controller.Upsert(Platform.Epic, new UpsertPlatformCredentialRequest
        {
            Credentials = ParseCredentials("{\"authorizationCode\":" + codeJson + ",\"refreshToken\":\"fallback-secret\"}")
        }, CancellationToken.None);

        var response = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal(expectedMessage, Assert.IsType<PlatformCredentialErrorResponse>(response.Value).Message);
        Assert.Empty(epic.AuthorizationCodes);
        Assert.Empty(await db.PlatformCredentials.ToListAsync());
    }

    [Fact]
    public async Task ReadCredential_TwoTenants_ReturnsOnlyActiveTenantPlaintext()
    {
        await using (var dbA = CreateContext(TenantA))
        {
            await CreateService(dbA, TenantA).UpsertAsync(
                Platform.Steam,
                1,
                ParseCredentials("{\"apiKey\":\"secret-a\",\"steamId\":\"111\"}"));
        }
        await using (var dbB = CreateContext(TenantB))
        {
            await CreateService(dbB, TenantB).UpsertAsync(
                Platform.Steam,
                1,
                ParseCredentials("{\"apiKey\":\"secret-b\",\"steamId\":\"222\"}"));
        }

        await using var verifyA = CreateContext(TenantA);
        await using var verifyB = CreateContext(TenantB);
        var credentialA = await CreateService(verifyA, TenantA).GetSteamAsync();
        var credentialB = await CreateService(verifyB, TenantB).GetSteamAsync();

        Assert.Equal("secret-a", credentialA?.ApiKey);
        Assert.Equal("111", credentialA?.SteamId);
        Assert.Equal("secret-b", credentialB?.ApiKey);
        Assert.Equal("222", credentialB?.SteamId);
    }

    [Fact]
    public async Task ReadPsnCredential_ValidCookieMap_ReturnsStoredCookies()
    {
        await using var db = CreateContext(TenantA);
        var service = CreateService(db, TenantA);
        await service.UpsertAsync(
            Platform.Psn,
            1,
            ParseCredentials("{\"cookies\":{\"pdccws_p\":\"secret-pdccws\",\"npsso\":\"secret-npsso\",\"sb-cdn.session\":\"secret-session\"}}"));

        var credential = await service.GetPsnAsync();

        Assert.NotNull(credential);
        Assert.Equal("secret-pdccws", credential.Cookies["pdccws_p"]);
        Assert.Equal("secret-npsso", credential.Cookies["npsso"]);
        Assert.Equal("secret-session", credential.Cookies["sb-cdn.session"]);
    }

    [Fact]
    public async Task ReadCredential_UnavailableEncryptionKey_ReturnsSanitizedReadFailure()
    {
        await using var db = CreateContext(TenantA);
        var service = CreateService(db, TenantA);
        await service.UpsertAsync(
            Platform.Steam,
            1,
            ParseCredentials("{\"apiKey\":\"secret\",\"steamId\":\"123\"}"));
        var stored = await db.PlatformCredentials.SingleAsync();
        stored.EncryptionKeyVersion = 999;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var exception = await Assert.ThrowsAsync<PlatformCredentialReadException>(
            () => service.GetSteamAsync());

        Assert.Equal("Stored credential for Steam is unavailable.", exception.Message);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upsert_OversizedCredential_RejectsWithoutWriting()
    {
        await using var db = CreateContext(TenantA);
        var service = CreateService(db, TenantA);
        var oversizedSecret = new string('x', PlatformCredentialService.MaximumPayloadSizeBytes);
        var payload = ParseCredentials(JsonSerializer.Serialize(new { token = oversizedSecret }));

        await Assert.ThrowsAsync<PlatformCredentialValidationException>(
            () => service.UpsertAsync(Platform.Steam, 1, payload));

        Assert.Empty(await db.PlatformCredentials.ToListAsync());
    }

    [Fact]
    public async Task Upsert_NumericUnknownPlatform_ReturnsBadRequestWithoutWriting()
    {
        await using var db = CreateContext(TenantA);
        var controller = new PlatformCredentialsController(
            CreateService(db, TenantA),
            NullLogger<PlatformCredentialsController>.Instance);

        var action = await controller.Upsert(
            (Platform)999,
            new UpsertPlatformCredentialRequest
            {
                Credentials = ParseCredentials("{\"token\":\"secret\"}")
            },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Empty(await db.PlatformCredentials.ToListAsync());
    }

    [Fact]
    public async Task Upsert_VaultNotConfigured_ReturnsServiceUnavailableWithoutWriting()
    {
        await using var db = CreateContext(TenantA);
        var emptyConfiguration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var disabledProtector = new PlatformCredentialProtector(
            PlatformCredentialKeyRing.FromConfiguration(emptyConfiguration));
        var service = new PlatformCredentialService(
            db,
            new FixedIngestTenantContext(TenantA),
            disabledProtector,
            _timeProvider);
        var controller = new PlatformCredentialsController(
            service,
            NullLogger<PlatformCredentialsController>.Instance);

        var action = await controller.Upsert(
            Platform.Steam,
            new UpsertPlatformCredentialRequest
            {
                Credentials = ParseCredentials("{\"apiKey\":\"secret\",\"steamId\":\"123\"}")
            },
            CancellationToken.None);

        var result = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Empty(await db.PlatformCredentials.ToListAsync());
    }

    [Fact]
    public void Controller_Authorization_RequiresTenantDelegationScheme()
    {
        var authorize = typeof(PlatformCredentialsController)
            .GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(authorize);
        Assert.Equal(TenantDelegationAuthenticationHandler.SchemeName, authorize.AuthenticationSchemes);
    }

    private IngestContext CreateContext(Guid tenantId) =>
        new(_options, new FixedIngestTenantContext(tenantId));

    private PlatformCredentialService CreateService(
        IngestContext db,
        Guid tenantId,
        IEpicAuthenticationClient? epicAuthenticationClient = null) =>
        new(
            db,
            new FixedIngestTenantContext(tenantId),
            _protector,
            _timeProvider,
            epicAuthenticationClient);

    private EpicRefreshTokenRotationStore CreateRotationStore(IngestContext db) =>
        new(db, _protector, _timeProvider);

    private static PlatformCredentialProtector CreateProtector()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Credentials:ActiveKeyVersion"] = "1",
                ["Credentials:Keys:1"] = Convert.ToBase64String(
                    Enumerable.Repeat((byte)0xA5, 32).ToArray())
            })
            .Build();
        return new PlatformCredentialProtector(
            PlatformCredentialKeyRing.FromConfiguration(configuration));
    }

    private static JsonElement ParseCredentials(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class FixedIngestTenantContext(Guid tenantId) : IIngestTenantContext
    {
        public Guid TenantId { get; } = tenantId;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeEpicAuthenticationClient(EpicPlatformCredential credential)
        : IEpicAuthenticationClient
    {
        public List<string> AuthorizationCodes { get; } = [];

        public Task<EpicPlatformCredential> ExchangeAuthorizationCodeAsync(
            string authorizationCode,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal("short-lived-code", authorizationCode);
            AuthorizationCodes.Add(authorizationCode);
            return Task.FromResult(credential);
        }
    }
}
