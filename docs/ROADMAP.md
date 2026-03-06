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
| Auth | ❌ Not started |
| CI/CD | ❌ Not started |
| Frontend | ❌ API-only |
| Enrichment / metadata | ❌ Only titles + platform IDs |

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

### 4. Game Metadata Enrichment (IGDB / RAWG)

Extend `UnifiedGame` with cover art, genres, descriptions, release dates, and ratings. An `EnrichmentService` would look up games by slug/title after sync. Could run as a background `IHostedService` to respect rate limits.

### 5. Web Frontend (Dashboard)

A browsable UI for the unified library:
- Cover art grid, search/filter by platform/genre, game detail pages
- Admin actions: merge/split UI, trigger sync, view sync history
- Options: Vite SPA, Next.js, or Blazor

### 6. Background Sync Scheduler

`IHostedService` or Quartz.NET job that runs `RunFullSyncAsync()` on a configurable interval. Add a `GET /api/library/sync/status` endpoint for sync status.

---

## 🔧 Tier 3 — Polish & Operations

### 7. CI/CD Pipeline

GitHub Actions: build → test → Docker build → push to registry. Add build status badge to README.

### 8. User Accounts & Multi-Tenancy

Per-user game libraries with encrypted credential storage. Auth via JWT or OAuth (Steam/PSN/GOG login).

### 9. Notifications & Activity Feed

`ActivityLog` table + `GET /api/activity` endpoint. Optional webhook support for Discord/Slack.

### 10. Statistics & Analytics

`GET /api/library/stats` — total games, per-platform counts, duplicate %, recently added. Platform overlap data for Venn diagrams.

---

## Suggested Next Steps

| Goal | Path |
|------|------|
| **Stability & confidence** | #1 Tests → #2 Migrations → #3 Auth |
| **A demo-able product** | #4 Metadata → #5 Frontend → #10 Stats |
| **Production readiness** | #1 Tests → #7 CI/CD → #3 Auth → #6 Scheduler |
| **Fun features** | #4 Metadata → #9 Activity Feed → #10 Stats |
