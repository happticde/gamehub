using Microsoft.EntityFrameworkCore;

namespace GameHub.Ingest.Data;

public class IngestContext : DbContext
{
    public IngestContext(DbContextOptions<IngestContext> options) : base(options) { }

    public DbSet<PsnRawItem> PsnRawItems => Set<PsnRawItem>();
    public DbSet<SteamRawItem> SteamRawItems => Set<SteamRawItem>();
    public DbSet<GogRawItem> GogRawItems => Set<GogRawItem>();
    public DbSet<EpicRawItem> EpicRawItems => Set<EpicRawItem>();
}
