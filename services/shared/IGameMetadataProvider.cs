namespace GameHub.Shared;

/// <summary>
/// Abstraction for any game metadata provider (IGDB, RAWG, OpenCritic, etc.).
///
/// The EnrichmentService depends only on this interface, never on a concrete
/// provider. To swap providers, implement this interface and change the DI
/// registration in Program.cs — no other code changes needed.
///
/// See ADR-003 for the design rationale.
/// </summary>
public interface IGameMetadataProvider
{
    /// <summary>
    /// Searches for a game by title and returns the best match, or null if
    /// no reasonable match is found.
    /// </summary>
    Task<GameMetadata?> SearchAsync(string title, CancellationToken ct = default);

    /// <summary>
    /// Fetches metadata by the provider-specific ID. Used for re-enrichment
    /// of games that already have a MetadataProviderId stored.
    /// </summary>
    Task<GameMetadata?> GetByIdAsync(int providerId, CancellationToken ct = default);

    /// <summary>
    /// Human-readable provider name for logging and the MetadataSource field.
    /// Example: "IGDB", "RAWG", "OpenCritic".
    /// </summary>
    string ProviderName { get; }
}
