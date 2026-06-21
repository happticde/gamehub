# GameHub Backend — Roadmap

Current status and future direction for the project. Ideas are grouped from **high-impact / foundational** to **nice-to-have**.

---

## Current Status

| Area | Status |
|------|--------|
| Ingest service (Steam, GOG, PSN, Epic) | ✅ Complete |
| Core unification (slug matching, manual merge/split) | ✅ Complete |
| Go CLI (Cobra/Viper, config file) | ✅ Complete |
| Docker Compose (SQLite dev, Postgres prod) | ✅ With health checks |
| Tests | ✅ xUnit + in-memory SQLite |
| EF Core Migrations | ✅ Auto-applied on startup |
| Structured Logging | ✅ All components, debugging guide |
| Auth | ✅ API key middleware (JWT-ready) |
| CI/CD | ✅ GitHub Actions (build, test, Docker) |
| Frontend | ✅ Mobile-First: Grid/List, Filters Dialog, Screenshot Carousel |
| Enrichment / metadata | ✅ IGDB provider with caching, rate-limiting, smart search |
| Statistics / Analytics | ✅ Complete board with charts and histograms |
| Sync / Ignore logic | ✅ Auto-hide keywords + retroactive ignore |
| Background Sync Scheduler | ✅ BackgroundService + status endpoint + admin UI |

---

## 🏗️ Tier 1 — Foundational

### 1. Unit & Integration Tests ✅
xUnit test suite with in-memory SQLite covering slug generation, unification, and controllers.

### 2. EF Core Migrations ✅
Replaced `EnsureCreated()` with proper migrations for schema stability.

### 3. API Key / Basic Auth ✅
Simple API key middleware protecting ingest and sync endpoints.

---

## 🎮 Tier 2 — Feature Enrichment

### 4. Web Frontend (Dashboard) ✅
A browsable UI for the unified library with cover grid, search/filter, game detail pages, and admin actions.
**Enhanced (2026-03-26)**: Fully mobile-optimized with responsive filtering dialogs and touch-native screenshot carousels.

### 5. Personal Tracking & Ignore ✅
Added 5-star ratings, Play Status tracking, and "Auto-Hide & Ignore" keyword logic for junk entries.

### 6. Background Sync Scheduler ✅
`SyncSchedulerService` (`BackgroundService`) runs `RunFullSyncAsync()` on a configurable interval (default 6h). `GET /api/library/sync/status` endpoint exposes scheduler state. Admin UI card with live status and auto-refresh. Disabled by default via `Sync:EnableScheduler`.

---

## 🔧 Tier 3 — Polish & Operations

### 7. CI/CD Pipeline ✅
GitHub Actions: build → test → Docker build → push to registry.

### 8. User Accounts & Multi-Tenancy
Per-user game libraries with encrypted credential storage. Auth via JWT or OAuth (Steam/PSN/GOG login).
- **Foundation (2026-06-21)**: Default-tenant migrations, claim-based tenant contexts, tenant-scoped Core and Ingest storage/queries/uniqueness constraints, tenant-isolated background jobs, local account bootstrap, optional JWT token issuance, rotating/revocable refresh sessions, API-key-controlled tenant provisioning, a frontend HttpOnly-cookie login/BFF flow, service-key-authenticated Core→Ingest tenant delegation, an AES-256-GCM tenant platform credential vault, vault-backed Steam/GOG/PSN retrieval, authenticated Core/frontend credential management, and a fail-closed credential-derived sync activation policy are implemented. The current Steam/GOG/PSN credential-management scope is complete; multi-user operation is not enabled by default.
- **Epic boundary**: Epic remains a fully manual Ingest flow through API or CLI. No Epic credentials
  are stored and no automatic Epic stored retrieval runs. Core continues to synchronize manually
  ingested Epic raw data. Future Epic authentication and stored retrieval are independently tracked
  in item #16 and are not pending work for the current credential-management scope.
- **Isolation verification (2026-06-21)**: Tenant-scoped backup export and restore, the active-tenant
  empty-library restore rule, equivalent cross-tenant slugs/platform IDs, and Library mutation
  boundaries for personal tracking, merge, and split have direct cross-tenant test coverage.
- **Remaining isolation gate**: Complete the audit of user-owned paths not yet explicitly verified
  above and close any gaps before multi-user enablement, especially sync, tags, manual links,
  background jobs, and uniqueness constraints.
- **Remaining account work**: Define the supported multi-user account lifecycle and complete the
  chosen production authentication flow. OAuth remains an account/authentication option; Epic
  integration is outside this item.
- **Remaining rollout work**: Add an explicit, guarded multi-user enablement path only after the
  isolation, portability, verification, and account-lifecycle gates are complete.

### 9. Notifications & Activity Feed
`ActivityLog` table + activity endpoint for social sharing/feed.

### 10. Statistics & Analytics ✅
Comprehensive dashboard with charts for platform distribution, genre breakdown, rating histograms, and play status tracking.

### 11. MCP Server (Model Context Protocol) ✅
AI agent integration layer — let assistants interact with the library via natural language.
See [docs/mcp.md](mcp.md) for client setup (VS Code, Claude Desktop, Cursor).

---

## 🚀 Tier 4 — Advanced Features & Portability

### 12. Data Portability (Backup & Restore) ✅
- **Feature**: GameHub-specific import/export via JSON/CSV.
- **Goal**: Full manual backup of entire unified library (metadata, ratings, play status).
- **Current scope**: JSON V1 export, validation, transactional empty-library restore, Admin UI, and spreadsheet-safe CSV export are implemented for the single-user deployment model.
- **Multi-tenancy status**: Export and restore are scoped to the authenticated tenant while the V1
  document remains tenant-neutral. Cross-tenant isolation and active-tenant empty-library behavior
  are covered by tests. See [ADR-008](adr/008-data-portability-format.md).

### 13. Physical Collection (Barcode Scanning)
- **Scanner**: Web-based barcode scanner (MediaDevices API) for physical game cases.
- **Platforms**: Support for PS3, PS4, and PS5 as distinct physical platforms.
- **Lookup**: Resolve barcodes to titles and automatically enrich via IGDB.

### 14. Discovery: "Surprise Me" (Random Pick) ✅
- **Feature**: Random game selection button that respects active filters (e.g., "Random RPG I haven't finished").
- **Goal**: Help users conquer their backlog by removing decision paralysis.
- **Implemented**: The Core random-library endpoint respects active filters, and the frontend
  provides a "Surprise Me" action that navigates to the selected game.

### 15. Power-User Maintenance Tools
- **Direct Enrichment**: Allow manual input of IGDB ID for instant, accurate matching.
- **Advanced Pagination**: Customizable page sizes, FIRST/LAST navigation, and direct page jumping.
- **Manual Split**: Ability to separate platform entries from a unified game into their own records.
- **Robust Sorting**: Logic to gracefully handle missing years or ratings (coalesce nulls).

### 16. Epic Authentication & Stored Retrieval
- **Current behavior**: Epic ingestion is manual through API or CLI; Core synchronizes the resulting
  raw data alongside other platforms.
- **Future scope**: Evaluate an Epic authentication mechanism, secure credential or token handling,
  and tenant-scoped stored retrieval as a standalone integration.
- **Boundary**: This work is not a missing part of the Steam/GOG/PSN credential-management feature
  and does not block that feature from being considered complete.

---

## Suggested Next Steps

| Goal | Path |
|------|------|
| **Stability & confidence** | ~~#1 Tests → #2 Migrations → #3 Auth~~ ✅ Done |
| **A demo-able product** | ~~#4 Metadata~~ ✅ Done → #5 Frontend → ~~#10 Stats~~ ✅ Done |
| **Production readiness** | ~~#7 CI/CD~~ ✅ → ~~#6 Scheduler~~ ✅ Done |
| **Multi-user readiness** | #8 remaining isolation audit → account lifecycle and production auth → guarded enablement |
| **AI integration** | #11 MCP Server (High Value) |
| **Data & Portability** | #12 Import/Export → #13 Barcode Scanning |
| **Maintenance & UI** | #15 Power-User Tools → ~~#14 Random Pick~~ ✅ Done |
| **Future platform integration** | #16 Epic authentication and stored retrieval |
