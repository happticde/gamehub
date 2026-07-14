using GameHub.Core.Data;
using GameHub.Shared;

namespace GameHub.Core.Models.Backup;

/// <summary>
/// Versioned provider-independent GameHub backup document. The type name is retained for source
/// compatibility; V2 adds physical copies and V3 adds optional normalized GTINs while V1 and V2
/// remain importable.
/// </summary>
/// <param name="Format">Stable format identifier used during validation.</param>
/// <param name="FormatVersion">Schema version of this document.</param>
/// <param name="ExportedAtUtc">UTC time at which the snapshot was created.</param>
/// <param name="Games">Complete unified library snapshot.</param>
public sealed record GameHubBackupV1(
    string Format,
    int FormatVersion,
    DateTimeOffset ExportedAtUtc,
    IReadOnlyList<BackupGameV1> Games);

/// <summary>
/// A unified game and all durable user-owned relationships needed for restoration.
/// </summary>
/// <param name="Title">Display title.</param>
/// <param name="Slug">Unique normalized title used by GameHub matching.</param>
/// <param name="PersonalTracking">User-managed play state and rating.</param>
/// <param name="Metadata">Enriched game metadata.</param>
/// <param name="Tags">Associated tag names.</param>
/// <param name="Items">Owned platform entries.</param>
/// <param name="ManualLinks">Manual matching overrides targeting this game.</param>
/// <param name="PhysicalCopies">
/// Physical copies in V2 and V3. Null represents a V1 document where the field did not exist.
/// </param>
public sealed record BackupGameV1(
    string Title,
    string Slug,
    BackupPersonalTrackingV1 PersonalTracking,
    BackupMetadataV1 Metadata,
    IReadOnlyList<string> Tags,
    IReadOnlyList<BackupLibraryItemV1> Items,
    IReadOnlyList<BackupManualLinkV1> ManualLinks,
    IReadOnlyList<BackupPhysicalGameCopyV2>? PhysicalCopies = null);

/// <summary>
/// Personal library state that must survive a backup and restore cycle.
/// </summary>
/// <param name="PlayStatus">Current play status.</param>
/// <param name="UserRating">Optional personal rating from 1 to 5.</param>
/// <param name="IsPlayStatusManual">Whether synchronization may update the play status.</param>
/// <param name="IsIgnored">Whether the game is hidden from the default library view.</param>
public sealed record BackupPersonalTrackingV1(
    PlayStatus PlayStatus,
    int? UserRating,
    bool IsPlayStatusManual,
    bool IsIgnored);

/// <summary>
/// Provider metadata stored on a unified game.
/// </summary>
/// <param name="CoverUrl">Cover image URL.</param>
/// <param name="Summary">Game description.</param>
/// <param name="Genres">Comma-separated genre names.</param>
/// <param name="Platforms">Comma-separated release platform names.</param>
/// <param name="Artworks">Comma-separated artwork URLs.</param>
/// <param name="Screenshots">Comma-separated screenshot URLs.</param>
/// <param name="Developers">Comma-separated developer names.</param>
/// <param name="Publishers">Comma-separated publisher names.</param>
/// <param name="FirstReleaseDate">Original release date.</param>
/// <param name="Rating">Aggregate provider rating.</param>
/// <param name="MetadataProviderId">Provider-specific game identifier.</param>
/// <param name="MetadataSource">Provider name.</param>
/// <param name="EnrichedAt">Time of the last successful enrichment.</param>
public sealed record BackupMetadataV1(
    string? CoverUrl,
    string? Summary,
    string? Genres,
    string? Platforms,
    string? Artworks,
    string? Screenshots,
    string? Developers,
    string? Publishers,
    DateTime? FirstReleaseDate,
    double? Rating,
    int? MetadataProviderId,
    string? MetadataSource,
    DateTime? EnrichedAt);

/// <summary>
/// An owned game entry from a platform library.
/// </summary>
/// <param name="Platform">Source platform.</param>
/// <param name="ExternalId">Platform-specific identifier.</param>
/// <param name="LastSynced">Last synchronization time.</param>
public sealed record BackupLibraryItemV1(
    Platform Platform,
    string ExternalId,
    DateTime LastSynced);

/// <summary>
/// A manual platform-to-unified-game matching override.
/// </summary>
/// <param name="Platform">Source platform.</param>
/// <param name="ExternalId">Platform-specific identifier.</param>
public sealed record BackupManualLinkV1(
    Platform Platform,
    string ExternalId);

/// <summary>One physical game copy introduced by V2 and extended with an optional GTIN in V3.</summary>
/// <param name="Platform">Supported physical PlayStation platform.</param>
/// <param name="NormalizedGtin">Optional canonical GTIN-14. This must be null in V2.</param>
public sealed record BackupPhysicalGameCopyV2(
    PhysicalGamePlatform Platform,
    string? NormalizedGtin = null);
