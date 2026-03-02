using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using GameHub.Shared;
using Microsoft.EntityFrameworkCore;

namespace GameHub.Ingest.Data;

// =============================================================================
// Raw Item Entities
//
// Each class represents the raw data stored from one gaming platform's API.
// They all implement IRawItem so the generic IngestControllerBase can work
// with them uniformly. The [NotMapped] properties (DisplayName, ExternalId)
// are computed at runtime and NOT stored as database columns.
// =============================================================================

/// <summary>Raw game data from the Steam API. Keyed by integer AppId.</summary>
[Index(nameof(AppId), IsUnique = true)]
public class SteamRawItem : IRawItem
{
    public int Id { get; set; }

    [Required]
    public int AppId { get; set; }
    public string? Name { get; set; }
    public string RawJson { get; set; } = "{}";
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    // IRawItem mappings — these are NOT stored in the database
    [NotMapped] public string? DisplayName => Name;
    [NotMapped] public string ExternalId => AppId.ToString();
}

/// <summary>Raw game data from the PlayStation Network API. Keyed by string TitleId (e.g. "CUSA00001_00").</summary>
[Index(nameof(TitleId), IsUnique = true)]
public class PsnRawItem : IRawItem
{
    public int Id { get; set; }

    [Required]
    public string TitleId { get; set; } = string.Empty;
    public string? TitleName { get; set; }
    public string RawJson { get; set; } = "{}";
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    // IRawItem mappings
    [NotMapped] public string? DisplayName => TitleName;
    [NotMapped] public string ExternalId => TitleId;
}

/// <summary>Raw game data from the GOG API. Keyed by long GameId.</summary>
[Index(nameof(GameId), IsUnique = true)]
public class GogRawItem : IRawItem
{
    public int Id { get; set; }

    [Required]
    public long GameId { get; set; }
    public string? Title { get; set; }
    public string RawJson { get; set; } = "{}";
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    // IRawItem mappings
    [NotMapped] public string? DisplayName => Title;
    [NotMapped] public string ExternalId => GameId.ToString();
}

/// <summary>Raw game data from the Epic Games API (via Legendary). Keyed by string AppName.</summary>
[Index(nameof(AppName), IsUnique = true)]
public class EpicRawItem : IRawItem
{
    public int Id { get; set; }

    [Required]
    public string AppName { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string RawJson { get; set; } = "{}";
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    // IRawItem mappings
    [NotMapped] public string? DisplayName => Title;
    [NotMapped] public string ExternalId => AppName;
}
