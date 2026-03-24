# ADR-007: Manual Metadata Override

**Date:** 2026-03-24
**Status:** Proposed
**Deciders:** @jankahnt

## Context

The automatic enrichment process uses title-based searching to match games between library sources (like Steam) and metadata providers (like IGDB). This is prone to "mismatch bugs" where a short or generic title on one platform matches the wrong entry on another (e.g., Steam's "ArcaniA" matching "Arcany" instead of "Arcania: A Gothic Tale").

While search heuristics can be improved, there will always be edge cases that require human intervention to ensure data accuracy.

## Options Considered

### Option A: Direct ID Override (API-only)
Provide an API endpoint to manually update the `MetadataProviderId` and `MetadataSource` for a specific game, followed by a forced re-enrichment.

| Aspect | Detail |
|--------|--------|
| **Complexity** | Low — minor API and DB update |
| **User Experience** | Poor — user must manually find IDs on provider websites (e.g., api-docs.igdb.com) |
| **Stability** | High — no new search logic needed |

### Option B: Manual Search & Pick (UI + API)
Extend the existing `IGameMetadataProvider` interface to return multiple search results. Implement a UI dialog where users can search by custom string and select the correct match from a list.

| Aspect | Detail |
|--------|--------|
| **Complexity** | High — requires new UI components and API search endpoints |
| **User Experience** | Excellent — search and fix directly within the dashboard |
| **Stability** | Medium — depends on provider search quality |

### Option C: Manual Field Editing (Local Override)
Allow users to manually edit fields (Summary, Genres, etc.) and "lock" the game from further automatic enrichment.

| Aspect | Detail |
|--------|--------|
| **Complexity** | Moderate — requires tracking "dirty" or "locked" fields in DB |
| **User Experience** | High effort — user must copy/paste data fields manually |
| **Stability** | Maximum — bypasses provider data entirely |

## Decision

**Option B: Manual Search & Pick**, combined with **Option A's** underlying ID override logic.

### Rationale

- **Accuracy**: Humans are better than regex at identifying the correct "Arcania" among similar titles.
- **Workflow**: A "Search & Pick" UI allows the user to fix the issue in seconds without leaving the application.
- **System Integrity**: By overriding the `MetadataProviderId` and then running the standard enrichment logic, we ensure the data remains structured and up-to-date with the provider's latest info, rather than becoming static text (as in Option C).

## Implementation Plan

1. **Backend**:
   - Add `POST /library/{id}/enrichment/override` (takes `providerId`).
   - Add `GET /enrichment/search?title=...` to return the raw top 10 results from IGDB.
2. **Frontend**:
   - Add a "Fix Matching" button to the Game Detail/Admin page.
   - Open a dialog with search results and "Select" buttons.

## Consequences

### Positive
- Misidentified games can be fixed permanently.
- No need to constantly "tune" the automatic search algorithm for rare edge cases.

### Negative
- Requires additional UI development.
- The `IGameMetadataProvider` interface needs to support returning multiple results (raw DTOs).
