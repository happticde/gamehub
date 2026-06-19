using System.Security.Claims;
using GameHub.Core.Data;
using GameHub.Core.Services;
using GameHub.Shared.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GameHub.Tests;

/// <summary>Tests trusted request and background tenant resolution.</summary>
public sealed class TenantContextTests
{
    private static readonly Guid TenantA = TenantDefaults.DefaultTenantId;
    private static readonly Guid TenantB = Guid.Parse("00000000-0000-0000-0000-000000000002");

    [Fact]
    public void TenantId_NoHttpRequest_UsesDefaultTenant()
    {
        var context = CreateTenantContext(httpContext: null);

        Assert.Equal(TenantA, context.TenantId);
    }

    [Fact]
    public void TenantId_AuthenticatedPrincipal_UsesTrustedClaim()
    {
        var httpContext = CreateAuthenticatedHttpContext(
            new Claim(GameHubClaimTypes.TenantId, TenantB.ToString("D")));
        var context = CreateTenantContext(httpContext);

        Assert.Equal(TenantB, context.TenantId);
    }

    [Fact]
    public void TenantId_AuthenticatedPrincipalWithoutTenantClaim_Throws()
    {
        var context = CreateTenantContext(CreateAuthenticatedHttpContext());

        var exception = Assert.Throws<InvalidOperationException>(() => context.TenantId);

        Assert.Contains(GameHubClaimTypes.TenantId, exception.Message);
    }

    [Fact]
    public void TenantId_BackgroundScope_OverridesAndRestoresRequestTenant()
    {
        var executionContext = new TenantExecutionContext();
        var httpContext = CreateAuthenticatedHttpContext(
            new Claim(GameHubClaimTypes.TenantId, TenantA.ToString("D")));
        var context = CreateTenantContext(httpContext, executionContext);

        using (executionContext.BeginScope(TenantB))
        {
            Assert.Equal(TenantB, context.TenantId);
        }

        Assert.Equal(TenantA, context.TenantId);
    }

    [Fact]
    public async Task TenantJobRunner_AllTenants_WritesWithinEachTenantScope()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var serviceProvider = CreateServiceProvider(connection);
        await SeedSecondTenantAsync(serviceProvider);
        var runner = serviceProvider.GetRequiredService<ITenantJobRunner>();
        var visited = new List<Guid>();

        await runner.RunForEachTenantAsync(async (services, tenantId, cancellationToken) =>
        {
            var tenantContext = services.GetRequiredService<ITenantContext>();
            var db = services.GetRequiredService<CoreContext>();
            Assert.Equal(tenantId, tenantContext.TenantId);
            visited.Add(tenantId);
            db.ActivityLogs.Add(new ActivityLog
            {
                EventType = "TenantJob",
                Message = tenantId.ToString("D")
            });
            await db.SaveChangesAsync(cancellationToken);
        });

        Assert.Equal([TenantA, TenantB], visited);
        using var verifyScope = serviceProvider.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<CoreContext>();
        var logs = await verifyDb.ActivityLogs
            .IgnoreQueryFilters()
            .OrderBy(log => log.TenantId)
            .ToListAsync();
        Assert.Equal([TenantA, TenantB], logs.Select(log => log.TenantId));
    }

    [Fact]
    public async Task TenantJobRunner_OneTenantFails_AttemptsRemainingTenants()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var serviceProvider = CreateServiceProvider(connection);
        await SeedSecondTenantAsync(serviceProvider);
        var runner = serviceProvider.GetRequiredService<ITenantJobRunner>();
        var visited = new List<Guid>();

        var exception = await Assert.ThrowsAsync<AggregateException>(() =>
            runner.RunForEachTenantAsync((_, tenantId, _) =>
            {
                visited.Add(tenantId);
                return tenantId == TenantA
                    ? Task.FromException(new InvalidOperationException("Tenant A failed"))
                    : Task.CompletedTask;
            }));

        Assert.Equal([TenantA, TenantB], visited);
        Assert.Single(exception.InnerExceptions);
    }

    private static AuthenticatedTenantContext CreateTenantContext(
        HttpContext? httpContext,
        TenantExecutionContext? executionContext = null) =>
        new(
            new HttpContextAccessor { HttpContext = httpContext },
            executionContext ?? new TenantExecutionContext());

    private static DefaultHttpContext CreateAuthenticatedHttpContext(params Claim[] claims)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
        };
        return context;
    }

    private static ServiceProvider CreateServiceProvider(SqliteConnection connection)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddSingleton<TenantExecutionContext>();
        services.AddScoped<ITenantContext, AuthenticatedTenantContext>();
        services.AddDbContext<CoreContext>(options => options.UseSqlite(connection));
        services.AddSingleton<ITenantJobRunner, TenantJobRunner>();
        return services.BuildServiceProvider();
    }

    private static async Task SeedSecondTenantAsync(ServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
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
}
