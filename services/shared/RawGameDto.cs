namespace GameHub.Shared;

/// <summary>
/// Lightweight DTO used to transfer game identity data from the Ingest service
/// to the Core service during sync. Contains just the external ID, display name,
/// and the timestamp of when the record was last updated in the ingest database.
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

    /// <summary>Total playtime in minutes recorded by the platform (if available).</summary>
    public int? PlaytimeMinutes { get; set; }
}
