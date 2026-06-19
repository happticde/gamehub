using GameHub.Shared;

namespace GameHub.Core.Data;

/// <summary>
/// Tracks the last successful sync timestamp per platform.
/// This enables incremental sync — on subsequent runs, the Core service
/// only requests records from Ingest that were updated after this timestamp,
/// avoiding re-processing the entire dataset every time.
/// </summary>
public class SyncMetadata : ITenantOwned
{
    public int Id { get; set; }
    public Guid TenantId { get; set; }

    /// <summary>Which platform this sync record is for (Steam, PSN, GOG, Epic).</summary>
    public Platform Platform { get; set; }

    /// <summary>Timestamp of the last successful sync for this platform.</summary>
    public DateTime LastSyncedAt { get; set; }
}
