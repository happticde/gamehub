using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace GameHub.Ingest.Data;

[Index(nameof(AppId), IsUnique = true)]
public class SteamRawItem
{
    public int Id { get; set; }

    [Required]
    public int AppId { get; set; }
    public string? Name { get; set; }
    public string RawJson { get; set; } = "{}";
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

[Index(nameof(TitleId), IsUnique = true)]
public class PsnRawItem
{
    public int Id { get; set; }

    [Required]
    public string TitleId { get; set; } = string.Empty;
    public string? TitleName { get; set; }
    public string RawJson { get; set; } = "{}";
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

[Index(nameof(GameId), IsUnique = true)]
public class GogRawItem
{
    public int Id { get; set; }

    [Required]
    public long GameId { get; set; }
    public string? Title { get; set; }
    public string RawJson { get; set; } = "{}";
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

[Index(nameof(AppName), IsUnique = true)]
public class EpicRawItem
{
    public int Id { get; set; }

    [Required]
    public string AppName { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string RawJson { get; set; } = "{}";
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
