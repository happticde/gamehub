using System.Net;
using System.Net.Http.Json;
using GameHub.Core.Data;
using GameHub.Core.Services;
using GameHub.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameHub.Tests;

/// <summary>Verifies tenant boundaries across one scheduled sync cycle.</summary>
public sealed class SyncSchedulerTenantIsolationTests
{
    private static readonly Guid TenantA = TenantDefaults.DefaultTenantId;
    private static readonly Guid TenantB = Guid.Parse("00000000-0000-0000-0000-000000000002");

    [Fact]
    public async Task RunCycleAsync_MixedTenantPolicies_WritesOnlyTenantOwnedActivityAndAuditRows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var serviceProvider = CreateServiceProvider(connection);
        await SeedTenantsAsync(serviceProvider);
        var runner = serviceProvider.GetRequiredService<ITenantJobRunner>();
        var scheduler = new SyncSchedulerService(
            runner,
            new ConfigurationBuilder().Build(),
            NullLogger<SyncSchedulerService>.Instance,
            new SyncSchedulerStatus());

        var processed = await scheduler.RunCycleAsync(CancellationToken.None);

        Assert.Equal(0, processed);
        using var verifyScope = serviceProvider.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<CoreContext>();
        var activity = await db.ActivityLogs
            .IgnoreQueryFilters()
            .OrderBy(log => log.TenantId)
            .ThenBy(log => log.Id)
            .ToListAsync();
        Assert.Collection(
            activity,
            log => AssertActivity(log, TenantA, "SyncStarted"),
            log => AssertActivity(log, TenantA, "SyncFinished"),
            log => AssertActivity(log, TenantB, "SyncStarted"),
            log => AssertActivity(log, TenantB, "SyncSkipped"));

        var audit = await db.SyncAuditLogs
            .IgnoreQueryFilters()
            .OrderBy(log => log.TenantId)
            .ThenBy(log => log.Platform)
            .ToListAsync();
        Assert.Equal(Enum.GetValues<Platform>().Length, audit.Count);
        Assert.All(audit, log => Assert.Equal(TenantA, log.TenantId));
        Assert.All(audit, log => Assert.Equal("Success", log.Status));
    }

    private static ServiceProvider CreateServiceProvider(SqliteConnection connection)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:IngestServiceKey"] = "scheduler-test-service-key"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<TenantExecutionContext>();
        services.AddScoped<ITenantContext, AuthenticatedTenantContext>();
        services.AddDbContext<CoreContext>(options => options.UseSqlite(connection));
        services.AddSingleton<ITenantJobRunner, TenantJobRunner>();
        services.AddScoped(provider => new IngestClient(
            new HttpClient(new EmptyIngestHandler())
            {
                BaseAddress = new Uri("http://ingest.test")
            },
            provider.GetRequiredService<ITenantContext>(),
            configuration));
        services.AddScoped<UnificationService>();
        services.AddScoped<ActivityLogService>();
        return services.BuildServiceProvider();
    }

    private static async Task SeedTenantsAsync(ServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
        await db.Database.EnsureCreatedAsync();
        db.Tenants.Add(new Tenant
        {
            Id = TenantB,
            Name = "Tenant B",
            PlatformSyncEnabled = false,
            CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private static void AssertActivity(ActivityLog log, Guid tenantId, string eventType)
    {
        Assert.Equal(tenantId, log.TenantId);
        Assert.Equal(eventType, log.EventType);
    }

    private sealed class EmptyIngestHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new PagedResult<RawGameDto>
                {
                    Items = [],
                    TotalCount = 0,
                    Page = 1,
                    PageSize = 200
                })
            });
        }
    }
}
