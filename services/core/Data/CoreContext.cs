using Microsoft.EntityFrameworkCore;

namespace GameHub.Core.Data;

public class CoreContext : DbContext
{
    public CoreContext(DbContextOptions<CoreContext> options) : base(options) { }

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
}

