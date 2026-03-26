# Session History

## Last Session Summary (2026-03-26)

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
