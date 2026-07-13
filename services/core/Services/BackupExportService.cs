using System.Data;
using GameHub.Core.Data;
using GameHub.Core.Models.Backup;
using Microsoft.EntityFrameworkCore;

namespace GameHub.Core.Services;

/// <summary>
/// Creates provider-independent snapshots of the durable unified library state.
/// </summary>
public sealed class BackupExportService
{
    /// <summary>Stable identifier written to every GameHub backup.</summary>
    public const string FormatName = "gamehub-backup";

    /// <summary>Current backup schema version. V2 adds physical copies.</summary>
    public const int CurrentFormatVersion = 2;

    /// <summary>Oldest backup version accepted by restore.</summary>
    public const int MinimumSupportedFormatVersion = 1;

    private readonly CoreContext _db;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes the backup export service.
    /// </summary>
    public BackupExportService(CoreContext db, TimeProvider timeProvider)
    {
        _db = db;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Creates a consistent, deterministically ordered V1 backup snapshot.
    /// </summary>
    public async Task<GameHubBackupV1> CreateBackupAsync(CancellationToken cancellationToken = default)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var games = await _db.UnifiedGames
            .AsNoTracking()
            .AsSplitQuery()
            .Include(game => game.Items)
            .Include(game => game.PhysicalCopies)
            .Include(game => game.Tags)
            .OrderBy(game => game.Slug)
            .ThenBy(game => game.Id)
            .ToListAsync(cancellationToken);

        var manualLinks = await _db.ManualLinks
            .AsNoTracking()
            .OrderBy(link => link.UnifiedGameId)
            .ThenBy(link => link.Platform)
            .ThenBy(link => link.ExternalId)
            .ToListAsync(cancellationToken);

        var linksByGame = manualLinks.ToLookup(link => link.UnifiedGameId);
        var backupGames = games.Select(game => new BackupGameV1(
            game.Title,
            game.Slug,
            new BackupPersonalTrackingV1(
                game.PlayStatus,
                game.UserRating,
                game.IsPlayStatusManual,
                game.IsIgnored),
            new BackupMetadataV1(
                game.CoverUrl,
                game.Summary,
                game.Genres,
                game.Platforms,
                game.Artworks,
                game.Screenshots,
                game.Developers,
                game.Publishers,
                game.FirstReleaseDate,
                game.Rating,
                game.MetadataProviderId,
                game.MetadataSource,
                game.EnrichedAt),
            game.Tags
                .Select(tag => tag.Name)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            game.Items
                .OrderBy(item => item.Platform)
                .ThenBy(item => item.ExternalId, StringComparer.Ordinal)
                .Select(item => new BackupLibraryItemV1(
                    item.Platform,
                    item.ExternalId,
                    item.LastSynced))
                .ToList(),
            linksByGame[game.Id]
                .Select(link => new BackupManualLinkV1(
                    link.Platform,
                    link.ExternalId))
                .ToList(),
            game.PhysicalCopies
                .OrderBy(copy => copy.Platform)
                .ThenBy(copy => copy.Id)
                .Select(copy => new BackupPhysicalGameCopyV2(copy.Platform))
                .ToList()))
            .ToList();

        await transaction.CommitAsync(cancellationToken);

        return new GameHubBackupV1(
            FormatName,
            CurrentFormatVersion,
            _timeProvider.GetUtcNow(),
            backupGames);
    }
}
