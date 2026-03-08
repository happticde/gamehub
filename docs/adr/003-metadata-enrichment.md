# ADR-003: Provider-Agnostic Metadata Enrichment

**Date:** 2026-03-08
**Status:** Proposed
**Deciders:** @jankahnt

## Context

`UnifiedGame` currently stores only a title, slug, and platform links. To make the library useful beyond a list of names — think cover art, genres, descriptions, release dates, ratings — we need to enrich games with metadata from an external source.

Several providers exist (IGDB, RAWG, OpenCritic, Metacritic), each with different APIs, coverage, and reliability. Metacritic has no official public API. RAWG has had intermittent key-issuance issues. The chosen provider may need to change over time.

## Options Considered

### Option A: Direct IGDB Integration

Hardwire `EnrichmentService` directly to IGDB's API. Fastest to implement.

| Aspect | Detail |
|--------|--------|
| **Complexity** | Low — single HTTP client |
| **Flexibility** | None — swapping providers requires rewriting enrichment logic |
| **Testability** | Harder — must mock concrete IGDB client |
| **Best for** | Throwaway prototypes |

### Option B: Provider Interface Abstraction

Define an `IGameMetadataProvider` interface with a provider-agnostic `GameMetadata` DTO. `EnrichmentService` depends only on the interface. IGDB is the first implementation; others can be added by implementing the same interface.

| Aspect | Detail |
|--------|--------|
| **Complexity** | Moderate — one interface, one DTO, one implementation |
| **Flexibility** | High — swap providers via one DI registration change |
| **Testability** | Easy — inject a mock `IGameMetadataProvider` |
| **Best for** | Long-lived projects where the data source may change |

### Option C: Multi-Provider Aggregation

Query multiple providers simultaneously and merge results (e.g. IGDB for cover art, OpenCritic for scores).

| Aspect | Detail |
|--------|--------|
| **Complexity** | High — conflict resolution, rate limiting across providers |
| **Flexibility** | Maximum — best-of-breed per field |
| **Testability** | Complex — many combinations to test |
| **Best for** | Large-scale applications with dedicated data teams |

## Decision

**Option B: Provider Interface Abstraction**, with IGDB as the initial implementation.

### Rationale

- **Provider independence** — the data model and enrichment logic stay the same regardless of source. Swapping IGDB for RAWG or OpenCritic means implementing one interface and changing one DI line.
- **Testability** — `EnrichmentService` tests use a mock provider, no HTTP mocking needed.
- **Auditability** — `MetadataSource` and `MetadataProviderId` fields on `UnifiedGame` record where each game's data came from.
- **Low overhead** — only one interface and one record type more than Option A.
- **Upgrade path** — if Option C is ever needed, the interface is already in place; just register multiple providers behind a composite.

### Key Design Elements

#### Provider Contract (in Shared project)

```csharp
public record GameMetadata(
    int    ProviderId,         // Provider-specific ID
    string? CoverUrl,
    string? Summary,
    List<string> Genres,
    DateTime? FirstReleaseDate,
    double? Rating             // Normalized 0-100
);

public interface IGameMetadataProvider
{
    Task<GameMetadata?> SearchAsync(string title, CancellationToken ct = default);
    Task<GameMetadata?> GetByIdAsync(int providerId, CancellationToken ct = default);
    string ProviderName { get; }
}
```

#### Data Model Fields (on `UnifiedGame`)

| Field | Type | Purpose |
|-------|------|---------|
| `CoverUrl` | `string?` | Cover art URL |
| `Summary` | `string?` | Game description |
| `Genres` | `string?` | Comma-separated genre names |
| `FirstReleaseDate` | `DateTime?` | Original release date |
| `Rating` | `double?` | Critic rating (0–100) |
| `MetadataProviderId` | `int?` | Provider-specific ID for re-enrichment |
| `MetadataSource` | `string?` | Provider name (e.g. `"IGDB"`) |
| `EnrichedAt` | `DateTime?` | When enrichment last ran (`null` = never) |

#### DI Registration

```csharp
// Program.cs — swap this line to change providers
builder.Services.AddHttpClient<IgdbClient>();
builder.Services.AddScoped<IGameMetadataProvider, IgdbClient>();
```

## Consequences

### Positive
- Enrichment logic is fully decoupled from any specific API
- Adding a new provider is a single-class change
- `MetadataSource` field makes it clear where data originated
- Mock-friendly for unit tests

### Negative
- Slight indirection compared to direct integration (one interface layer)
- `GameMetadata` DTO is a lowest-common-denominator — provider-specific fields (e.g. IGDB's `hypes` count) are not exposed
- If a future provider uses a non-integer ID, `MetadataProviderId` would need to change to `string`
