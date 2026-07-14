using System.Data;
using System.Text.Json;
using GameHub.Core.Data;
using GameHub.Core.Models.Backup;
using GameHub.Shared;
using Microsoft.EntityFrameworkCore;

namespace GameHub.Core.Services;

/// <summary>
/// Parses, validates, and transactionally restores GameHub backup documents.
/// </summary>
public sealed class BackupImportService
{
    /// <summary>Maximum number of games accepted in one backup document.</summary>
    public const int MaxGameCount = 100_000;

    private const int MaxValidationErrors = 100;

    private readonly CoreContext _db;
    private readonly BackupJsonSerializer _backupJson;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes the backup import service.
    /// </summary>
    public BackupImportService(
        CoreContext db,
        BackupJsonSerializer backupJson,
        TimeProvider timeProvider)
    {
        _db = db;
        _backupJson = backupJson;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Parses a JSON stream and returns both the document and its structured validation result.
    /// </summary>
    public async Task<(GameHubBackupV1? Backup, BackupValidationResult Validation)> ReadAndValidateAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var backup = await _backupJson.DeserializeAsync(stream, cancellationToken);
            return (backup, Validate(backup));
        }
        catch (JsonException exception)
        {
            return (null, InvalidResult(
                "invalid_json",
                exception.Message,
                exception.Path));
        }
        catch (NotSupportedException exception)
        {
            return (null, InvalidResult(
                "unsupported_json",
                exception.Message));
        }
    }

    /// <summary>
    /// Validates a parsed backup without changing the database.
    /// </summary>
    public BackupValidationResult Validate(GameHubBackupV1? backup)
    {
        if (backup is null)
        {
            return InvalidResult("empty_document", "The backup document is empty.");
        }

        var errors = new List<BackupValidationError>();
        void AddError(string code, string message, string? path = null)
        {
            if (errors.Count < MaxValidationErrors)
            {
                errors.Add(new BackupValidationError(code, message, path));
            }
        }

        if (!string.Equals(backup.Format, BackupExportService.FormatName, StringComparison.Ordinal))
        {
            AddError(
                "invalid_format",
                $"Expected format '{BackupExportService.FormatName}'.",
                "format");
        }

        if (backup.FormatVersion is < BackupExportService.MinimumSupportedFormatVersion
            or > BackupExportService.CurrentFormatVersion)
        {
            AddError(
                "unsupported_version",
                $"Backup version {backup.FormatVersion} is not supported.",
                "formatVersion");
        }

        if (backup.Games is null)
        {
            AddError("missing_games", "The games collection is required.", "games");
            return CreateResult(backup, errors);
        }

        if (backup.Games.Count > MaxGameCount)
        {
            AddError(
                "too_many_games",
                $"A backup may contain at most {MaxGameCount} games.",
                "games");
            return CreateResult(backup, errors);
        }

        var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var libraryItems = new HashSet<(Platform Platform, string ExternalId)>();
        var manualLinks = new HashSet<(Platform Platform, string ExternalId)>();

        for (var gameIndex = 0; gameIndex < backup.Games.Count; gameIndex++)
        {
            var game = backup.Games[gameIndex];
            var gamePath = $"games[{gameIndex}]";

            if (game is null)
            {
                AddError("missing_game", "Game entries cannot be null.", gamePath);
                continue;
            }

            if (string.IsNullOrWhiteSpace(game.Title))
            {
                AddError("missing_title", "A game title is required.", $"{gamePath}.title");
            }

            if (string.IsNullOrWhiteSpace(game.Slug))
            {
                AddError("missing_slug", "A game slug is required.", $"{gamePath}.slug");
            }
            else if (!slugs.Add(game.Slug))
            {
                AddError("duplicate_slug", $"Duplicate game slug '{game.Slug}'.", $"{gamePath}.slug");
            }

            ValidatePersonalTracking(game.PersonalTracking, gamePath, AddError);
            ValidateMetadata(game.Metadata, gamePath, AddError);
            ValidateTags(game.Tags, gamePath, AddError);
            ValidateItems(game.Items, gamePath, libraryItems, AddError);
            ValidateManualLinks(game.ManualLinks, gamePath, manualLinks, AddError);
            ValidatePhysicalCopies(
                game.PhysicalCopies,
                backup.FormatVersion,
                gamePath,
                AddError);
        }

        return CreateResult(backup, errors);
    }

    /// <summary>
    /// Restores a valid backup into an empty Core database in one transaction.
    /// </summary>
    public async Task<BackupRestoreResult> RestoreAsync(
        GameHubBackupV1 backup,
        CancellationToken cancellationToken = default)
    {
        var validation = Validate(backup);
        if (!validation.IsValid)
        {
            return new BackupRestoreResult(
                false,
                "Backup validation failed.",
                validation.Summary);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var targetHasData = await _db.UnifiedGames.AnyAsync(cancellationToken)
            || await _db.LibraryItems.AnyAsync(cancellationToken)
            || await _db.PhysicalGameCopies.AnyAsync(cancellationToken)
            || await _db.ManualLinks.AnyAsync(cancellationToken)
            || await _db.Tags.AnyAsync(cancellationToken);

        if (targetHasData)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new BackupRestoreResult(
                false,
                "The target library must be empty before restore.",
                validation.Summary);
        }

        var tagsByName = new Dictionary<string, Tag>(StringComparer.OrdinalIgnoreCase);

        foreach (var backupGame in backup.Games)
        {
            var game = new UnifiedGame
            {
                Title = backupGame.Title,
                Slug = backupGame.Slug,
                PlayStatus = backupGame.PersonalTracking.PlayStatus,
                UserRating = backupGame.PersonalTracking.UserRating,
                IsPlayStatusManual = backupGame.PersonalTracking.IsPlayStatusManual,
                IsIgnored = backupGame.PersonalTracking.IsIgnored,
                CoverUrl = backupGame.Metadata.CoverUrl,
                Summary = backupGame.Metadata.Summary,
                Genres = backupGame.Metadata.Genres,
                Platforms = backupGame.Metadata.Platforms,
                Artworks = backupGame.Metadata.Artworks,
                Screenshots = backupGame.Metadata.Screenshots,
                Developers = backupGame.Metadata.Developers,
                Publishers = backupGame.Metadata.Publishers,
                FirstReleaseDate = backupGame.Metadata.FirstReleaseDate,
                Rating = backupGame.Metadata.Rating,
                MetadataProviderId = backupGame.Metadata.MetadataProviderId,
                MetadataSource = backupGame.Metadata.MetadataSource,
                EnrichedAt = backupGame.Metadata.EnrichedAt,
                Items = backupGame.Items
                    .Select(item => new LibraryItem
                    {
                        Platform = item.Platform,
                        ExternalId = item.ExternalId,
                        LastSynced = item.LastSynced
                    })
                    .ToList(),
                PhysicalCopies = (backupGame.PhysicalCopies ?? [])
                    .Select(copy => new PhysicalGameCopy
                    {
                        Platform = copy.Platform,
                        NormalizedGtin = copy.NormalizedGtin
                    })
                    .ToList()
            };

            foreach (var tagName in backupGame.Tags)
            {
                if (!tagsByName.TryGetValue(tagName, out var tag))
                {
                    tag = new Tag { Name = tagName };
                    tagsByName.Add(tagName, tag);
                }

                game.Tags.Add(tag);
            }

            _db.UnifiedGames.Add(game);

            foreach (var backupLink in backupGame.ManualLinks)
            {
                _db.ManualLinks.Add(new ManualLink
                {
                    Platform = backupLink.Platform,
                    ExternalId = backupLink.ExternalId,
                    UnifiedGame = game
                });
            }
        }

        _db.ActivityLogs.Add(new ActivityLog
        {
            Timestamp = _timeProvider.GetUtcNow().UtcDateTime,
            EventType = "BackupRestore",
            Message = $"Restored {validation.Summary.GameCount} games from a V{backup.FormatVersion} backup"
        });

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new BackupRestoreResult(
            true,
            "Backup restored successfully.",
            validation.Summary);
    }

    private static void ValidatePersonalTracking(
        BackupPersonalTrackingV1? personalTracking,
        string gamePath,
        Action<string, string, string?> addError)
    {
        if (personalTracking is null)
        {
            addError(
                "missing_personal_tracking",
                "Personal tracking data is required.",
                $"{gamePath}.personalTracking");
            return;
        }

        if (!Enum.IsDefined(typeof(PlayStatus), personalTracking.PlayStatus))
        {
            addError(
                "invalid_play_status",
                $"Unknown play status '{personalTracking.PlayStatus}'.",
                $"{gamePath}.personalTracking.playStatus");
        }

        if (personalTracking.UserRating is < 1 or > 5)
        {
            addError(
                "invalid_user_rating",
                "User rating must be between 1 and 5.",
                $"{gamePath}.personalTracking.userRating");
        }
    }

    private static void ValidateMetadata(
        BackupMetadataV1? metadata,
        string gamePath,
        Action<string, string, string?> addError)
    {
        if (metadata is null)
        {
            addError("missing_metadata", "Metadata is required.", $"{gamePath}.metadata");
            return;
        }

        if (metadata.Rating is double rating && (!double.IsFinite(rating) || rating is < 0 or > 100))
        {
            addError(
                "invalid_metadata_rating",
                "Metadata rating must be between 0 and 100.",
                $"{gamePath}.metadata.rating");
        }
    }

    private static void ValidateTags(
        IReadOnlyList<string>? tags,
        string gamePath,
        Action<string, string, string?> addError)
    {
        if (tags is null)
        {
            addError("missing_tags", "The tags collection is required.", $"{gamePath}.tags");
            return;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var tagIndex = 0; tagIndex < tags.Count; tagIndex++)
        {
            var tag = tags[tagIndex];
            var path = $"{gamePath}.tags[{tagIndex}]";

            if (string.IsNullOrWhiteSpace(tag))
            {
                addError("missing_tag", "Tag names cannot be empty.", path);
            }
            else if (!names.Add(tag))
            {
                addError("duplicate_tag", $"Duplicate tag '{tag}'.", path);
            }
        }
    }

    private static void ValidateItems(
        IReadOnlyList<BackupLibraryItemV1>? items,
        string gamePath,
        HashSet<(Platform Platform, string ExternalId)> allItems,
        Action<string, string, string?> addError)
    {
        if (items is null)
        {
            addError("missing_items", "The platform items collection is required.", $"{gamePath}.items");
            return;
        }

        var gamePlatforms = new HashSet<Platform>();
        for (var itemIndex = 0; itemIndex < items.Count; itemIndex++)
        {
            var item = items[itemIndex];
            var itemPath = $"{gamePath}.items[{itemIndex}]";

            if (item is null)
            {
                addError("missing_item", "Platform items cannot be null.", itemPath);
                continue;
            }

            if (!Enum.IsDefined(typeof(Platform), item.Platform))
            {
                addError("invalid_platform", $"Unknown platform '{item.Platform}'.", $"{itemPath}.platform");
            }

            if (!gamePlatforms.Add(item.Platform))
            {
                addError(
                    "duplicate_game_platform",
                    $"A game can contain only one '{item.Platform}' item.",
                    $"{itemPath}.platform");
            }

            if (string.IsNullOrWhiteSpace(item.ExternalId))
            {
                addError("missing_external_id", "Platform external ID is required.", $"{itemPath}.externalId");
            }
            else if (!allItems.Add((item.Platform, item.ExternalId)))
            {
                addError(
                    "duplicate_library_item",
                    $"Platform item '{item.Platform}:{item.ExternalId}' occurs more than once.",
                    itemPath);
            }
        }
    }

    private static void ValidateManualLinks(
        IReadOnlyList<BackupManualLinkV1>? links,
        string gamePath,
        HashSet<(Platform Platform, string ExternalId)> allLinks,
        Action<string, string, string?> addError)
    {
        if (links is null)
        {
            addError(
                "missing_manual_links",
                "The manual links collection is required.",
                $"{gamePath}.manualLinks");
            return;
        }

        for (var linkIndex = 0; linkIndex < links.Count; linkIndex++)
        {
            var link = links[linkIndex];
            var linkPath = $"{gamePath}.manualLinks[{linkIndex}]";

            if (link is null)
            {
                addError("missing_manual_link", "Manual links cannot be null.", linkPath);
                continue;
            }

            if (!Enum.IsDefined(typeof(Platform), link.Platform))
            {
                addError("invalid_platform", $"Unknown platform '{link.Platform}'.", $"{linkPath}.platform");
            }

            if (string.IsNullOrWhiteSpace(link.ExternalId))
            {
                addError("missing_external_id", "Manual link external ID is required.", $"{linkPath}.externalId");
            }
            else if (!allLinks.Add((link.Platform, link.ExternalId)))
            {
                addError(
                    "duplicate_manual_link",
                    $"Manual link '{link.Platform}:{link.ExternalId}' occurs more than once.",
                    linkPath);
            }
        }
    }

    private static void ValidatePhysicalCopies(
        IReadOnlyList<BackupPhysicalGameCopyV2>? copies,
        int formatVersion,
        string gamePath,
        Action<string, string, string?> addError)
    {
        if (formatVersion == 1)
        {
            if (copies is not null)
            {
                addError(
                    "physical_copies_not_supported_in_v1",
                    "Physical copies require backup format version 2.",
                    $"{gamePath}.physicalCopies");
            }

            return;
        }

        if (copies is null)
        {
            addError(
                "missing_physical_copies",
                "The physical copies collection is required in a V2 or V3 backup.",
                $"{gamePath}.physicalCopies");
            return;
        }

        for (var copyIndex = 0; copyIndex < copies.Count; copyIndex++)
        {
            var copy = copies[copyIndex];
            var copyPath = $"{gamePath}.physicalCopies[{copyIndex}]";
            if (copy is null)
            {
                addError("missing_physical_copy", "Physical copies cannot be null.", copyPath);
                continue;
            }

            if (!Enum.IsDefined(copy.Platform))
            {
                addError(
                    "invalid_physical_platform",
                    $"Unknown physical platform '{copy.Platform}'.",
                    $"{copyPath}.platform");
            }

            if (formatVersion == 2 && copy.NormalizedGtin is not null)
            {
                addError(
                    "physical_gtin_not_supported_in_v2",
                    "Normalized GTINs require backup format version 3.",
                    $"{copyPath}.normalizedGtin");
                continue;
            }

            if (formatVersion == 3 && copy.NormalizedGtin is not null)
            {
                var normalization = GtinNormalizer.Normalize(copy.NormalizedGtin);
                if (!normalization.IsValid)
                {
                    addError(
                        "invalid_normalized_gtin",
                        normalization.Error!,
                        $"{copyPath}.normalizedGtin");
                }
                else if (!string.Equals(
                    normalization.NormalizedGtin,
                    copy.NormalizedGtin,
                    StringComparison.Ordinal))
                {
                    addError(
                        "non_canonical_normalized_gtin",
                        "A backup GTIN must use the canonical 14-digit representation.",
                        $"{copyPath}.normalizedGtin");
                }
            }
        }
    }

    private static BackupValidationResult CreateResult(
        GameHubBackupV1 backup,
        IReadOnlyList<BackupValidationError> errors) =>
        new(
            errors.Count == 0,
            backup.Format,
            backup.FormatVersion,
            BuildSummary(backup),
            errors);

    private static BackupValidationResult InvalidResult(
        string code,
        string message,
        string? path = null) =>
        new(
            false,
            null,
            null,
            new BackupSummary(0, 0, 0, 0),
            [new BackupValidationError(code, message, path)]);

    private static BackupSummary BuildSummary(GameHubBackupV1 backup)
    {
        if (backup.Games is null)
        {
            return new BackupSummary(0, 0, 0, 0);
        }

        var itemCount = 0;
        var manualLinkCount = 0;
        var physicalCopyCount = 0;
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var game in backup.Games)
        {
            if (game is null)
            {
                continue;
            }

            itemCount += game.Items?.Count ?? 0;
            manualLinkCount += game.ManualLinks?.Count ?? 0;
            physicalCopyCount += game.PhysicalCopies?.Count ?? 0;

            if (game.Tags is not null)
            {
                foreach (var tag in game.Tags.Where(tag => !string.IsNullOrWhiteSpace(tag)))
                {
                    tags.Add(tag);
                }
            }
        }

        return new BackupSummary(
            backup.Games.Count,
            itemCount,
            manualLinkCount,
            tags.Count,
            physicalCopyCount);
    }
}
