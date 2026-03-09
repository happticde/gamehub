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
| CI/CD | ❌ Not started |
| Frontend | ✅ Next.js library UI, filters, sorting, game detail pages |
| Enrichment / metadata | ✅ IGDB provider with caching, rate-limiting, smart search |

---

## 🏗️ Tier 1 — Foundational

### 1. Unit & Integration Tests 🟡

xUnit test suite with in-memory SQLite covering:
- **Slug generation** — `GenerateSlug()` / `NormalizeTitle()` with cross-platform matching verification
- **Unification pipeline** — game creation, slug merging, manual link overrides, sync metadata
- **Library controller** — merge/split operations and edge cases
- **Ingest controller** — batch upsert, idempotency, pagination
- **Export controller** — paginated export with incremental sync filtering

**Tech:** xUnit, `Microsoft.EntityFrameworkCore.Sqlite` (in-memory), `InternalsVisibleTo`

### 2. EF Core Migrations

Replace `EnsureCreated()` with proper migrations so schema changes don't require deleting the database.

- `dotnet ef migrations add InitialCreate` for both Ingest and Core
- Replace `EnsureCreated()` with `db.Database.Migrate()` in both `Program.cs` files

### 3. API Key / Basic Auth

Simple API key middleware (`X-Api-Key` header) to protect ingest and sync endpoints. The `RetrieveController` accepts raw credentials, so securing it is important.

---

## 🎮 Tier 2 — Feature Enrichment

### 4. Web Frontend (Dashboard)

A browsable UI for the unified library:
- Cover art grid, search/filter by platform/genre/developer/publisher, game detail pages ✅
- Sort by title/rating/releasedate ✅
- Admin actions: merge/split UI, trigger sync, view sync history
- Current Implementation: Next.js App Router
- **Remaining**: Stats dashboard (#10), background sync scheduler (#6)

### 6. Background Sync Scheduler

`IHostedService` or Quartz.NET job that runs `RunFullSyncAsync()` on a configurable interval (such as once every 6 hours) so the library is always perfectly up to date in the background without manual clicks. Add a `GET /api/library/sync/status` endpoint for sync status.

---

## 🔧 Tier 3 — Polish & Operations

### 7. CI/CD Pipeline

GitHub Actions: build → test → Docker build → push to registry. Add build status badge to README.

### 8. User Accounts & Multi-Tenancy

Per-user game libraries with encrypted credential storage. Auth via JWT or OAuth (Steam/PSN/GOG login).

### 9. Notifications & Activity Feed

`ActivityLog` table + `GET /api/activity` endpoint. Optional webhook support for Discord/Slack.

### 10. Statistics & Analytics

`GET /api/library/stats` — total games, per-platform counts, duplicate %, recently added. Platform overlap data for Venn diagrams. Map this to a new dynamic Next.js dashboard/charting page.

### 11. MCP Server (Model Context Protocol)

AI agent integration layer — expose library queries, sync triggers, and merge/split as MCP tools. Lets AI assistants interact with the game library via natural language (e.g. *"What RPGs do I own on both Steam and GOG?"*). Best value after metadata enrichment (#4).

Tools: `list_games`, `search_games`, `sync_library`, `fetch_platform`, `merge_games`, `library_stats`

### 12. Export / Collection Sharing API

Expose a public, read-only API endpoint to export your curated library collection, allowing users to show off or share their unified library with friends.

---

## Suggested Next Steps

| Goal | Path |
|------|------|
| **Stability & confidence** | ~~#1 Tests → #2 Migrations → #3 Auth~~ ✅ Done |
| **A demo-able product** | ~~#4 Metadata~~  ✅ Done → #5 Frontend → #10 Stats |
| **Production readiness** | #7 CI/CD → #6 Scheduler |
| **AI integration** | ~~#4 Metadata~~ ✅ Done → #11 MCP Server |
| **Fun features** | ~~#4 Metadata~~ ✅ Done → #9 Activity Feed → #10 Stats |
