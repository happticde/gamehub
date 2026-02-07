using Microsoft.EntityFrameworkCore;

namespace GameHub.Ingest.Data;

public class GameHubContext : DbContext
{
    public GameHubContext(DbContextOptions<GameHubContext> options) : base(options) { }

    public DbSet<PsnRawItem> PsnRawItems => Set<PsnRawItem>();
    public DbSet<SteamRawItem> SteamRawItems => Set<SteamRawItem>();
    public DbSet<GogRawItem> GogRawItems => Set<GogRawItem>();
    public DbSet<EpicRawItem> EpicRawItems => Set<EpicRawItem>();
}
