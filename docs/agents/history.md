# Session History

## Last Session Summary (2026-03-24)

### What Changed

**Backend (`services/core/`):**
- **IGDB Search Robustness**: `IgdbClient` now generates multiple candidate variations (specific to general) and uses them sequentially as fallbacks if the primary search fails. Base-name fallback retained.
- **Manual Metadata Override (ADR-007)**: Added `SearchManyAsync` to `IGameMetadataProvider` for retrieving raw search results from providers. Added `GET /api/library/enrichment/search` and `POST /api/library/{id}/enrichment/override` endpoints to `LibraryController` to support manual matching.

**Frontend (`frontend/`):**
- **Fix Match Dialog**: Created `FixMatchDialog.tsx` component to allow users to search IGDB from the Game Detail page, pick the correct result, and forcefully override the game's metadata.
- **UI Enhancements**: Resolved UI scrolling issues for long search result lists within `DialogContent` by enforcing flexbox constraints.

### Known State / Pending
- Manual override dialog is fully functional and overrides `MetadataProviderId`.
- Search fallbacks significantly reduce mismatches from editions or beta tags.
- Remaining roadmap: MCP Server (#11), User Accounts (#8).
