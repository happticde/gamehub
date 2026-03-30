# Session History

## Upcoming Objectives (Sprint 1 & 2)

> **Context for AI Agents**: The features below have been added to the roadmap and prioritized. Refer to these technical notes instead of browsing the conversation history to save tokens.

### 1. Manual Split (Deferred from Power-User Tools)
- **Backend API**: Create `POST /api/library/split` accepting `SourceGameId`, `Platform`, and `ExternalId`. Moves the `LibraryItem` to a newly generated `UnifiedGame` and creates a `ManualLink`.
- **Frontend**: In the game detail page, add a "Split" button next to each platform pill in the Admin Actions section.

### 2. Data Portability (Phase 1: Export)
- **Backend API**: Create `ExportController.cs`. Add `GET /api/export/library` returning a full JSON dump of the DB (`UnifiedGames` + `LibraryItems` + `Tags`). Keep it simple, exclude audit logs.
- **Frontend**: Add an "Export / Backup" card to the Admin dashboard invoking the endpoint and triggering a file download.

### 3. Physical Collection & Importer (Phase 2 & 3)
- **Blocked**: Barcode scanning requires a research spike on a reliable `Barcode -> Game Title` API (as IGDB lacks native EAN support) and testing of browser camera permissions on `http://192.x` contexts.

---

## Last Session Summary (2026-03-31)

### What Changed

**"Surprise Me" (Random Pick):**
- **Backend**: Added `GET /api/library/random` to `LibraryController.cs`. Reused `BuildLibraryQuery` to ensure filters are respected.
- **Frontend**: Added a "Surprise Me" (Shuffle) button to `LibraryFilters.tsx` that fetches a random game ID and redirects the user.

**Power-User Maintenance Tools:**
- **Robust Sorting**: Updated `LibraryController.cs` to coalesce null ratings/dates to the bottom of the list.
- **Direct IGDB Match**: Updated `IgdbClient.cs` to support direct ID queries when a numeric string is entered in the "Fix Match" dialog.
- **Advanced Navigation**: 
    - Added "First" and "Last" page jumps to `LibraryPagination.tsx`.
    - Added a direct page selection dropdown (HTML `<select>`) for instant jumping.
    - Implemented a **floating bubble** pagination UI (sticky, compact pill) at the bottom-center of the library view.

### Known State / Pending
- Manual Split was deferred to its own task.
- Build verified (Backend 0 errors, Frontend TypeChecks pass).

## Previous Session Summary (2026-03-26)

### What Changed

**Frontend & Mobile UX:**
- **Mobile-First Refactor**: Implemented `LibraryFilters` as a responsive `Dialog` on mobile, freeing up screen space for game covers.
- **Screenshot Carousel**: Replaced static vertical screenshot lists with a touch-native horizontal carousel (`ScreenshotCarousel.tsx`) using CSS scroll-snap.
- **Admin Dashboard**: Enabled tab wrapping for the statistics and management dashboard on narrow screens.
- **iOS Viewport Fix**: Added `userScalable: false` to the global layout to prevent unwanted browser zooming on mobile inputs.
- **Build Recovery**: Rescued the frontend build after a PWA-related dependency corruption by performing a clean `npm ci` natively inside the Linux devcontainer.

**Backend & Integration:**
- **Manual Match Override**: Finalized integration of the "Fix Match" UI with the `SearchManyAsync` provider logic, allowing users to manually correct IGDB mismatches.

### Known State / Pending
- Mobile UI is now fully responsive and tested with a production build.
- PWA implementation was deferred/reverted to prioritize build stability and mobile layout quality.
- Next steps: Implement User Authentication (#8) or the MCP Server integration (#11).

## Previous Session Summary (2026-03-24)

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
