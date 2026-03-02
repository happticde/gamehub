using Microsoft.EntityFrameworkCore;

namespace GameHub.Core.Data;

public class CoreContext : DbContext
{
    public CoreContext(DbContextOptions<CoreContext> options) : base(options) { }

    // --- Unified Tables (Owned by Core) ---
    public DbSet<UnifiedGame> UnifiedGames => Set<UnifiedGame>();
    public DbSet<LibraryItem> LibraryItems => Set<LibraryItem>();

    // --- Sync Tracking ---
    /// <summary>Stores the last successful sync timestamp per platform for incremental sync.</summary>
    public DbSet<SyncMetadata> SyncMetadata => Set<SyncMetadata>();
}

