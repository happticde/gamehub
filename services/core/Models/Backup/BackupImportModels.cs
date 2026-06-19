namespace GameHub.Core.Models.Backup;

/// <summary>
/// Counts the durable records represented by a backup document.
/// </summary>
/// <param name="GameCount">Number of unified games.</param>
/// <param name="PlatformItemCount">Number of owned platform entries.</param>
/// <param name="ManualLinkCount">Number of manual matching overrides.</param>
/// <param name="TagCount">Number of distinct tag names.</param>
public sealed record BackupSummary(
    int GameCount,
    int PlatformItemCount,
    int ManualLinkCount,
    int TagCount);

/// <summary>
/// A machine-readable validation issue with an optional JSON path.
/// </summary>
/// <param name="Code">Stable error code for API clients.</param>
/// <param name="Message">Human-readable explanation.</param>
/// <param name="Path">Location within the backup document, when available.</param>
public sealed record BackupValidationError(
    string Code,
    string Message,
    string? Path = null);

/// <summary>
/// Result returned when a backup upload is parsed and validated.
/// </summary>
/// <param name="IsValid">Whether the document can be restored.</param>
/// <param name="Format">Parsed format identifier, when available.</param>
/// <param name="FormatVersion">Parsed schema version, when available.</param>
/// <param name="Summary">Best-effort record counts for preview.</param>
/// <param name="Errors">Validation issues; empty when valid.</param>
public sealed record BackupValidationResult(
    bool IsValid,
    string? Format,
    int? FormatVersion,
    BackupSummary Summary,
    IReadOnlyList<BackupValidationError> Errors);

/// <summary>
/// Result of an attempted restore operation.
/// </summary>
/// <param name="Restored">Whether data was committed.</param>
/// <param name="Message">Outcome suitable for display to the user.</param>
/// <param name="Summary">Record counts represented by the backup.</param>
public sealed record BackupRestoreResult(
    bool Restored,
    string Message,
    BackupSummary Summary);
