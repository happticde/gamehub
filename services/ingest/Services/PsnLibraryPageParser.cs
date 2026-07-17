using System.Text.Json;

namespace GameHub.Ingest.Services;

/// <summary>A validated page from PSN's provider-fragile purchased-library response.</summary>
public sealed record PsnLibraryPage(IReadOnlyList<JsonElement> Games, bool IsLast);

/// <summary>
/// Parses the minimum PSN response contract needed by ingestion without exposing provider payloads.
/// </summary>
public static class PsnLibraryPageParser
{
    /// <summary>Parses one purchased-library page and rejects structures that could lose data or loop.</summary>
    public static PsnLibraryPage Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || HasGraphQlErrors(root)
            || !TryGetObject(root, "data", out var data)
            || !TryGetObject(data, "purchasedTitlesRetrieve", out var retrieve)
            || !retrieve.TryGetProperty("games", out var gamesElement)
            || gamesElement.ValueKind != JsonValueKind.Array
            || !TryGetObject(retrieve, "pageInfo", out var pageInfo)
            || !pageInfo.TryGetProperty("isLast", out var isLastElement)
            || isLastElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new PsnProviderContractException();
        }

        var games = new List<JsonElement>();
        foreach (var game in gamesElement.EnumerateArray())
        {
            if (game.ValueKind != JsonValueKind.Object)
            {
                throw new PsnProviderContractException();
            }

            games.Add(game.Clone());
        }

        var isLast = isLastElement.GetBoolean();
        if (!isLast && games.Count == 0)
        {
            throw new PsnProviderContractException();
        }

        return new PsnLibraryPage(games, isLast);
    }

    private static bool HasGraphQlErrors(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errors))
        {
            return false;
        }

        return errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() > 0;
    }

    private static bool TryGetObject(JsonElement parent, string propertyName, out JsonElement value)
    {
        return parent.TryGetProperty(propertyName, out value)
            && value.ValueKind == JsonValueKind.Object;
    }
}

/// <summary>A secret-free signal that PSN returned an unsupported or malformed response shape.</summary>
public sealed class PsnProviderContractException : InvalidOperationException
{
    private const string SafeMessage = "PSN provider returned an unexpected response.";

    /// <summary>Creates a safe provider-contract failure.</summary>
    public PsnProviderContractException()
        : base(SafeMessage)
    {
    }

    /// <summary>Creates a safe provider-contract failure while retaining the local parse cause.</summary>
    public PsnProviderContractException(Exception innerException)
        : base(SafeMessage, innerException)
    {
    }
}
