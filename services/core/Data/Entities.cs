using GameHub.Shared;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace GameHub.Core.Data;

[Index(nameof(Slug), IsUnique = true)]
public class UnifiedGame
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Slug { get; set; } = "";
    public List<LibraryItem> Items { get; set; } = [];

    // --- Metadata (populated by EnrichmentService via IGameMetadataProvider) ---

    /// <summary>URL to the game's cover art image.</summary>
    public string? CoverUrl { get; set; }

    /// <summary>Short text description of the game.</summary>
    public string? Summary { get; set; }

    /// <summary>Comma-separated genre names, e.g. "RPG, Action, Adventure".</summary>
    public string? Genres { get; set; }

    /// <summary>Original release date of the game.</summary>
    public DateTime? FirstReleaseDate { get; set; }

    /// <summary>Critic/aggregate rating on a 0–100 scale.</summary>
    public double? Rating { get; set; }

    /// <summary>Provider-specific ID for re-enrichment without re-searching.</summary>
    public int? MetadataProviderId { get; set; }

    /// <summary>Name of the provider that supplied the metadata (e.g. "IGDB").</summary>
    public string? MetadataSource { get; set; }

    /// <summary>When metadata was last enriched. Null means never enriched.</summary>
    public DateTime? EnrichedAt { get; set; }
}

[Index(nameof(UnifiedGameId), nameof(Platform), IsUnique = true)]
public class LibraryItem
{
    public int Id { get; set; }
    public int UnifiedGameId { get; set; }
    public UnifiedGame? UnifiedGame { get; set; }
    public Platform Platform { get; set; }
    public string ExternalId { get; set; } = "";
    public DateTime LastSynced { get; set; } = DateTime.UtcNow;
}

public class EnrichmentAuditLog
{
    public int Id { get; set; }
    public int UnifiedGameId { get; set; }
    public UnifiedGame? UnifiedGame { get; set; }
    
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string ProviderName { get; set; } = "";
    public string Status { get; set; } = ""; // "Success", "NotFound", "Error"
    public string? ErrorMessage { get; set; }
    public int? ProviderGameId { get; set; }
    public string? MatchedTitle { get; set; }
}

public class SyncAuditLog
{
    public int Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public Platform Platform { get; set; }
    public string Status { get; set; } = ""; // "Success", "Error"
    public int ItemsProcessed { get; set; }
    public int NewGamesCreated { get; set; }
    public string? ErrorMessage { get; set; }
}
