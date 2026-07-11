namespace GameHub.Shared;

/// <summary>
/// Lightweight DTO used to transfer game identity data from the Ingest service
/// to the Core service during sync. Optional metadata fields are best-effort
/// storefront fallbacks and must not replace enriched provider metadata.
/// </summary>
public class RawGameDto
{
    /// <summary>Platform-specific unique ID (e.g. Steam AppId, PSN TitleId).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Human-readable game name/title.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// When this record was last updated in the Ingest database.
    /// Used by the Core service for incremental sync — only records updated
    /// after the last sync timestamp are fetched.
    /// </summary>
    public DateTime LastUpdated { get; set; }

    /// <summary>SHA-256 fingerprint of the canonical source payload.</summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>Total playtime in minutes recorded by the platform (if available).</summary>
    public int? PlaytimeMinutes { get; set; }

    /// <summary>Best-effort storefront description/summary fallback.</summary>
    public string? Summary { get; set; }

    /// <summary>Best-effort storefront cover image URL fallback.</summary>
    public string? CoverUrl { get; set; }

    /// <summary>Best-effort storefront artwork URL fallback.</summary>
    public string? ArtworkUrl { get; set; }

    /// <summary>Best-effort comma-separated developer names fallback.</summary>
    public string? Developers { get; set; }

    /// <summary>Best-effort comma-separated publisher names fallback.</summary>
    public string? Publishers { get; set; }

    /// <summary>Best-effort storefront release-date fallback.</summary>
    public DateTime? FirstReleaseDate { get; set; }
}
