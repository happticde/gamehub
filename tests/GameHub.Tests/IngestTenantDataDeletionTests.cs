using GameHub.Ingest.Data;
using GameHub.Ingest.Services;
using GameHub.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GameHub.Tests;

/// <summary>Direct cleanup coverage for all Ingest tenant-owned tables and encrypted secrets.</summary>
public sealed class IngestTenantDataDeletionTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<IngestContext> _options;

    public IngestTenantDataDeletionTests()
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
    public async Task DeleteCurrentTenant_AllTablesAndEncryptedCredentials_RemovesOnlyTenantA()
    {
        await SeedAsync(TenantA, 100, "tenant-a");
        await SeedAsync(TenantB, 200, "tenant-b");
        await using var tenantA = CreateContext(TenantA);
        var deletion = new TenantDataDeletionService(
            tenantA,
            new FixedIngestTenantContext(TenantA));

        await deletion.DeleteCurrentTenantAsync();
        await deletion.DeleteCurrentTenantAsync();

        Assert.Equal(0, await CountRowsAsync(TenantA));
        Assert.Equal(6, await CountRowsAsync(TenantB));
        await using var verify = CreateContext(TenantA);
        Assert.False(await verify.PlatformCredentials.IgnoreQueryFilters()
            .AnyAsync(row => row.TenantId == TenantA));
        Assert.True(await verify.PlatformCredentials.IgnoreQueryFilters()
            .AnyAsync(row => row.TenantId == TenantB));
    }

    private async Task SeedAsync(Guid tenantId, int numericId, string marker)
    {
        await using var db = CreateContext(tenantId);
        db.SteamRawItems.Add(new SteamRawItem
        {
            AppId = numericId,
            Name = marker,
            RawJson = "{}"
        });
        db.PsnRawItems.Add(new PsnRawItem
        {
            TitleId = $"{marker}-psn",
            TitleName = marker,
            RawJson = "{}"
        });
        db.GogRawItems.Add(new GogRawItem
        {
            GameId = numericId,
            Title = marker,
            RawJson = "{}"
        });
        db.EpicRawItems.Add(new EpicRawItem
        {
            AppName = $"{marker}-epic",
            Title = marker,
            RawJson = "{}"
        });
        db.IngestAuditLogs.Add(new IngestAuditLog
        {
            Platform = "Steam",
            Status = "Success"
        });
        db.PlatformCredentials.Add(new PlatformCredential
        {
            Platform = Platform.Steam,
            CredentialSchemaVersion = 1,
            EncryptionFormatVersion = 1,
            EncryptionKeyVersion = 1,
            EncryptedPayload = [1, 2, 3],
            Nonce = new byte[12],
            AuthenticationTag = new byte[16],
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private async Task<int> CountRowsAsync(Guid tenantId)
    {
        await using var db = CreateContext(TenantA);
        return await db.SteamRawItems.IgnoreQueryFilters().CountAsync(row => row.TenantId == tenantId)
            + await db.PsnRawItems.IgnoreQueryFilters().CountAsync(row => row.TenantId == tenantId)
            + await db.GogRawItems.IgnoreQueryFilters().CountAsync(row => row.TenantId == tenantId)
            + await db.EpicRawItems.IgnoreQueryFilters().CountAsync(row => row.TenantId == tenantId)
            + await db.IngestAuditLogs.IgnoreQueryFilters().CountAsync(row => row.TenantId == tenantId)
            + await db.PlatformCredentials.IgnoreQueryFilters().CountAsync(row => row.TenantId == tenantId);
    }

    private IngestContext CreateContext(Guid tenantId) =>
        new(_options, new FixedIngestTenantContext(tenantId));

    private sealed class FixedIngestTenantContext(Guid tenantId) : IIngestTenantContext
    {
        public Guid TenantId { get; } = tenantId;
    }
}
