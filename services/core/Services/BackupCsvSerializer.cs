using System.Globalization;
using System.Text;
using GameHub.Core.Models.Backup;

namespace GameHub.Core.Services;

/// <summary>
/// Creates a spreadsheet-friendly, non-restorable CSV view of a GameHub backup.
/// </summary>
public sealed class BackupCsvSerializer
{
    private static readonly string[] Headers =
    [
        "Title",
        "Slug",
        "Play Status",
        "User Rating",
        "Play Status Manual",
        "Ignored",
        "Owned Platforms",
        "Platform IDs",
        "Last Synced",
        "Manual Links",
        "Physical Copies",
        "Physical GTINs",
        "Tags",
        "Genres",
        "Release Platforms",
        "Developers",
        "Publishers",
        "First Release Date",
        "Critic Rating",
        "Metadata Source",
        "Metadata Provider ID",
        "Enriched At",
        "Cover URL",
        "Artworks",
        "Screenshots",
        "Summary"
    ];

    /// <summary>
    /// Serializes one row per unified game using UTF-8 with BOM and CRLF line endings.
    /// </summary>
    public byte[] Serialize(GameHubBackupV1 backup)
    {
        var csv = new StringBuilder();
        AppendRow(csv, Headers);

        foreach (var game in backup.Games)
        {
            AppendRow(csv,
            [
                game.Title,
                game.Slug,
                game.PersonalTracking.PlayStatus.ToString(),
                FormatNumber(game.PersonalTracking.UserRating),
                game.PersonalTracking.IsPlayStatusManual.ToString(),
                game.PersonalTracking.IsIgnored.ToString(),
                string.Join("; ", game.Items.Select(item => item.Platform)),
                string.Join("; ", game.Items.Select(item => $"{item.Platform}:{item.ExternalId}")),
                string.Join("; ", game.Items.Select(item => $"{item.Platform}:{FormatDateTime(item.LastSynced)}")),
                string.Join("; ", game.ManualLinks.Select(link => $"{link.Platform}:{link.ExternalId}")),
                string.Join("; ", (game.PhysicalCopies ?? []).Select(copy => copy.Platform)),
                string.Join("; ", (game.PhysicalCopies ?? [])
                    .Where(copy => copy.NormalizedGtin is not null)
                    .Select(copy => $"{copy.Platform}:'{copy.NormalizedGtin}")),
                string.Join("; ", game.Tags),
                game.Metadata.Genres,
                game.Metadata.Platforms,
                game.Metadata.Developers,
                game.Metadata.Publishers,
                game.Metadata.FirstReleaseDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                FormatNumber(game.Metadata.Rating),
                game.Metadata.MetadataSource,
                FormatNumber(game.Metadata.MetadataProviderId),
                game.Metadata.EnrichedAt is DateTime enrichedAt ? FormatDateTime(enrichedAt) : null,
                game.Metadata.CoverUrl,
                game.Metadata.Artworks,
                game.Metadata.Screenshots,
                game.Metadata.Summary
            ]);
        }

        var body = Encoding.UTF8.GetBytes(csv.ToString());
        var preamble = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble();
        var result = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, result, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, result, preamble.Length, body.Length);
        return result;
    }

    private static void AppendRow(StringBuilder csv, IReadOnlyList<string?> values)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (index > 0)
            {
                csv.Append(',');
            }

            csv.Append(EscapeCell(values[index]));
        }

        csv.Append("\r\n");
    }

    private static string EscapeCell(string? value)
    {
        var safeValue = value ?? string.Empty;
        var firstContentCharacter = safeValue.AsSpan().TrimStart();

        if (!firstContentCharacter.IsEmpty && firstContentCharacter[0] is '=' or '+' or '-' or '@')
        {
            safeValue = $"'{safeValue}";
        }

        if (safeValue.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            return safeValue;
        }

        return $"\"{safeValue.Replace("\"", "\"\"")}\"";
    }

    private static string? FormatNumber<T>(T? value)
        where T : struct, IFormattable =>
        value?.ToString(null, CultureInfo.InvariantCulture);

    private static string FormatDateTime(DateTime value) =>
        value.ToString("O", CultureInfo.InvariantCulture);
}
