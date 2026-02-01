using Microsoft.EntityFrameworkCore;

namespace GameHub.Data;

public class GameHubContext : DbContext
{
    public GameHubContext(DbContextOptions<GameHubContext> options) : base(options) { }

    public DbSet<PsnRawItem> PsnRawItems => Set<PsnRawItem>();
    public DbSet<SteamRawItem> SteamRawItems => Set<SteamRawItem>();
}
