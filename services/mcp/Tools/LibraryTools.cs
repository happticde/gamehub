using ModelContextProtocol.Server;
using System.ComponentModel;

namespace GameHub.Mcp.Tools;

/// <summary>
/// MCP tool set for querying and managing the GameHub unified game library.
/// Each method is exposed as an MCP tool callable by any compatible AI client.
/// </summary>
[McpServerToolType]
public sealed class LibraryTools
{
    private readonly CoreApiClient _api;

    public LibraryTools(CoreApiClient api)
    {
        _api = api;
    }

    // =========================================================================
    // READ tools
    // =========================================================================

    [McpServerTool]
    [Description("""
        Search the game library by title, genre, platform, or play status.
        Returns a paginated JSON list of matched games.

        Parameters (all optional):
        - search: partial title to match (case-insensitive)
        - platform: filter to one platform: Steam, GOG, PSN, Epic, Xbox
        - playStatus: Unplayed | Played | Playing | Completed | Abandoned
        - genre: genre tag to filter by (e.g. "RPG", "Action")
        - sortBy: title | rating | releasedate
        - sortOrder: asc | desc  (default: asc)
        - page: page number (default: 1)
        - pageSize: items per page, max 100 (default: 20)
        """)]
    public async Task<string> SearchLibrary(
        [Description("Partial title to search for")] string? search = null,
        [Description("Platform filter: Steam, GOG, PSN, Epic, Xbox")] string? platform = null,
        [Description("Play status filter: Unplayed, Played, Playing, Completed, Abandoned")] string? playStatus = null,
        [Description("Genre tag to filter by, e.g. 'RPG', 'Action'")] string? genre = null,
        [Description("Sort field: title, rating, releasedate")] string? sortBy = null,
        [Description("Sort direction: asc or desc")] string sortOrder = "asc",
        [Description("Page number (starts at 1)")] int page = 1,
        [Description("Items per page (max 100)")] int pageSize = 20)
    {
        var result = await _api.SearchLibraryAsync(search, platform, playStatus, genre, sortBy, sortOrder, page, pageSize);
        return result ?? "Error: Could not reach the GameHub Core service. Is it running on port 5001?";
    }

    [McpServerTool]
    [Description("""
        Get the full details for a specific game by its numeric ID.
        Returns title, summary, genres, platforms, screenshots, artworks,
        play status, user rating, IGDB rating, release date, and more.
        """)]
    public async Task<string> GetGame(
        [Description("The numeric ID of the game (use search_library to find IDs)")] int gameId)
    {
        var result = await _api.GetGameAsync(gameId);
        return result ?? $"Error: Game with ID {gameId} not found, or the Core service is unavailable.";
    }

    [McpServerTool]
    [Description("""
        Pick a random game from the library. Useful for deciding what to play next.
        Returns a JSON object with the game ID; call get_game to get full details.

        Parameters (all optional — combine them to narrow the pool):
        - platform: Steam | GOG | PSN | Epic | Xbox
        - playStatus: Unplayed | Played | Playing | Completed | Abandoned
        - genre: genre tag (e.g. "Horror", "RPG")
        """)]
    public async Task<string> GetRandomGame(
        [Description("Platform to restrict random pick to")] string? platform = null,
        [Description("Play status to restrict random pick to")] string? playStatus = null,
        [Description("Genre tag to restrict random pick to")] string? genre = null)
    {
        var result = await _api.GetRandomGameAsync(platform, playStatus, genre);
        return result ?? "Error: No games matched the filters, or the Core service is unavailable.";
    }

    [McpServerTool]
    [Description("""
        Get comprehensive library statistics.
        Returns: total game count, platform breakdown, genre distribution,
        play status counts, rating histogram, top-rated games, and recently added games.
        """)]
    public async Task<string> GetStatistics()
    {
        var result = await _api.GetStatsAsync();
        return result ?? "Error: Could not reach the GameHub Core service. Is it running on port 5001?";
    }

    [McpServerTool]
    [Description("List all genre tags currently in the library, sorted alphabetically.")]
    public async Task<string> ListTags()
    {
        var result = await _api.GetTagsAsync();
        return result ?? "Error: Could not reach the GameHub Core service. Is it running on port 5001?";
    }

    // =========================================================================
    // WRITE tools
    // =========================================================================

    [McpServerTool]
    [Description("""
        Update the play status of a game in the library.
        Valid values for playStatus: Unplayed | Played | Playing | Completed | Abandoned
        This action is marked as manual, so future syncs will not overwrite it.
        """)]
    public async Task<string> SetPlayStatus(
        [Description("The numeric ID of the game to update")] int gameId,
        [Description("New play status: Unplayed, Played, Playing, Completed, or Abandoned")] string playStatus)
    {
        // Parse and validate the play status string
        if (!Enum.TryParse<PlayStatusEnum>(playStatus, ignoreCase: true, out var parsedStatus))
        {
            return $"Error: '{playStatus}' is not a valid play status. " +
                   "Valid values are: Unplayed, Played, Playing, Completed, Abandoned.";
        }

        var body = new { PlayStatus = parsedStatus.ToString(), UserRating = (int?)null };
        var result = await _api.UpdatePersonalTrackingAsync(gameId, body);
        return result ?? $"Error: Could not update game {gameId}. Is the Core service running?";
    }

    [McpServerTool]
    [Description("""
        Set a personal star rating (1–5) for a game in the library.
        This is separate from the IGDB critic rating stored in the 'Rating' field.
        Pass null to clear the rating.
        """)]
    public async Task<string> SetRating(
        [Description("The numeric ID of the game to rate")] int gameId,
        [Description("Star rating from 1 to 5, or null to clear it")] int? rating)
    {
        if (rating.HasValue && (rating < 1 || rating > 5))
        {
            return "Error: Rating must be between 1 and 5 (or null to clear).";
        }

        // We need to also send PlayStatus — PATCH requires the full PersonalUpdateRequest.
        // First, get the current game to read its existing play status.
        var current = await _api.GetGameAsync(gameId);
        if (current == null)
        {
            return $"Error: Game with ID {gameId} not found, or the Core service is unavailable.";
        }

        // Parse the current play status from the JSON response.
        // Default to Unplayed if we can't parse it (safe fallback).
        var currentStatus = "Unplayed";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(current);
            if (doc.RootElement.TryGetProperty("playStatus", out var ps))
            {
                currentStatus = ps.GetString() ?? "Unplayed";
            }
        }
        catch { /* fallback to Unplayed */ }

        var body = new { PlayStatus = currentStatus, UserRating = rating };
        var result = await _api.UpdatePersonalTrackingAsync(gameId, body);
        return result ?? $"Error: Could not update rating for game {gameId}. Is the Core service running?";
    }

    // =========================================================================
    // Private helpers
    // =========================================================================

    /// <summary>
    /// Local enum mirror for play status validation — avoids a project reference to Core.
    /// Must stay in sync with PlayStatus in GameHub.Core.Data.Entities.
    /// </summary>
    private enum PlayStatusEnum
    {
        Unplayed,
        Played,
        Playing,
        Completed,
        Abandoned
    }
}
