using Microsoft.EntityFrameworkCore;

namespace GameHub.Core.Data;

public class CoreContext : DbContext
{
    private readonly ITenantContext _tenantContext;

    public CoreContext(DbContextOptions<CoreContext> options, ITenantContext tenantContext) : base(options)
    {
        _tenantContext = tenantContext;
    }

    /// <summary>All configured tenant boundaries.</summary>
    public DbSet<Tenant> Tenants => Set<Tenant>();

    /// <summary>Local accounts that authenticate into tenant boundaries.</summary>
    public DbSet<UserAccount> UserAccounts => Set<UserAccount>();

    // --- Unified Tables (Owned by Core) ---
    public DbSet<UnifiedGame> UnifiedGames => Set<UnifiedGame>();
    public DbSet<LibraryItem> LibraryItems => Set<LibraryItem>();
    public DbSet<Tag> Tags => Set<Tag>();

    // --- Sync Tracking ---
    /// <summary>Stores the last successful sync timestamp per platform for incremental sync.</summary>
    public DbSet<SyncMetadata> SyncMetadata => Set<SyncMetadata>();

    // --- Manual Overrides ---
    /// <summary>Stores manual merge/split overrides that bypass automatic slug-based matching.</summary>
    public DbSet<ManualLink> ManualLinks => Set<ManualLink>();

    // --- Audit Logs ---
    /// <summary>History of metadata enrichment attempts and outcomes.</summary>
    public DbSet<EnrichmentAuditLog> EnrichmentAuditLogs => Set<EnrichmentAuditLog>();
    
    /// <summary>History of ingestion syncs from platform sources.</summary>
    public DbSet<SyncAuditLog> SyncAuditLogs => Set<SyncAuditLog>();

    /// <summary>System activity events for the admin activity feed.</summary>
    public DbSet<ActivityLog> ActivityLogs => Set<ActivityLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.Property(tenant => tenant.Name).HasMaxLength(200);
            entity.HasData(new Tenant
            {
                Id = TenantDefaults.DefaultTenantId,
                Name = "Default",
                CreatedAtUtc = new DateTime(2026, 6, 19, 0, 0, 0, DateTimeKind.Utc)
            });
        });

        modelBuilder.Entity<UserAccount>(entity =>
        {
            // Accounts are identity records, not tenant-filtered library rows. The token
            // endpoint must locate an account by email before it knows which tenant is valid.
            // Only a successful password check allows this TenantId into a signed JWT.
            entity.Property(account => account.Email).HasMaxLength(320);
            entity.Property(account => account.NormalizedEmail).HasMaxLength(320);
            entity.Property(account => account.DisplayName).HasMaxLength(100);
            entity.Property(account => account.PasswordHash).HasMaxLength(1024);
            // Global email uniqueness provides one unambiguous login identity. TenantId is
            // unique because this initial account model supports one owner account per tenant.
            entity.HasIndex(account => account.NormalizedEmail).IsUnique();
            entity.HasIndex(account => account.TenantId).IsUnique();
            entity.HasOne(account => account.Tenant)
                .WithMany()
                .HasForeignKey(account => account.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        ConfigureTenantOwned(modelBuilder.Entity<UnifiedGame>());
        ConfigureTenantOwned(modelBuilder.Entity<LibraryItem>());
        ConfigureTenantOwned(modelBuilder.Entity<Tag>());
        ConfigureTenantOwned(modelBuilder.Entity<SyncMetadata>());
        ConfigureTenantOwned(modelBuilder.Entity<ManualLink>());
        ConfigureTenantOwned(modelBuilder.Entity<EnrichmentAuditLog>());
        ConfigureTenantOwned(modelBuilder.Entity<SyncAuditLog>());
        ConfigureTenantOwned(modelBuilder.Entity<ActivityLog>());

        modelBuilder.Entity<UnifiedGame>()
            .HasIndex(game => new { game.TenantId, game.Slug })
            .IsUnique();
        modelBuilder.Entity<LibraryItem>()
            .HasIndex(item => new { item.TenantId, item.UnifiedGameId, item.Platform })
            .IsUnique();
        modelBuilder.Entity<Tag>()
            .HasIndex(tag => new { tag.TenantId, tag.Name })
            .IsUnique();
        modelBuilder.Entity<SyncMetadata>()
            .HasIndex(metadata => new { metadata.TenantId, metadata.Platform })
            .IsUnique();
        modelBuilder.Entity<ManualLink>()
            .HasIndex(link => new { link.TenantId, link.Platform, link.ExternalId })
            .IsUnique();
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

    private void ConfigureTenantOwned<TEntity>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> entity)
        where TEntity : class, ITenantOwned
    {
        entity.Property(record => record.TenantId)
            .HasDefaultValue(TenantDefaults.DefaultTenantId);
        entity.HasQueryFilter(record => record.TenantId == _tenantContext.TenantId);
        entity.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(record => record.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private void ApplyTenantOwnership()
    {
        var tenantId = _tenantContext.TenantId;
        if (tenantId == Guid.Empty)
        {
            throw new InvalidOperationException("The active tenant identifier cannot be empty.");
        }

        foreach (var entry in ChangeTracker.Entries<ITenantOwned>())
        {
            if (entry.State == EntityState.Added)
            {
                if (entry.Entity.TenantId != Guid.Empty && entry.Entity.TenantId != tenantId)
                {
                    throw new InvalidOperationException("Cannot create a record for a different tenant.");
                }

                entry.Entity.TenantId = tenantId;
                continue;
            }

            if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                var tenantProperty = entry.Property(record => record.TenantId);
                if (tenantProperty.OriginalValue != tenantId || entry.Entity.TenantId != tenantId)
                {
                    throw new InvalidOperationException("Cannot modify or delete a record owned by a different tenant.");
                }
            }
        }
    }
}
