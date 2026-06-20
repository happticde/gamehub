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
        await service.UpsertAsync(Platform.Gog, 1, ParseCredentials("{\"cookie\":\"first\"}"));
        var firstNonce = (await db.PlatformCredentials.SingleAsync()).Nonce.ToArray();
        db.ChangeTracker.Clear();

        await service.UpsertAsync(Platform.Gog, 2, ParseCredentials("{\"cookie\":\"second\"}"));
        var stored = await db.PlatformCredentials.SingleAsync();

        Assert.Equal(1, await db.PlatformCredentials.CountAsync());
        Assert.Equal(2, stored.CredentialSchemaVersion);
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
                ParseCredentials("{\"npsso\":\"tenant-a\"}"));
        }
        await using (var dbB = CreateContext(TenantB))
        {
            await CreateService(dbB, TenantB).UpsertAsync(
                Platform.Psn,
                1,
                ParseCredentials("{\"npsso\":\"tenant-b\"}"));
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
                Credentials = ParseCredentials("{\"apiKey\":\"secret\"}")
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

    private PlatformCredentialService CreateService(IngestContext db, Guid tenantId) =>
        new(
            db,
            new FixedIngestTenantContext(tenantId),
            _protector,
            _timeProvider);

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
}
