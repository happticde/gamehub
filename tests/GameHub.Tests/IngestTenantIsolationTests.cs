using GameHub.Ingest.Controllers;
using GameHub.Ingest.Data;
using GameHub.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameHub.Tests;

/// <summary>Cross-tenant isolation tests for raw platform data and Core export feeds.</summary>
public sealed class IngestTenantIsolationTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<IngestContext> _options;

    public IngestTenantIsolationTests()
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
    public async Task RawItems_EquivalentPlatformIds_CoexistAndRemainTenantScoped()
    {
        await using (var tenantA = CreateContext(TenantA))
        {
            tenantA.SteamRawItems.Add(new SteamRawItem
            {
                AppId = 730,
                Name = "Tenant A game",
                RawJson = "{}"
            });
            await tenantA.SaveChangesAsync();
        }

        await using (var tenantB = CreateContext(TenantB))
        {
            tenantB.SteamRawItems.Add(new SteamRawItem
            {
                AppId = 730,
                Name = "Tenant B game",
                RawJson = "{}"
            });
            await tenantB.SaveChangesAsync();
        }

        await using var verifyA = CreateContext(TenantA);
        await using var verifyB = CreateContext(TenantB);
        Assert.Equal("Tenant A game", (await verifyA.SteamRawItems.SingleAsync()).Name);
        Assert.Equal("Tenant B game", (await verifyB.SteamRawItems.SingleAsync()).Name);
        Assert.Equal(2, await verifyA.SteamRawItems.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Export_TenantA_CannotReturnTenantBItems()
    {
        await using (var tenantA = CreateContext(TenantA))
        {
            tenantA.SteamRawItems.Add(new SteamRawItem
            {
                AppId = 1,
                Name = "Visible to A",
                RawJson = "{}"
            });
            await tenantA.SaveChangesAsync();
        }
        await using (var tenantB = CreateContext(TenantB))
        {
            tenantB.SteamRawItems.Add(new SteamRawItem
            {
                AppId = 2,
                Name = "Secret for B",
                RawJson = "{}"
            });
            await tenantB.SaveChangesAsync();
        }

        await using var exportContext = CreateContext(TenantA);
        var controller = new ExportController(
            exportContext,
            NullLogger<ExportController>.Instance);

        var action = await controller.GetByPlatform("steam");
        var result = Assert.IsType<OkObjectResult>(action);
        var page = Assert.IsType<PagedResult<RawGameDto>>(result.Value);
        Assert.Single(page.Items);
        Assert.Equal("Visible to A", page.Items[0].Name);
        Assert.DoesNotContain(page.Items, item => item.Name == "Secret for B");
    }

    [Fact]
    public async Task SaveChanges_ExplicitForeignTenantId_IsRejected()
    {
        await using var tenantA = CreateContext(TenantA);
        tenantA.SteamRawItems.Add(new SteamRawItem
        {
            TenantId = TenantB,
            AppId = 42,
            Name = "Wrong owner",
            RawJson = "{}"
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tenantA.SaveChangesAsync());
        Assert.Contains("different tenant", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TenantMigration_ExistingRawData_AssignsDefaultTenant()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IngestContext>()
            .UseSqlite(connection)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var db = new IngestContext(
            options,
            new FixedIngestTenantContext(IngestTenantDefaults.DefaultTenantId));
        var migrator = db.Database.GetService<IMigrator>();

        await migrator.MigrateAsync("20260311190029_AddPlaytimeMinutes");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO SteamRawItems (AppId, Name, RawJson, LastUpdated) " +
            "VALUES (730, 'Legacy game', '{{}}', '2026-06-20T00:00:00Z')");

        await migrator.MigrateAsync();
        db.ChangeTracker.Clear();

        var migrated = await db.SteamRawItems.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(IngestTenantDefaults.DefaultTenantId, migrated.TenantId);
        Assert.Equal("Legacy game", migrated.Name);
    }

    private IngestContext CreateContext(Guid tenantId) =>
        new(_options, new FixedIngestTenantContext(tenantId));

    private sealed class FixedIngestTenantContext(Guid tenantId) : IIngestTenantContext
    {
        public Guid TenantId { get; } = tenantId;
    }
}
