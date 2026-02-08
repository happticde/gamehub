using Microsoft.EntityFrameworkCore;

namespace GameHub.Core.Data;

public class CoreContext : DbContext
{
    public CoreContext(DbContextOptions<CoreContext> options) : base(options) { }

    // --- Unified Tables (Owned by Core) ---
    public DbSet<UnifiedGame> UnifiedGames => Set<UnifiedGame>();
    public DbSet<LibraryItem> LibraryItems => Set<LibraryItem>();
}
