using System.ComponentModel;
using System.Text.Json;
using GameHub.Core.Data;
using GameHub.Core.Services;
using GameHub.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using JsonOptions = Microsoft.AspNetCore.Mvc.JsonOptions;

namespace GameHub.Core.Mcp;

internal sealed class RemoteLibraryTools(LibraryOperations library, IOptions<JsonOptions> json)
{
    [McpServerTool(Name = "search_library", ReadOnly = true)]
    [Description("Search the library by title, platform, play status, or genre. Returns a paginated list.")]
    public async Task<string> SearchLibrary(
        CancellationToken cancellationToken,
        string? search = null,
        [Description("Steam, GOG, PSN, Epic, or Xbox")] string? platform = null,
        [Description("Unplayed, Played, Playing, Completed, or Abandoned")] string? playStatus = null,
        string? genre = null,
        [Description("title, rating, or releasedate")] string? sortBy = null,
        [Description("asc or desc")] string sortOrder = "asc",
        int page = 1,
        [Description("Items per page, maximum 100")] int pageSize = 20) =>
        Serialize(await library.SearchAsync(search, TagFilter(genre), Filter<Platform>(platform),
            null, Filter<PlayStatus>(playStatus), null, null, sortBy, sortOrder, false, page,
            Math.Clamp(pageSize, 1, 100), cancellationToken));

    [McpServerTool(Name = "get_game", ReadOnly = true)]
    [Description("Get the library details for one game by its numeric ID.")]
    public async Task<string> GetGame(int gameId, CancellationToken cancellationToken) =>
        Serialize(await library.GetGameAsync(gameId, cancellationToken)
            ?? throw new McpException($"Game with ID {gameId} not found."));

    [McpServerTool(Name = "get_random_game", ReadOnly = true)]
    [Description("Pick a random game ID, optionally filtered by platform, play status, or genre.")]
    public async Task<string> GetRandomGame(CancellationToken cancellationToken, string? platform = null,
        string? playStatus = null, string? genre = null)
    {
        var id = await library.GetRandomIdAsync(null, TagFilter(genre), Filter<Platform>(platform),
            null, Filter<PlayStatus>(playStatus), null, null, false, cancellationToken);
        return id is null ? throw new McpException("No games matched the filters.") : Serialize(new { Id = id.Value });
    }

    [McpServerTool(Name = "get_statistics", ReadOnly = true)]
    [Description("Get the library's totals, platform and genre breakdowns, play status counts, and ratings.")]
    public async Task<string> GetStatistics(CancellationToken cancellationToken) =>
        Serialize(await library.GetStatsAsync(cancellationToken));

    [McpServerTool(Name = "list_tags", ReadOnly = true)]
    [Description("List the library's tag names in alphabetical order.")]
    public async Task<string> ListTags(CancellationToken cancellationToken) =>
        Serialize(await library.GetTagsAsync(cancellationToken));

    [McpServerTool(Name = "set_play_status")]
    [Authorize(Policy = PrivateMcpTransport.TrackingPolicy)]
    [Description("Set a game's manual play status: Unplayed, Played, Playing, Completed, or Abandoned.")]
    public async Task<string> SetPlayStatus(int gameId, string playStatus, CancellationToken cancellationToken) =>
        Serialize(await library.UpdatePlayStatusAsync(gameId, Parse<PlayStatus>(playStatus), cancellationToken)
            ?? throw new McpException($"Game with ID {gameId} not found."));

    [McpServerTool(Name = "set_rating")]
    [Authorize(Policy = PrivateMcpTransport.TrackingPolicy)]
    [Description("Set a personal rating from 1 to 5, or clear it with null.")]
    public async Task<string> SetRating(int gameId, int? rating, CancellationToken cancellationToken)
    {
        if (rating is < 1 or > 5) throw new McpException("Rating must be between 1 and 5, or null.");
        return Serialize(await library.UpdateRatingAsync(gameId, rating, cancellationToken)
            ?? throw new McpException($"Game with ID {gameId} not found."));
    }

    private string Serialize(object value) => JsonSerializer.Serialize(value, json.Value.JsonSerializerOptions);

    private static string[]? TagFilter(string? value) => string.IsNullOrWhiteSpace(value) ? null : [value];

    private static T[]? Filter<T>(string? value) where T : struct, Enum =>
        string.IsNullOrWhiteSpace(value) ? null : [Parse<T>(value)];

    private static T Parse<T>(string value) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: true, out var result) && Enum.IsDefined(result)
            ? result : throw new McpException($"Invalid {typeof(T).Name} value.");
}
