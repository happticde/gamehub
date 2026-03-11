# GameHub Backend

![CI](https://github.com/happticde/game-library-backend/actions/workflows/ci.yml/badge.svg)

A unified game library aggregator that ingests data from **Steam**, **PSN**, **GOG**, and **Epic** into a single queryable library. It merges games across platforms using intelligent title matching, enriches them with IGDB metadata, and provides a modern web interface along with REST APIs.

## Architecture

```
┌──────────────┐     ┌──────────────┐     ┌──────────────┐
│   Ingest     │◄────│    Core      │◄────│   Frontend   │
│  :5000       │     │   :5001      │     │   Next.js    │
│              │     │              │     │   :3000      │
│ • Raw game   │     │ • Unified    │     │ • Library UI │
│   storage    │     │   library    │     │ • Admin      │
│ • Platform   │     │ • Slug-based │     │   Dashboard  │
│   fetchers   │     │   matching   │     │              │
│ • Export API │     │ • Enrichment │     └──────────────┘
└──────────────┘     └──────────────┘
       ▲
       │
┌──────┴────────┐
│   Go CLI      │
│ gamehub-cli   │
└───────────────┘
```

**Four components:**
- **Ingest Service** (.NET) — stores raw game data per platform, fetches directly from platform APIs
- **Core Service** (.NET) — merges data into a unified library with cross-platform matching, IGDB enrichment
- **Frontend** (Next.js) — unified library browser, game detail pages, and admin dashboard
- **CLI** (Go) — alternative client that fetches libraries and pushes to Ingest

## Features

- **Multi-platform ingestion** — Steam, GOG, PSN, Epic
- **Cross-platform deduplication** — slug-based title matching with manual merge/split overrides
- **IGDB metadata enrichment** — cover art, ratings, genres, screenshots, developers, publishers
- **Modern web UI** — filterable/searchable library grid, cinematic game detail pages
- **Admin dashboard** — enrichment controls, platform ingestion forms, sync management, audit logs
- **API key authentication** — JWT-ready, configurable via env vars

## Quick Start

### 1. Configure Secrets

```bash
cp .env.example .env
```

Open `.env` and configure your credentials:

| Variable | Purpose |
|----------|---------|
| `API_KEY` | API key for authenticating requests (empty = auth disabled) |
| `IGDB_CLIENT_ID` | Twitch Developer App Client ID (for IGDB enrichment) |
| `IGDB_CLIENT_SECRET` | Twitch Developer App Client Secret |

**To get IGDB credentials:**
1. Log in to the [Twitch Developer Console](https://dev.twitch.tv/console) (requires 2FA)
2. Register your application (OAuth redirect: `https://localhost`, Category: `Application Integration`, Client Type: `Confidential`)
3. Copy your **Client ID** and generate a **Client Secret**

### 2. Start Everything

```bash
docker compose up --build
```

| Service | URL |
|---------|-----|
| Frontend | `http://localhost:3000` |
| Ingest API | `http://localhost:5000` (Swagger at `/swagger`) |
| Core API | `http://localhost:5001/swagger` |
| Health checks | `/health` on both services |

### 3. Ingest Your Libraries

**Option A: Admin Dashboard (recommended)**

Navigate to `http://localhost:3000/admin` → **Ingestion** tab. Enter your platform credentials and click "Fetch & Ingest".

**Option B: CLI**

```bash
cd services/cli && go build -o gamehub-cli .

gamehub-cli ingest steam --key YOUR_KEY --id YOUR_STEAM_ID
gamehub-cli ingest gog --user YOUR_USERNAME --token YOUR_GOG_AL_COOKIE
gamehub-cli ingest psn --cookies '{"JSESSIONID":"...","sb-cdn.session":"..."}'
```

**Option C: Direct API Calls**

```bash
# Steam
curl -X POST http://localhost:5000/api/retrieve/steam \
  -H "Content-Type: application/json" \
  -d '{"apiKey": "YOUR_KEY", "steamId": "YOUR_STEAM_ID_64"}'

# GOG
curl -X POST http://localhost:5000/api/retrieve/gog \
  -H "Content-Type: application/json" \
  -d '{"cookie": "YOUR_GOG_AL_COOKIE", "user": "YOUR_GOG_USERNAME"}'

# PSN
curl -X POST http://localhost:5000/api/retrieve/psn \
  -H "Content-Type: application/json" \
  -d '{"cookies": {"JSESSIONID": "...", "sb-cdn.session": "..."}}'
```

### 4. Sync & Enrich

```bash
# Trigger unification sync
curl -X POST http://localhost:5001/api/library/sync

# Enrich all un-enriched games with IGDB metadata
curl -X POST http://localhost:5001/api/library/enrich
```

Or use the Admin Dashboard at `/admin` for a visual interface.

### 5. Browse Your Library

Visit `http://localhost:3000/library` to see your unified collection with filters, sorting, and game detail pages.

## Frontend Pages

| Route | Description |
|-------|-------------|
| `/` | Landing page with quick stats and feature highlights |
| `/library` | Unified library grid with search, filters (tags, platforms, developer, publisher), sorting |
| `/library/[id]` | Cinematic game detail page with metadata, screenshots, artworks |
| `/admin` | Admin dashboard with tabs for Enrichment, Ingestion, and Sync management |

## API Reference

### Ingest Service (`:5000`)

| Method | Endpoint | Description |
|--------|----------|-------------|
| `POST` | `/api/ingest/{platform}` | Batch upsert raw game data |
| `GET` | `/api/ingest/{platform}?page=1&pageSize=20` | List ingested items |
| `GET` | `/api/ingest/{platform}/{externalId}` | Get single item detail |
| `GET` | `/api/export/{platform}?page=1&pageSize=200&updatedAfter=` | Paginated export for Core sync |
| `POST` | `/api/retrieve/steam` | Fetch + ingest Steam library |
| `POST` | `/api/retrieve/gog` | Fetch + ingest GOG library |
| `POST` | `/api/retrieve/psn` | Fetch + ingest PSN library |
| `GET` | `/api/retrieve/history` | Ingest audit log |

### Core Service (`:5001`)

| Method | Endpoint | Description |
|--------|----------|-------------|
| `POST` | `/api/library/sync` | Trigger incremental sync from Ingest |
| `GET` | `/api/library?search=&tags=&platforms=&sortBy=&page=&includeIgnored=false` | Query unified library (paginated) |
| `GET` | `/api/library/{id}` | Full game detail with media arrays |
| `PATCH` | `/api/library/{id}/personal` | Update status, rating, or ignore status |
| `GET` | `/api/library/tags` | List all unique tags |
| `GET` | `/api/library/developers` | List all developers |
| `GET` | `/api/library/publishers` | List all publishers |
| `POST` | `/api/library/enrich` | Bulk enrich un-enriched games (`?force=true` to reset all) |
| `POST` | `/api/library/enrich/stale` | Reset stale enrichments (enriched but no metadata) |
| `POST` | `/api/library/enrich/{id}` | Enrich/re-enrich a single game (`?force=true`) |
| `GET` | `/api/library/enrichment-stats` | Aggregated enrichment counts for dashboard |
| `GET` | `/api/library/enrichment-history` | Enrichment audit log (`?status=Error&gameId=&limit=`) |
| `GET` | `/api/library/sync-history` | Unification sync audit log |
| `POST` | `/api/library/retroactive-ignore` | Retroactively hide junk/demo games matching keywords |
| `POST` | `/api/library/merge` | Merge two games `{ sourceGameId, targetGameId }` |
| `POST` | `/api/library/split` | Split a platform entry `{ libraryItemId }` |

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Frontend | Next.js (React), TypeScript, Tailwind CSS, shadcn/ui |
| Services | ASP.NET Core (.NET 10), C# |
| CLI | Go (Cobra + Viper) |
| Database | SQLite (dev) / PostgreSQL (prod) via EF Core |
| Auth | API key via `X-Api-Key` header (JWT-ready) |
| Docker | Alpine-based images, Docker Compose |
| Tests | xUnit with in-memory SQLite |

## Project Structure

```
frontend/                   # Next.js web application
├── app/
│   ├── page.tsx            # Landing page
│   ├── layout.tsx          # Root layout with Navbar
│   ├── admin/page.tsx      # Admin dashboard (enrichment, ingestion, sync)
│   └── library/
│       ├── page.tsx        # Library grid
│       └── [id]/page.tsx   # Game detail page
├── components/
│   ├── admin/              # EnrichmentPanel, IngestionPanel, SyncPanel
│   ├── ui/                 # shadcn primitives (button, card, tabs, etc.)
│   ├── Navbar.tsx
│   ├── GameCard.tsx
│   ├── LibraryFilters.tsx
│   └── LibraryPagination.tsx
├── types/game.ts           # TypeScript interfaces
└── lib/                    # Utilities

services/
├── ingest/                 # Raw data storage service
│   ├── Controllers/
│   │   ├── IngestControllerBase.cs   # Generic base (shared logic)
│   │   ├── Steam/Gog/Psn/EpicIngestController.cs
│   │   ├── RetrieveController.cs     # Server-side fetch + ingest
│   │   └── ExportController.cs       # Paginated export for Core
│   ├── Services/            # Platform fetchers
│   └── Data/
├── core/                   # Unified library service
│   ├── Controllers/
│   │   └── LibraryController.cs      # Library, enrichment, merge/split
│   ├── Services/
│   │   ├── UnificationService.cs     # Slug matching + manual links
│   │   ├── EnrichmentService.cs      # IGDB enrichment orchestration
│   │   ├── IgdbClient.cs             # IGDB API client with caching
│   │   └── IngestClient.cs           # HTTP client for Ingest
│   └── Data/
├── shared/                 # Shared DTOs, enums, auth handler
│   └── Authentication/     # ApiKeyAuthenticationHandler
└── cli/                    # Go CLI
    └── cmd/

tests/GameHub.Tests/        # xUnit test suite
docs/                       # ROADMAP.md, DEBUGGING.md, ADRs
```

## Notes

- **Schema changes:** Run `dotnet ef migrations add <Name> --project services/<service>/<project>.csproj` and restart. Migrations are auto-applied on startup.
- **Swagger:** Available at `/swagger` on both services.
- **Health checks:** Both services expose `GET /health` with DB connectivity checks.
- **Authentication:** Set `Authentication__ApiKey` env var. Leave empty to disable auth.
