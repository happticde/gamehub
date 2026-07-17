using System.Globalization;
using System.Text.Json;

namespace GameHub.Ingest.Services;

/// <summary>A validated page from GOG's provider-fragile library statistics response.</summary>
public sealed record GogLibraryPage(IReadOnlyList<JsonElement> Games, int TotalPages);

/// <summary>
/// Parses the minimum GOG response contract needed by ingestion without exposing provider payloads.
/// </summary>
public static class GogLibraryPageParser
{
    /// <summary>Parses one library page and rejects structures that could hide partial data.</summary>
    public static GogLibraryPage Parse(JsonElement root, int currentPage)
    {
        if (currentPage <= 0
            || root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("pages", out var pagesElement)
            || pagesElement.ValueKind != JsonValueKind.Number
            || !pagesElement.TryGetInt32(out var totalPages)
            || totalPages < 0
            || (totalPages > 0 && totalPages < currentPage)
            || (totalPages == 0 && currentPage != 1)
            || !TryGetObject(root, "_embedded", out var embedded)
            || !embedded.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            throw new GogProviderContractException();
        }

        var games = new List<JsonElement>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("game", out var game)
                || game.ValueKind != JsonValueKind.Object
                || !HasValidGameId(game))
            {
                throw new GogProviderContractException();
            }

            games.Add(game.Clone());
        }

        if ((totalPages == 0 && games.Count > 0)
            || (currentPage < totalPages && games.Count == 0))
        {
            throw new GogProviderContractException();
        }

        return new GogLibraryPage(games, totalPages);
    }

    private static bool HasValidGameId(JsonElement game)
    {
        if (!game.TryGetProperty("id", out var id))
        {
            return false;
        }

        return id.ValueKind switch
        {
            JsonValueKind.Number => id.TryGetInt64(out var numericId) && numericId > 0,
            JsonValueKind.String => long.TryParse(
                id.GetString(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var stringId) && stringId > 0,
            _ => false
        };
    }

    private static bool TryGetObject(JsonElement parent, string propertyName, out JsonElement value)
    {
        return parent.TryGetProperty(propertyName, out value)
            && value.ValueKind == JsonValueKind.Object;
    }
}

/// <summary>A secret-free signal that GOG returned an unsupported or malformed response shape.</summary>
public sealed class GogProviderContractException : InvalidOperationException
{
    private const string SafeMessage = "GOG provider returned an unexpected response.";

    /// <summary>Creates a safe provider-contract failure.</summary>
    public GogProviderContractException()
        : base(SafeMessage)
    {
    }

    /// <summary>Creates a safe provider-contract failure while retaining the local parse cause.</summary>
    public GogProviderContractException(Exception innerException)
        : base(SafeMessage, innerException)
    {
    }
}
