# Session History

## Current Session Summary (2026-06-20)

### What Changed

**Tenant Platform Credential Vault:**
- Ingest owns tenant-scoped encrypted platform credentials with a unique `(TenantId, Platform)`
  boundary.
- AES-256-GCM uses a fresh 96-bit nonce, full 128-bit tag, tenant/platform-bound associated data,
  and versioned external keys for rotation.
- Internal service-key-only endpoints provide secret-free status, encrypted upsert, and deletion.
- SQLite/PostgreSQL-compatible migration and encryption, spoofing, validation, and cross-tenant
  isolation tests were added.

### Known State / Pending

- Fetchers do not consume stored credentials yet.
- Core/frontend credential management remains pending.
- Provisioned tenants remain `PlatformSyncEnabled = false` until those integrations are complete.

## Last Session Summary (2026-05-25)

### What Changed

**Global Navigation Live Search:**
- **New Component**: `frontend/components/GlobalSearchBar.tsx` — SLEEK glassmorphic live autocomplete input that queries the unified game library debounced by 300ms. Shows cover art, rating, release year, and colored platform badges.
- **Navbar Integration**: Placed in the top-middle for desktop view (`md:`), taking up centered space.
- **Mobile Responsive Dialog**: Collapses into a touch-friendly search icon button next to the theme toggle. Tapping it opens a clean, modern full-screen `Dialog` to perform live search.
- **Keyboard Power-User Navigation**: Supported global keyboard hotkeys (`/` and `⌘K` / `Ctrl+K`) to focus, arrow keys (`ArrowUp`/`ArrowDown`) to select, and `Enter` to navigate to detail pages or full search page.
- **Always Present**: Search bar remains visible on all pages (landing page `/`, library list `/library`, stats `/stats`, admin dashboard `/admin`, and game details page `/library/[id]`).

### Known State / Pending
- Fully compiled and typechecked locally on host Mac architecture with zero errors.

---

## Previous Session Summary (2026-05-02)

### What Changed

**MCP Server (#11 — ✅ Complete):**
- **New project**: `services/mcp/GameHub.Mcp.csproj` — stdio MCP server console app using `ModelContextProtocol` v1.2.0.
- **CoreApiClient**: Typed HTTP client proxying calls to the Core REST API (`http://localhost:5001`).
- **LibraryTools**: 7 MCP tools with rich descriptions for AI model understanding:
  - `search_library` — paginated, filterable search
  - `get_game` — full game details by ID
  - `get_random_game` — random pick with optional filters
  - `get_statistics` — comprehensive library stats
  - `list_tags` — genre tag list
  - `set_play_status` — update play status (with validation)
  - `set_rating` — set personal star rating (1–5)
- **`.vscode/mcp.json`**: Auto-detected VS Code MCP config for GitHub Copilot.
- **`docs/mcp.md`**: Client setup guide for VS Code, Claude Desktop, and Cursor.
- **Roadmap**: Marked #11 as ✅.

### Known State / Pending
- Build pending (devcontainer was offline during session — verify with `dotnet build services/mcp/GameHub.Mcp.csproj`).
- No DB migrations needed (MCP proxies Core, no direct DB access).
- Next steps: User Accounts (#8) or Data Portability (#12).

---

## Previous Session Summary (2026-03-31)

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

---



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
