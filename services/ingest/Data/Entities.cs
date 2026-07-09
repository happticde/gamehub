using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using GameHub.Shared;

namespace GameHub.Ingest.Data;

// =============================================================================
// Raw Item Entities
//
// Each class represents the raw data stored from one gaming platform's API.
// They all implement IRawItem so the generic IngestControllerBase can work
// with them uniformly. The [NotMapped] properties (DisplayName, ExternalId)
// are computed at runtime and NOT stored as database columns.
// =============================================================================

/// <summary>Raw game data from the Steam API. Keyed by tenant and integer AppId.</summary>
public class SteamRawItem : IRawItem, IIngestTenantOwned
{
    public int Id { get; set; }
    public Guid TenantId { get; set; }

    [Required]
    public int AppId { get; set; }
    public string? Name { get; set; }
    public string RawJson { get; set; } = "{}";
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    public int? PlaytimeMinutes { get; set; }

    // IRawItem mappings — these are NOT stored in the database
    [NotMapped] public string? DisplayName => Name;
    [NotMapped] public string ExternalId => AppId.ToString();
}

/// <summary>Raw PSN data keyed by tenant and TitleId (e.g. "CUSA00001_00").</summary>
public class PsnRawItem : IRawItem, IIngestTenantOwned
{
    public int Id { get; set; }
    public Guid TenantId { get; set; }

    [Required]
    public string TitleId { get; set; } = string.Empty;
    public string? TitleName { get; set; }
    public string RawJson { get; set; } = "{}";
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    public int? PlaytimeMinutes { get; set; }

    // IRawItem mappings
    [NotMapped] public string? DisplayName => TitleName;
    [NotMapped] public string ExternalId => TitleId;
}

/// <summary>Raw game data from the GOG API. Keyed by tenant and long GameId.</summary>
public class GogRawItem : IRawItem, IIngestTenantOwned
{
    public int Id { get; set; }
    public Guid TenantId { get; set; }

    [Required]
    public long GameId { get; set; }
    public string? Title { get; set; }
    public string RawJson { get; set; } = "{}";
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    public int? PlaytimeMinutes { get; set; }

    // IRawItem mappings
    [NotMapped] public string? DisplayName => Title;
    [NotMapped] public string ExternalId => GameId.ToString();
}

/// <summary>Raw Epic Games data (via Legendary), keyed by tenant and AppName.</summary>
public class EpicRawItem : IRawItem, IIngestTenantOwned
{
    public int Id { get; set; }
    public Guid TenantId { get; set; }

    [Required]
    public string AppName { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string RawJson { get; set; } = "{}";
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    public int? PlaytimeMinutes { get; set; }

    // IRawItem mappings
    [NotMapped] public string? DisplayName => Title;
    [NotMapped] public string ExternalId => AppName;
}

public class IngestAuditLog : IIngestTenantOwned
{
    public int Id { get; set; }
    public Guid TenantId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Platform { get; set; } = ""; // "Steam", "GOG", "PSN", "Epic"
    public string Status { get; set; } = "";   // "Success", "PartialSuccess", "TokenExpired", "RateLimited", "MalformedResponse", "UpstreamUnavailable", "Error"
    public int GamesFound { get; set; }
    public string? ErrorMessage { get; set; }
}
