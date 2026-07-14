using System.Data;
using GameHub.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GameHub.Core.Services;

/// <summary>
/// Performs tenant-filtered physical capture reads and atomic writes. Tenant ownership is supplied
/// exclusively by <see cref="CoreContext"/>'s trusted tenant context and save guard.
/// </summary>
public sealed class PhysicalGameCaptureService
{
    private const int MaxManualTitleLength = 200;
    private const int MaxSlugAttempts = 8;

    private readonly CoreContext _db;

    /// <summary>Initializes the physical capture service.</summary>
    public PhysicalGameCaptureService(CoreContext db)
    {
        _db = db;
    }

    /// <summary>Returns every tenant-local game candidate previously confirmed for a GTIN.</summary>
    public async Task<IReadOnlyList<PhysicalGtinCandidate>> LookupAsync(
        string normalizedGtin,
        CancellationToken cancellationToken = default)
    {
        var matches = await _db.PhysicalGameCopies
            .AsNoTracking()
            .Where(copy => copy.NormalizedGtin == normalizedGtin)
            .Select(copy => new
            {
                copy.UnifiedGameId,
                Title = copy.UnifiedGame!.Title,
                Slug = copy.UnifiedGame.Slug,
                copy.Platform
            })
            .ToListAsync(cancellationToken);

        return matches
            .GroupBy(copy => new { copy.UnifiedGameId, copy.Title, copy.Slug })
            .Select(group => new PhysicalGtinCandidate(
                group.Key.UnifiedGameId,
                group.Key.Title,
                group.Key.Slug,
                group.Select(copy => copy.Platform).Distinct().OrderBy(platform => platform).ToList(),
                group.Count()))
            .OrderBy(candidate => candidate.Title)
            .ThenBy(candidate => candidate.GameId)
            .ToList();
    }

    /// <summary>Adds a copy to an existing tenant-owned game in one transaction.</summary>
    public async Task<PhysicalCaptureResult?> CaptureExistingAsync(
        int gameId,
        PhysicalGamePlatform platform,
        string? normalizedGtin,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var game = await _db.UnifiedGames
            .FirstOrDefaultAsync(candidate => candidate.Id == gameId, cancellationToken);
        if (game is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var copy = new PhysicalGameCopy
        {
            UnifiedGameId = game.Id,
            Platform = platform,
            NormalizedGtin = normalizedGtin
        };
        _db.PhysicalGameCopies.Add(copy);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new PhysicalCaptureResult(game.Id, game.Title, game.Slug, false, copy);
    }

    /// <summary>
    /// Creates a manual tenant-owned game and its first physical copy atomically. Slug collisions
    /// receive the first available deterministic <c>-manual-N</c> suffix; they are never merged or
    /// interpreted as proof that two games are duplicates.
    /// </summary>
    public async Task<PhysicalCaptureResult> CaptureManualAsync(
        string title,
        PhysicalGamePlatform platform,
        string? normalizedGtin,
        CancellationToken cancellationToken = default)
    {
        var normalizedTitle = title.Trim();
        if (normalizedTitle.Length == 0 || normalizedTitle.Length > MaxManualTitleLength)
        {
            throw new PhysicalCaptureValidationException(
                $"A manual game title between 1 and {MaxManualTitleLength} characters is required.");
        }

        var baseSlug = UnificationService.GenerateSlug(normalizedTitle);
        if (baseSlug.Length == 0)
        {
            throw new PhysicalCaptureValidationException(
                "The manual game title must contain at least one letter or digit.");
        }

        for (var attempt = 0; attempt < MaxSlugAttempts; attempt++)
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            try
            {
                var slug = await SelectAvailableSlugAsync(baseSlug, cancellationToken);
                var game = new UnifiedGame
                {
                    Title = normalizedTitle,
                    Slug = slug,
                    PhysicalCopies =
                    [
                        new PhysicalGameCopy
                        {
                            Platform = platform,
                            NormalizedGtin = normalizedGtin
                        }
                    ]
                };

                _db.UnifiedGames.Add(game);
                await _db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new PhysicalCaptureResult(
                    game.Id,
                    game.Title,
                    game.Slug,
                    true,
                    game.PhysicalCopies[0]);
            }
            catch (Exception exception) when (IsRetryableWriteConflict(exception) && attempt < MaxSlugAttempts - 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                _db.ChangeTracker.Clear();
                await Task.Delay(TimeSpan.FromMilliseconds(10 * (attempt + 1)), cancellationToken);
            }
        }

        throw new InvalidOperationException("A unique manual game slug could not be allocated.");
    }

    private async Task<string> SelectAvailableSlugAsync(
        string baseSlug,
        CancellationToken cancellationToken)
    {
        var prefix = $"{baseSlug}-manual-";
        var existingSlugs = await _db.UnifiedGames
            .AsNoTracking()
            .Where(game => game.Slug == baseSlug || game.Slug.StartsWith(prefix))
            .Select(game => game.Slug)
            .ToListAsync(cancellationToken);

        if (!existingSlugs.Contains(baseSlug, StringComparer.Ordinal))
        {
            return baseSlug;
        }

        var occupied = existingSlugs.ToHashSet(StringComparer.Ordinal);
        for (var suffix = 2; suffix < int.MaxValue; suffix++)
        {
            var candidate = $"{prefix}{suffix}";
            if (!occupied.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("A unique manual game slug could not be allocated.");
    }

    private static bool IsRetryableWriteConflict(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                return true;
            }

            if (current is SqliteException { SqliteExtendedErrorCode: 2067 })
            {
                return true;
            }

            if (current is SqliteException { SqliteErrorCode: 5 or 6 })
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>One tenant-local game candidate associated with a normalized GTIN.</summary>
public sealed record PhysicalGtinCandidate(
    int GameId,
    string Title,
    string Slug,
    IReadOnlyList<PhysicalGamePlatform> Platforms,
    int CopyCount);

/// <summary>Result of one confirmed physical capture write.</summary>
public sealed record PhysicalCaptureResult(
    int GameId,
    string Title,
    string Slug,
    bool CreatedManualGame,
    PhysicalGameCopy Copy);

/// <summary>Safe validation failure for a manual physical capture request.</summary>
public sealed class PhysicalCaptureValidationException(string message) : Exception(message);
