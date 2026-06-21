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

/// <summary>Verifies tenant boundaries across one background-enrichment batch.</summary>
public sealed class EnrichmentBackgroundTenantIsolationTests
{
    private static readonly Guid TenantA = TenantDefaults.DefaultTenantId;
    private static readonly Guid TenantB = Guid.Parse("00000000-0000-0000-0000-000000000002");

    [Fact]
    public async Task RunCycleAsync_BatchLimit_AppliesIndependentlyToEveryTenant()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var provider = new NotFoundMetadataProvider();
        await using var serviceProvider = CreateServiceProvider(connection, provider);
        await SeedLibrariesAsync(serviceProvider);
        var backgroundService = new EnrichmentBackgroundService(
            serviceProvider.GetRequiredService<ITenantJobRunner>(),
            new ConfigurationBuilder().Build(),
            NullLogger<EnrichmentBackgroundService>.Instance);

        var result = await backgroundService.RunCycleAsync(1, CancellationToken.None);

        Assert.Equal(4, result.Remaining);
        Assert.Equal(2, result.Processed);
        Assert.Equal(2, provider.SearchCallCount);

        using var verifyScope = serviceProvider.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<CoreContext>();
        var games = await db.UnifiedGames
            .IgnoreQueryFilters()
            .OrderBy(game => game.TenantId)
            .ThenBy(game => game.Id)
            .ToListAsync();
        AssertTenantBatch(games, TenantA);
        AssertTenantBatch(games, TenantB);

        var audit = await db.EnrichmentAuditLogs
            .IgnoreQueryFilters()
            .OrderBy(log => log.TenantId)
            .ToListAsync();
        Assert.Collection(
            audit,
            log => AssertAudit(log, TenantA),
            log => AssertAudit(log, TenantB));
    }

    private static ServiceProvider CreateServiceProvider(
        SqliteConnection connection,
        IGameMetadataProvider metadataProvider)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddSingleton<TenantExecutionContext>();
        services.AddScoped<ITenantContext, AuthenticatedTenantContext>();
        services.AddDbContext<CoreContext>(options => options.UseSqlite(connection));
        services.AddSingleton<ITenantJobRunner, TenantJobRunner>();
        services.AddSingleton(metadataProvider);
        services.AddSingleton<EnrichmentService>(provider => new EnrichmentService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IGameMetadataProvider>(),
            NullLogger<EnrichmentService>.Instance));
        return services.BuildServiceProvider();
    }

    private static async Task SeedLibrariesAsync(ServiceProvider serviceProvider)
    {
        using (var scope = serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
            await db.Database.EnsureCreatedAsync();
            db.Tenants.Add(new Tenant
            {
                Id = TenantB,
                Name = "Tenant B",
                CreatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var executionContext = serviceProvider.GetRequiredService<TenantExecutionContext>();
        await SeedTenantGamesAsync(serviceProvider, executionContext, TenantA, "tenant-a");
        await SeedTenantGamesAsync(serviceProvider, executionContext, TenantB, "tenant-b");
    }

    private static async Task SeedTenantGamesAsync(
        ServiceProvider serviceProvider,
        TenantExecutionContext executionContext,
        Guid tenantId,
        string slugPrefix)
    {
        using var tenantExecution = executionContext.BeginScope(tenantId);
        using var scope = serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
        db.UnifiedGames.AddRange(
            new UnifiedGame { Title = $"{slugPrefix} first", Slug = $"{slugPrefix}-first" },
            new UnifiedGame { Title = $"{slugPrefix} second", Slug = $"{slugPrefix}-second" });
        await db.SaveChangesAsync();
    }

    private static void AssertTenantBatch(IReadOnlyList<UnifiedGame> games, Guid tenantId)
    {
        var tenantGames = games.Where(game => game.TenantId == tenantId).ToList();
        Assert.Equal(2, tenantGames.Count);
        Assert.Single(tenantGames, game => game.EnrichedAt is not null);
        Assert.Single(tenantGames, game => game.EnrichedAt is null);
    }

    private static void AssertAudit(EnrichmentAuditLog log, Guid tenantId)
    {
        Assert.Equal(tenantId, log.TenantId);
        Assert.Equal("NotFound", log.Status);
        Assert.Equal("NotFoundProvider", log.ProviderName);
    }

    private sealed class NotFoundMetadataProvider : IGameMetadataProvider
    {
        public string ProviderName => "NotFoundProvider";
        public int SearchCallCount { get; private set; }

        public Task<GameMetadata?> SearchAsync(string title, CancellationToken ct = default)
        {
            SearchCallCount++;
            return Task.FromResult<GameMetadata?>(null);
        }

        public Task<GameMetadata?> GetByIdAsync(int providerId, CancellationToken ct = default) =>
            Task.FromResult<GameMetadata?>(null);

        public Task<List<GameMetadata>> SearchManyAsync(
            string title,
            int limit = 10,
            CancellationToken ct = default) =>
            Task.FromResult(new List<GameMetadata>());
    }
}
