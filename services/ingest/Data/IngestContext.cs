using Microsoft.EntityFrameworkCore;
using GameHub.Ingest.Services;

namespace GameHub.Ingest.Data;

public class IngestContext : DbContext
{
    private readonly IIngestTenantContext _tenantContext;

    public IngestContext(
        DbContextOptions<IngestContext> options,
        IIngestTenantContext tenantContext) : base(options)
    {
        _tenantContext = tenantContext;
    }

    public DbSet<PsnRawItem> PsnRawItems => Set<PsnRawItem>();
    public DbSet<SteamRawItem> SteamRawItems => Set<SteamRawItem>();
    public DbSet<GogRawItem> GogRawItems => Set<GogRawItem>();
    public DbSet<EpicRawItem> EpicRawItems => Set<EpicRawItem>();
    public DbSet<PlatformCredential> PlatformCredentials => Set<PlatformCredential>();
    
    // --- Audit Logs ---
    public DbSet<IngestAuditLog> IngestAuditLogs => Set<IngestAuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureTenantOwned(modelBuilder.Entity<SteamRawItem>());
        ConfigureTenantOwned(modelBuilder.Entity<PsnRawItem>());
        ConfigureTenantOwned(modelBuilder.Entity<GogRawItem>());
        ConfigureTenantOwned(modelBuilder.Entity<EpicRawItem>());
        ConfigureTenantOwned(modelBuilder.Entity<IngestAuditLog>());
        ConfigureTenantOwned(modelBuilder.Entity<PlatformCredential>());

        // Platform identifiers are unique only inside one user's source library. Equivalent
        // store IDs may safely coexist for different tenants in the shared Ingest database.
        modelBuilder.Entity<SteamRawItem>()
            .HasIndex(item => new { item.TenantId, item.AppId })
            .IsUnique();
        modelBuilder.Entity<PsnRawItem>()
            .HasIndex(item => new { item.TenantId, item.TitleId })
            .IsUnique();
        modelBuilder.Entity<GogRawItem>()
            .HasIndex(item => new { item.TenantId, item.GameId })
            .IsUnique();
        modelBuilder.Entity<EpicRawItem>()
            .HasIndex(item => new { item.TenantId, item.AppName })
            .IsUnique();
        modelBuilder.Entity<IngestAuditLog>()
            .HasIndex(log => log.TenantId);
        modelBuilder.Entity<PlatformCredential>()
            .HasIndex(credential => new { credential.TenantId, credential.Platform })
            .IsUnique();
        modelBuilder.Entity<PlatformCredential>()
            .Property(credential => credential.EncryptedPayload)
            .HasMaxLength(PlatformCredentialService.MaximumPayloadSizeBytes)
            .IsRequired();
        modelBuilder.Entity<PlatformCredential>()
            .Property(credential => credential.Nonce)
            .HasMaxLength(PlatformCredentialProtector.NonceSizeBytes)
            .IsRequired();
        modelBuilder.Entity<PlatformCredential>()
            .Property(credential => credential.AuthenticationTag)
            .HasMaxLength(PlatformCredentialProtector.AuthenticationTagSizeBytes)
            .IsRequired();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyTenantOwnership();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        ApplyTenantOwnership();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void ConfigureTenantOwned<TEntity>(
        Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> entity)
        where TEntity : class, IIngestTenantOwned
    {
        entity.Property(record => record.TenantId)
            .HasDefaultValue(IngestTenantDefaults.DefaultTenantId);
        entity.HasQueryFilter(record => record.TenantId == _tenantContext.TenantId);
    }

    private void ApplyTenantOwnership()
    {
        var tenantId = _tenantContext.TenantId;
        if (tenantId == Guid.Empty)
        {
            throw new InvalidOperationException("The active Ingest tenant identifier cannot be empty.");
        }

        foreach (var entry in ChangeTracker.Entries<IIngestTenantOwned>())
        {
            if (entry.State == EntityState.Added)
            {
                if (entry.Entity.TenantId != Guid.Empty && entry.Entity.TenantId != tenantId)
                {
                    throw new InvalidOperationException("Cannot create Ingest data for a different tenant.");
                }

                entry.Entity.TenantId = tenantId;
                continue;
            }

            if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                var tenantProperty = entry.Property(record => record.TenantId);
                if (tenantProperty.OriginalValue != tenantId || entry.Entity.TenantId != tenantId)
                {
                    throw new InvalidOperationException(
                        "Cannot modify or delete Ingest data owned by a different tenant.");
                }
            }
        }
    }
}
