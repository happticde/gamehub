namespace GameHub.Shared;

/// <summary>
/// Generic wrapper for paginated API responses.
/// Used by ExportController (returns it) and IngestClient (deserializes it).
/// </summary>
public class PagedResult<T>
{
    /// <summary>The items on the current page.</summary>
    public List<T> Items { get; set; } = [];

    /// <summary>Total number of records matching the query across all pages.</summary>
    public int TotalCount { get; set; }

    /// <summary>Current page number (1-indexed).</summary>
    public int Page { get; set; }

    /// <summary>Number of items per page.</summary>
    public int PageSize { get; set; }

    /// <summary>True if there are more pages after this one.</summary>
    public bool HasMore => Page * PageSize < TotalCount;
}
