# GameHub Backend — Agent Context

> This file provides structured context for AI coding assistants.
> Read this first before making changes.

## Architecture

Two ASP.NET Core microservices + Go CLI, orchestrated via Docker Compose:

```
┌──────────────┐     ┌──────────────┐
│   Ingest     │◄────│    Core      │
│  :5000       │     │   :5001      │
│              │     │              │
│ • Raw game   │     │ • Unified    │
│   storage    │     │   library    │
│ • Platform   │     │ • Slug-based │
│   fetchers   │     │   matching   │
│ • Export API │     │ • Merge/Split│
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
| Services | ASP.NET Core (.NET 10 preview), C# |
| CLI | Go (Cobra/Viper) |
| Database | SQLite (dev), Postgres (prod) via EF Core |
| ORM | Entity Framework Core with Migrations |
| Docker | Alpine-based images, Docker Compose |
| Tests | xUnit, in-memory SQLite |
| Auth | API key via `X-Api-Key` header (JWT-ready) |

## Key Directories

```
services/
  ingest/           # Ingest service (raw game data)
    Controllers/    # IngestControllerBase + 4 platform controllers, RetrieveController, ExportController
    Services/       # PlatformFetchers (Steam, GOG, PSN)
    Data/           # EF Core context + entities
  core/             # Core service (unified library)
    Controllers/    # LibraryController (sync, merge, split)
    Services/       # UnificationService, IngestClient
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
See `docs/ROADMAP.md` for full status and next steps.
See `docs/adr/` for architecture decision records.

## Conventions

- All controllers have `ILogger` injection with structured logging (`{Platform}`, `{Count}`, etc.)
- XML doc comments (`/// <summary>`) on all public members
- Comments explain *why*, not *what*
- Tests follow pattern: `MethodName_Scenario_ExpectedResult`
