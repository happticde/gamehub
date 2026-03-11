# GameHub Backend — Agent Context

> This file provides structured context for AI coding assistants.
> Read this first before making changes.

## Architecture

Two ASP.NET Core microservices + Go CLI, orchestrated via Docker Compose:

```
┌──────────────┐     ┌──────────────┐     ┌──────────────┐
│   Ingest     │◄────│    Core      │◄────│   Frontend   │
│  :5000       │     │   :5001      │     │  Next.js     │
│              │     │              │     │ (React Space)│
│ • Raw game   │     │ • Unified    │     │ • User UI    │
│   storage    │     │   library    │     │ • BFF API    │
│ • Platform   │     │ • Slug-based │     │   Routes     │
│   fetchers   │     │   matching   │     │              │
│ • Export API │     │ • Merge/Split│     └──────────────┘
└──────────────┘     └──────────────┘
       ▲
       │
┌──────┴────────┐
│   Go CLI      │
│ gamehub-cli   │
└───────────────┘
```

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Frontend | Next.js (React), TypeScript, Tailwind CSS, shadcn/ui |
| Services | ASP.NET Core (.NET 10 preview), C# |
| CLI | Go (Cobra/Viper) |
| Database | SQLite (dev), Postgres (prod) via EF Core |
| ORM | Entity Framework Core with Migrations |
| Docker | Alpine-based images, Docker Compose |
| Tests | xUnit, in-memory SQLite |
| Auth | API key via `X-Api-Key` header (JWT-ready) |

### 🧠 Developer Context (Frontend)
- The user is **new to React and TypeScript**.
- When writing frontend code, **explain concepts clearly, use best practices, and break down complex React/Next.js paradigms** (e.g., Server Components vs Client Components, hooks, state management).
- Write highly readable, well-commented TypeScript code to assist in their learning journey.

## Key Directories

```
services/
  ingest/           # Ingest service (raw game data)
    Controllers/    # IngestControllerBase + 4 platform controllers, RetrieveController, ExportController
    Services/       # PlatformFetchers (Steam, GOG, PSN)
    Data/           # EF Core context + entities
  core/             # Core service (unified library)
    Controllers/    # LibraryController (sync, merge, split, enrich, enrichment-history)
    Services/       # UnificationService, IngestClient, EnrichmentService, IgdbClient
    Data/           # EF Core context + entities
  shared/           # Shared DTOs, enums, auth handler
    Authentication/ # ApiKeyAuthenticationHandler (AuthenticationHandler<T>)
  cli/cmd/          # Go CLI commands
tests/GameHub.Tests/  # xUnit test suite
docs/               # ROADMAP.md, DEBUGGING.md, adr/
```

## Design Patterns

- **IngestControllerBase<T>** — generic base with shared upsert/pagination; platform controllers only implement JSON key extraction
- **Slug-based matching** — `UnificationService.GenerateSlug()` normalizes titles for cross-platform deduplication
- **ManualLinks** — user overrides for merge/split that persist across syncs
- **AuthenticationHandler<T>** — API key auth using ASP.NET's pipeline, not raw middleware (JWT can be added as second scheme)

## Configuration

- Auth: `Authentication:ApiKey` in appsettings.json or `Authentication__ApiKey` env var. Empty = auth disabled.
- DB: `Database:Provider` (`Sqlite`/`Postgres`), connection strings in `ConnectionStrings` section
- Core→Ingest URL: `Services:IngestUrl` (default `http://localhost:5000`)

## Current Status

Stability path complete: Tests ✅, Migrations ✅, Logging ✅, Auth ✅.
Frontend: Next.js library UI with filters, sorting, game detail pages ✅.
Enrichment: IGDB metadata provider with caching, rate-limiting, smart search ✅.
Admin Dashboard: Enrichment, ingestion, sync controls in tabbed UI ✅.
Statistics page: Pure CSS charts with platform/genre/rating breakdowns ✅.
CI/CD: GitHub Actions with 3 parallel jobs (backend, frontend, Docker) ✅.
See `docs/ROADMAP.md` for full status and next steps.
See `docs/adr/` for architecture decision records.

## Last Session Summary (2026-03-11)

> Context for the next agent. Read this instead of re-exploring files.

### What Changed

**Frontend (`frontend/`):**
- **Library View Modes**: Added Grid, Compact, and List layouts. High-performance client-side switching via `localStorage`. Compact mode uses hover overlays for a dense UI. List mode uses horizontal rows for data scanning.
- **Improved Layout**: Fixed vertical alignment between filter buttons and view toggles on the library page.
- **Network Exposure**: Updated `package.json` and `.devcontainer/docker-compose.yml` to expose the Next.js server on `0.0.0.0:3000`, allowing access via the host's actual IP address.

**Backend (`services/core/`):**
- **Auto-Hide & Ignore**: Implemented `IsIgnored` flag on `UnifiedGame`. Added keyword-based filtering in `UnificationService` (demo, beta, server, etc.).
- **Retroactive Ignore Service**: New `POST /api/library/retroactive-ignore` endpoint to scan existing library and hide junk games matching the filter list.
- **Admin Dashboard Integration**: Added a "Run Keyword Filter" button to the Sync Panel to trigger retroactive ignore.
- **Personal Game Tracking**: Added 5-star rating (1-10 scale) and Play Status (`Unplayed`, `Playing`, `Completed`, etc.) with manual overrides and automatic playtime detection.

### Known State / Pending
- **Network Access**: Frontend is now accessible at `http://<your-ip>:3000` but requires the devcontainer to be rebuilt if port mapping changes.
- **Retroactive Ignore**: Verified working on 2400+ games.
- **IGDB improvements**: Stable and tested.
- All tests pass, both builds clean.
- Remaining roadmap: Background Sync Scheduler (#6), MCP Server (#11), User Accounts (#8), Activity Feed (#9)

## Conventions

- All controllers have `ILogger` injection with structured logging (`{Platform}`, `{Count}`, etc.)
- XML doc comments (`/// <summary>`) on all public members
- Comments explain *why*, not *what*
- Tests follow pattern: `MethodName_Scenario_ExpectedResult`

## Development Environment

- **dotnet is NOT installed on the host machine.** Do not attempt to run `dotnet build`, `dotnet test`, `dotnet ef`, or any other dotnet CLI commands directly on the host.
- **npm / npx are NOT installed on the host machine.** Do not attempt to run `npm install`, `npm run`, `npx`, or any other Node.js CLI commands directly on the host (including `npx shadcn`). To add shadcn components, create the files manually.
- All building, testing, and dependency management are done inside a **devcontainer**.
- The devcontainer container name is: **`game-library-backend_devcontainer-app-1`**

### Running Commands via `docker exec`

The AI agent CAN run build/test commands inside the devcontainer using `docker exec` (the devcontainer must already be running):

```bash
# Backend build
docker exec -w /workspaces/game-library-backend game-library-backend_devcontainer-app-1 dotnet build services/core/GameHub.Core.csproj --verbosity quiet

# Backend tests
docker exec -w /workspaces/game-library-backend game-library-backend_devcontainer-app-1 dotnet test --verbosity quiet

# Frontend build
docker exec -w /workspaces/game-library-backend/frontend game-library-backend_devcontainer-app-1 npm run build

# EF Core migrations
docker exec -w /workspaces/game-library-backend game-library-backend_devcontainer-app-1 dotnet ef migrations add <Name> --project services/core/GameHub.Core.csproj
```

The workspace is mounted at `/workspaces/game-library-backend` inside the container.

## 🚨 Critical AI Agent Rules 🚨

1. **NEVER quietly ignore failed code edits.** If a code edit tool returns an error (e.g., "Cannot find original content"), you MUST halt, re-read the file, and apply the edit successfully before proceeding or notifying the user.
2. **Verify your work.** Always double-check that your code edits were actually applied. Do not blindly assume success if you fired off multiple tools concurrently.
3. **No sweeping assumptions.** Do not tell the user a feature is complete until you have verified the code actually exists in the file.
4. **Always research latest stable tool versions.** Before proposing or hardcoding a version number for an OS package, runtime (Node.js, Go), NuGet package, or NPM package in an implementation plan, research the *current* latest stable release date/version rather than guessing from old memory blocks.
