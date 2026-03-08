namespace GameHub.Shared;

/// <summary>
/// Provider-agnostic metadata DTO — the contract between any metadata provider
/// and the EnrichmentService. Providers map their API responses to this record.
///
/// Design note: This is intentionally a "lowest common denominator" across
/// providers (IGDB, RAWG, OpenCritic, etc.). Provider-specific fields
/// (e.g. IGDB hype count) are not exposed here.
/// </summary>
public record GameMetadata(
    /// <summary>Provider-specific unique ID (e.g. IGDB game ID).</summary>
    int ProviderId,

    /// <summary>URL to the game's cover art image.</summary>
    string? CoverUrl,

    /// <summary>Short text description / summary of the game.</summary>
    string? Summary,

    /// <summary>Genre names, e.g. ["RPG", "Action", "Adventure"].</summary>
    List<string> Genres,

    /// <summary>Original release date of the game.</summary>
    DateTime? FirstReleaseDate,

    /// <summary>Critic/aggregate rating normalized to 0–100 scale.</summary>
    double? Rating,

    /// <summary>The actual title returned by the provider (used for audit logs).</summary>
    string? MatchedTitle = null,

    /// <summary>Original platforms the game released on.</summary>
    List<string>? Platforms = null,

    /// <summary>High resolution background artworks / headers.</summary>
    List<string>? Artworks = null,

    /// <summary>In-game screenshots.</summary>
    List<string>? Screenshots = null,

    /// <summary>Developer companies involved.</summary>
    List<string>? Developers = null,

    /// <summary>Publisher companies involved.</summary>
    List<string>? Publishers = null
);
