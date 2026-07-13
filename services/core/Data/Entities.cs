using GameHub.Shared;

namespace GameHub.Core.Data;

public enum PlayStatus
{
    Unplayed,
    Played,
    Playing,
    Completed,
    Abandoned
}

/// <summary>Supported physical console platforms for manually owned game copies.</summary>
public enum PhysicalGamePlatform
{
    PS3,
    PS4,
    PS5
}

public class UnifiedGame : ITenantOwned
{
    public int Id { get; set; }
    public Guid TenantId { get; set; }
    public string Title { get; set; } = "";
    public string Slug { get; set; } = "";
    public List<LibraryItem> Items { get; set; } = [];
    public List<PhysicalGameCopy> PhysicalCopies { get; set; } = [];

    // --- Personal Tracking ---
    public PlayStatus PlayStatus { get; set; } = PlayStatus.Unplayed;
    public int? UserRating { get; set; }
    public bool IsPlayStatusManual { get; set; } = false;
    public bool IsIgnored { get; set; } = false;

    // --- Metadata (populated by EnrichmentService via IGameMetadataProvider) ---

    /// <summary>URL to the game's cover art image.</summary>
    public string? CoverUrl { get; set; }

    /// <summary>Short text description of the game.</summary>
    public string? Summary { get; set; }

    /// <summary>Comma-separated genre names, e.g. "RPG, Action, Adventure".</summary>
    public string? Genres { get; set; }

    /// <summary>Comma-separated platform names the game released on.</summary>
    public string? Platforms { get; set; }

    /// <summary>Comma-separated URLs for high-resolution background artworks.</summary>
    public string? Artworks { get; set; }

    /// <summary>Comma-separated URLs for in-game screenshots.</summary>
    public string? Screenshots { get; set; }

    /// <summary>Comma-separated developer companies involved.</summary>
    public string? Developers { get; set; }

    /// <summary>Comma-separated publisher companies involved.</summary>
    public string? Publishers { get; set; }

    /// <summary>Automatic categorization and tenant-assigned custom tags.</summary>
    public ICollection<Tag> Tags { get; set; } = new List<Tag>();

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

/// <summary>
/// One tenant-owned physical game copy. Copies intentionally have independent identifiers so a
/// tenant may own multiple copies of the same game on the same console.
/// </summary>
public class PhysicalGameCopy : ITenantOwned
{
    public int Id { get; set; }
    public Guid TenantId { get; set; }
    public int UnifiedGameId { get; set; }
    public UnifiedGame? UnifiedGame { get; set; }
    public PhysicalGamePlatform Platform { get; set; }
}

public class LibraryItem : ITenantOwned
{
    public int Id { get; set; }
    public Guid TenantId { get; set; }
    public int UnifiedGameId { get; set; }
    public UnifiedGame? UnifiedGame { get; set; }
    public Platform Platform { get; set; }
    public string ExternalId { get; set; } = "";
    public string? SourceContentHash { get; set; }
    public DateTime LastSynced { get; set; } = DateTime.UtcNow;
}

public class EnrichmentAuditLog : ITenantOwned
{
    public int Id { get; set; }
    public Guid TenantId { get; set; }
    public int UnifiedGameId { get; set; }
    public UnifiedGame? UnifiedGame { get; set; }
    
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string ProviderName { get; set; } = "";
    public string Status { get; set; } = ""; // "Success", "NotFound", "Error"
    public string? ErrorMessage { get; set; }
    public int? ProviderGameId { get; set; }
    public string? MatchedTitle { get; set; }
}

public class SyncAuditLog : ITenantOwned
{
    public int Id { get; set; }
    public Guid TenantId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public Platform Platform { get; set; }
    public string Status { get; set; } = ""; // "Success", "PartialSuccess", "Error"
    public int ItemsProcessed { get; set; }
    public int NewGamesCreated { get; set; }
    public string? ErrorMessage { get; set; }
}

public class Tag : ITenantOwned
{
    public int Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Whether this reusable tag definition was created by the tenant.</summary>
    public bool IsCustomDefinition { get; set; }
    
    public ICollection<UnifiedGame> Games { get; set; } = new List<UnifiedGame>();
}

/// <summary>Tenant-owned association between a tag and a unified game.</summary>
public class TagAssignment : ITenantOwned
{
    public Guid TenantId { get; set; }
    public int UnifiedGameId { get; set; }
    public UnifiedGame? UnifiedGame { get; set; }
    public int TagId { get; set; }
    public Tag? Tag { get; set; }

    /// <summary>
    /// Whether the authenticated tenant explicitly assigned this tag. Automatic enrichment may
    /// update non-custom assignments, but must never remove a custom assignment.
    /// </summary>
    public bool IsCustom { get; set; }
}

/// <summary>
/// Tracks system events (sync cycles, manual merge/split actions, game count changes)
/// for display in the admin dashboard's activity feed.
/// </summary>
public class ActivityLog : ITenantOwned
{
    public int Id { get; set; }
    public Guid TenantId { get; set; }

    /// <summary>UTC timestamp when the event occurred.</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>Category of event, e.g. "SyncStarted", "SyncFinished", "Merge", "Split".</summary>
    public string EventType { get; set; } = "";

    /// <summary>Human-readable description, e.g. "Synced 42 items from Steam".</summary>
    public string Message { get; set; } = "";

    /// <summary>Optional extra payload (JSON or text) for additional context.</summary>
    public string? Details { get; set; }
}
