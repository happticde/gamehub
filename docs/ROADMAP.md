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
| Frontend | ✅ Grid/List Views, filters, sorting, game detail pages |
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
A browsable UI for the unified library with cover grid, search/filter, game detail pages, and admin actions (merge/split UI, sync history).

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

### 9. Notifications & Activity Feed
`ActivityLog` table + activity endpoint for social sharing/feed.

### 10. Statistics & Analytics ✅
Comprehensive dashboard with charts for platform distribution, genre breakdown, rating histograms, and play status tracking.

### 11. MCP Server (Model Context Protocol) 🚀
AI agent integration layer — let assistants interact with the library via natural language.

---

## Suggested Next Steps

| Goal | Path |
|------|------|
| **Stability & confidence** | ~~#1 Tests → #2 Migrations → #3 Auth~~ ✅ Done |
| **A demo-able product** | ~~#4 Metadata~~ ✅ Done → #5 Frontend → ~~#10 Stats~~ ✅ Done |
| **Production readiness** | ~~#7 CI/CD~~ ✅ → ~~#6 Scheduler~~ ✅ Done |
| **AI integration** | #11 MCP Server (High Value) |
| **Fun features** | #9 Activity Feed |
