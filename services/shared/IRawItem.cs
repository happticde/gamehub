namespace GameHub.Shared;

/// <summary>
/// Common interface implemented by all platform-specific raw item entities
/// (SteamRawItem, PsnRawItem, GogRawItem, EpicRawItem).
///
/// This allows the generic IngestControllerBase to work with any platform entity
/// without knowing its concrete type — enabling a single set of shared logic for
/// batch upsert, paginated listing, and detail retrieval.
/// </summary>
public interface IRawItem
{
    /// <summary>Database primary key (auto-generated).</summary>
    int Id { get; set; }

    /// <summary>
    /// Human-readable game title (e.g. "The Witcher 3").
    /// Nullable because some API responses may not include a name.
    /// Each entity maps this to its platform-specific field (Name, TitleName, Title).
    /// </summary>
    string? DisplayName { get; }

    /// <summary>
    /// The platform-specific unique identifier as a string.
    /// Steam uses AppId (int→string), GOG uses GameId (long→string),
    /// PSN uses TitleId (string), Epic uses AppName (string).
    /// </summary>
    string ExternalId { get; }

    /// <summary>The full raw JSON response from the platform API, stored for future re-processing.</summary>
    string RawJson { get; set; }

    /// <summary>Timestamp of when this record was last ingested or updated.</summary>
    DateTime LastUpdated { get; set; }
}
