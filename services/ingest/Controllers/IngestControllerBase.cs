using GameHub.Shared;
using GameHub.Ingest.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Linq.Expressions;
using System.Text.Json;

namespace GameHub.Ingest.Controllers;

/// <summary>
/// Generic base controller that encapsulates the shared ingest pattern used by
/// all four platform controllers (Steam, PSN, GOG, Epic).
///
/// Each platform controller inherits from this class and only needs to provide:
///   1. How to extract an ID + name from a raw JSON payload (platform-specific JSON keys)
///   2. How to create a new entity from those extracted values
///   3. How to update an existing entity with new data
///   4. How to query by external ID (the DB column differs per platform)
///
/// This eliminates ~400 lines of duplicated code across the four controllers.
///
/// Type parameter TEntity must be a class that implements IRawItem,
/// which all four raw item entities (SteamRawItem, PsnRawItem, etc.) do.
/// </summary>
public abstract class IngestControllerBase<TEntity> : ControllerBase where TEntity : class, IRawItem
{
    /// <summary>The shared EF Core database context for all ingest operations.</summary>
    protected readonly IngestContext Db;

    /// <summary>Logger instance for tracking ingest operations.</summary>
    protected readonly ILogger Logger;

    /// <summary>Display name for error messages (e.g. "Steam", "GOG", "PSN", "Epic").</summary>
    protected abstract string PlatformName { get; }

    /// <summary>The EF Core DbSet for this platform's entity type (e.g. Db.SteamRawItems).</summary>
    protected abstract DbSet<TEntity> DbSet { get; }

    protected IngestControllerBase(IngestContext db, ILogger logger)
    {
        Db = db;
        Logger = logger;
    }

    // -------------------------------------------------------------------------
    // Abstract members — each platform subclass must implement these
    // -------------------------------------------------------------------------

    /// <summary>
    /// Extract the platform-specific external ID and display name from a raw JSON element.
    /// For example, Steam looks for "appid" and "name", while Epic looks for "app_name" and "app_title".
    /// Returns (null, null) if the JSON doesn't contain a valid ID — the record will be skipped.
    /// </summary>
    protected abstract (string? Id, string? Name) ExtractIdAndName(JsonElement json);

    /// <summary>
    /// Create a new entity instance from the extracted values.
    /// The subclass maps the generic externalId/name back to its specific fields
    /// (e.g. SteamRawItem.AppId = int.Parse(externalId)).
    /// </summary>
    protected abstract TEntity CreateEntity(string externalId, string? name, string rawJson);

    /// <summary>
    /// Update an existing entity with fresh data from a re-import.
    /// Typically updates RawJson, the name (if provided), and LastUpdated timestamp.
    /// </summary>
    protected abstract void UpdateEntity(TEntity existing, string? name, string rawJson);

    /// <summary>
    /// Build an Expression predicate to find an entity by its external ID.
    /// For example, Steam returns: x => x.AppId == int.Parse(id)
    /// This is used by EF Core to generate the correct SQL WHERE clause.
    /// </summary>
    protected abstract Expression<Func<TEntity, bool>> FindByExternalId(string id);

    // -------------------------------------------------------------------------
    // Shared action methods — these handle HTTP requests identically for all platforms
    // -------------------------------------------------------------------------

    /// <summary>
    /// POST: api/ingest/{platform}
    /// Receives a batch of raw game JSON objects from the CLI and upserts them into the database.
    ///
    /// For each JSON element in the batch:
    ///   1. Extract the external ID and name using the platform-specific logic
    ///   2. Check if a record with that ID already exists
    ///   3. If yes → update it (idempotent). If no → insert a new record.
    ///   4. Save all changes in one batch at the end.
    ///
    /// Returns a count of added and updated records.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> IngestBatch([FromBody] List<JsonElement> rawGames)
    {
        Logger.LogInformation("{Platform}: starting batch ingest of {Count} items", PlatformName, rawGames.Count);

        int added = 0;
        int updated = 0;
        int skipped = 0;

        foreach (var gameJson in rawGames)
        {
            // Step 1: Let the subclass extract the ID and name from platform-specific JSON keys
            var (id, name) = ExtractIdAndName(gameJson);
            if (string.IsNullOrEmpty(id))
            {
                skipped++;
                Logger.LogDebug("{Platform}: skipped item — no valid ID found", PlatformName);
                continue;
            }

            // Step 2: Check if this game is already in the database (idempotency check)
            var existing = await DbSet.FirstOrDefaultAsync(FindByExternalId(id));
            string jsonString = gameJson.ToString();

            if (existing != null)
            {
                // Step 3a: Record exists → update it with fresh data
                UpdateEntity(existing, name, jsonString);
                updated++;
            }
            else
            {
                // Step 3b: New record → create and add it
                DbSet.Add(CreateEntity(id, name, jsonString));
                added++;
                Logger.LogDebug("{Platform}: new game '{Name}' (ID: {Id})", PlatformName, name, id);
            }
        }

        // Step 4: Persist all changes in a single transaction
        await Db.SaveChangesAsync();

        Logger.LogInformation("{Platform}: batch complete — {Added} added, {Updated} updated, {Skipped} skipped",
            PlatformName, added, updated, skipped);

        return Ok(new { Added = added, Updated = updated });
    }

    /// <summary>
    /// GET: api/ingest/{platform}?page=1&pageSize=50
    /// Returns a paginated list of ingested items, excluding the large RawJson blob.
    /// Page size is capped at 100 to prevent excessive memory usage.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> ListItems([FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        // Enforce safe bounds
        if (pageSize > 100) pageSize = 100;
        if (page < 1) page = 1;

        var query = DbSet.AsNoTracking(); // Read-only — no change tracking needed
        var totalCount = await query.CountAsync();

        // Project to a lightweight shape (no RawJson) for the list view
        var items = await query
            .OrderByDescending(x => x.LastUpdated)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new
            {
                x.Id,
                ExternalId = x.ExternalId,
                Name = x.DisplayName,
                x.LastUpdated
            })
            .ToListAsync();

        return Ok(new
        {
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            Items = items
        });
    }

    /// <summary>
    /// GET: api/ingest/{platform}/{externalId}
    /// Returns the full detail for a single item, including the parsed RawJson.
    /// The stored JSON string is deserialized back to an object so the API
    /// response is clean nested JSON rather than a stringified string.
    /// </summary>
    [HttpGet("{externalId}")]
    public async Task<IActionResult> GetItem(string externalId)
    {
        var item = await DbSet
            .AsNoTracking()
            .FirstOrDefaultAsync(FindByExternalId(externalId));

        if (item == null)
            return NotFound(new { Message = $"{PlatformName} game with ID {externalId} not found" });

        // Parse the stored JSON string back into an object for a clean API response
        object? parsedMetadata = null;
        try
        {
            if (!string.IsNullOrEmpty(item.RawJson))
                parsedMetadata = JsonSerializer.Deserialize<object>(item.RawJson);
        }
        catch
        {
            parsedMetadata = new { Error = "Invalid JSON stored" };
        }

        return Ok(new
        {
            item.Id,
            ExternalId = item.ExternalId,
            Name = item.DisplayName,
            item.LastUpdated,
            RawData = parsedMetadata
        });
    }
}
