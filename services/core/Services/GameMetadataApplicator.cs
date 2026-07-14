using GameHub.Core.Data;
using GameHub.Shared;
using Microsoft.EntityFrameworkCore;

namespace GameHub.Core.Services;

/// <summary>
/// Applies provider-neutral metadata to one tenant-owned game while preserving custom tag
/// assignments. The caller controls the surrounding transaction and persists the changes.
/// </summary>
internal static class GameMetadataApplicator
{
    /// <summary>Applies one successful provider response to a unified game.</summary>
    public static async Task ApplyAsync(
        CoreContext db,
        UnifiedGame game,
        GameMetadata metadata,
        string providerName,
        CancellationToken cancellationToken = default)
    {
        game.CoverUrl = metadata.CoverUrl;
        game.Summary = metadata.Summary;
        game.Genres = Join(metadata.Genres);
        game.Platforms = Join(metadata.Platforms);
        game.Artworks = Join(metadata.Artworks);
        game.Screenshots = Join(metadata.Screenshots);
        game.Developers = Join(metadata.Developers);
        game.Publishers = Join(metadata.Publishers);
        game.FirstReleaseDate = metadata.FirstReleaseDate;
        game.Rating = metadata.Rating;
        game.MetadataProviderId = metadata.ProviderId;
        game.MetadataSource = providerName;
        game.EnrichedAt = DateTime.UtcNow;

        // Automatic genre assignments are replaceable, but a tenant's explicit assignment is
        // durable even when a later provider response no longer contains the same genre.
        var genreNames = (metadata.Genres ?? [])
            .Where(genre => !string.IsNullOrWhiteSpace(genre))
            .Select(genre => genre.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var assignments = await db.Set<TagAssignment>()
            .Include(assignment => assignment.Tag)
            .Where(assignment => assignment.UnifiedGameId == game.Id)
            .ToListAsync(cancellationToken);
        var assignmentsByName = new Dictionary<string, TagAssignment>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in assignments.Where(assignment => assignment.Tag is not null))
        {
            assignmentsByName.TryAdd(assignment.Tag!.Name, assignment);
        }

        var currentGenreNames = genreNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in assignments.Where(assignment => !assignment.IsCustom))
        {
            if (assignment.Tag is null || !currentGenreNames.Contains(assignment.Tag.Name))
            {
                db.Set<TagAssignment>().Remove(assignment);
            }
        }

        foreach (var genreName in genreNames)
        {
            if (assignmentsByName.ContainsKey(genreName))
            {
                continue;
            }

            var normalizedName = genreName.ToLower();
            var tag = await db.Tags.FirstOrDefaultAsync(
                candidate => candidate.Name.ToLower() == normalizedName,
                cancellationToken);
            if (tag is null)
            {
                tag = new Tag { Name = genreName };
                db.Tags.Add(tag);
            }

            db.Set<TagAssignment>().Add(new TagAssignment
            {
                UnifiedGame = game,
                Tag = tag,
                IsCustom = false
            });
        }
    }

    private static string? Join(IReadOnlyCollection<string>? values) =>
        values?.Count > 0 ? string.Join(", ", values) : null;
}
