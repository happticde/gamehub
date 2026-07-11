using System.Text.Encodings.Web;
using GameHub.Ingest.Controllers;
using GameHub.Ingest.Data;
using GameHub.Shared;
using GameHub.Shared.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
                RawJson = "{}",
                ContentHash = "tenant-a-hash"
            });
            await tenantA.SaveChangesAsync();
        }

        await using (var tenantB = CreateContext(TenantB))
        {
            tenantB.SteamRawItems.Add(new SteamRawItem
            {
                AppId = 730,
                Name = "Tenant B game",
                RawJson = "{}",
                ContentHash = "tenant-b-hash"
            });
            await tenantB.SaveChangesAsync();
        }

        await using var verifyA = CreateContext(TenantA);
        await using var verifyB = CreateContext(TenantB);
        Assert.Equal("Tenant A game", (await verifyA.SteamRawItems.SingleAsync()).Name);
        Assert.Equal("Tenant B game", (await verifyB.SteamRawItems.SingleAsync()).Name);
        Assert.Equal("tenant-a-hash", (await verifyA.SteamRawItems.SingleAsync()).ContentHash);
        Assert.Equal("tenant-b-hash", (await verifyB.SteamRawItems.SingleAsync()).ContentHash);
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
    public async Task DelegatedServiceTenants_ExportCannotCrossTenantBoundary()
    {
        var principalA = await AuthenticateDelegation(TenantA);
        await using (var tenantA = CreateDelegatedContext(principalA, TenantA))
        {
            tenantA.SteamRawItems.Add(new SteamRawItem
            {
                AppId = 10,
                Name = "Delegated A",
                RawJson = "{}"
            });
            await tenantA.SaveChangesAsync();
        }
        var principalB = await AuthenticateDelegation(TenantB);
        await using (var tenantB = CreateDelegatedContext(principalB, TenantB))
        {
            tenantB.SteamRawItems.Add(new SteamRawItem
            {
                AppId = 20,
                Name = "Delegated B",
                RawJson = "{}"
            });
            await tenantB.SaveChangesAsync();
        }

        await using (var verifyOwnership = CreateContext(TenantA))
        {
            var owners = await verifyOwnership.SteamRawItems
                .IgnoreQueryFilters()
                .OrderBy(item => item.AppId)
                .Select(item => item.TenantId)
                .ToListAsync();
            Assert.Equal([TenantA, TenantB], owners);
        }

        await using var delegatedA = CreateDelegatedContext(principalA, TenantA);
        var controller = new ExportController(
            delegatedA,
            NullLogger<ExportController>.Instance);

        var result = Assert.IsType<OkObjectResult>(await controller.GetByPlatform("steam"));
        var page = Assert.IsType<PagedResult<RawGameDto>>(result.Value);
        Assert.Equal("Delegated A", Assert.Single(page.Items).Name);
        Assert.DoesNotContain(page.Items, item => item.Name == "Delegated B");
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
        Assert.Equal(string.Empty, migrated.ContentHash);
    }

    private IngestContext CreateContext(Guid tenantId) =>
        new(_options, new FixedIngestTenantContext(tenantId));

    private static async Task<System.Security.Claims.ClaimsPrincipal> AuthenticateDelegation(
        Guid tenantId)
    {
        const string serviceKey = "cross-tenant-test-service-key";
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers[TenantDelegationAuthenticationHandler.ServiceKeyHeaderName] = serviceKey;
        httpContext.Request.Headers[TenantDelegationAuthenticationHandler.TenantHeaderName] =
            tenantId.ToString("D");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:ServiceKey"] = serviceKey
            })
            .Build();
        var options = new OptionsMonitor<AuthenticationSchemeOptions>(
            new OptionsFactory<AuthenticationSchemeOptions>([], []),
            [],
            new OptionsCache<AuthenticationSchemeOptions>());
        var handler = new TenantDelegationAuthenticationHandler(
            options,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            configuration);
        var scheme = new AuthenticationScheme(
            TenantDelegationAuthenticationHandler.SchemeName,
            null,
            typeof(TenantDelegationAuthenticationHandler));
        await handler.InitializeAsync(scheme, httpContext);
        var authentication = await handler.AuthenticateAsync();
        Assert.True(authentication.Succeeded);
        return authentication.Principal!;
    }

    private IngestContext CreateDelegatedContext(
        System.Security.Claims.ClaimsPrincipal principal,
        Guid expectedTenantId)
    {
        var httpContext = new DefaultHttpContext { User = principal };
        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        var tenantContext = new AuthenticatedIngestTenantContext(accessor);
        Assert.Equal(expectedTenantId, tenantContext.TenantId);
        return new IngestContext(
            _options,
            tenantContext);
    }

    private sealed class FixedIngestTenantContext(Guid tenantId) : IIngestTenantContext
    {
        public Guid TenantId { get; } = tenantId;
    }
}
