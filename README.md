# GameHub Backend

A unified game library aggregator that ingests data from **Steam**, **PSN**, and **GOG** into a single queryable library. It merges games across platforms using intelligent title matching and provides a REST API for your unified collection.

## Architecture

```
+-------------------+                          +-------------------+
|   gamehub-cli     |  POST /api/ingest/{p}    |                   |
|   (Go CLI)        | -----------------------> |  Ingest Service   |
+-------------------+                          |  :5000            |
                                               |                   |
  Any HTTP client      POST /api/retrieve/{p}  |  Fetches from     |
  (curl, browser,   ----------------------->   |  Steam/GOG/PSN    |
   future UI)                                  +--------+----------+
                                                        |
                       GET /api/export/{p}              |
+-------------------+ <---------------------------------+
|                   |
|  Core Service     |
|  :5001            | -->  Unified Library (slug matching + manual overrides)
|                   |
+-------------------+
```

**Three components:**
- **Ingest Service** (.NET) — stores raw game data per platform, can also fetch directly from APIs
- **Core Service** (.NET) — merges data into a unified library with cross-platform matching
- **CLI** (Go) — alternative client that fetches libraries and pushes to Ingest

## Quick Start

### 1. Start the backend

```bash
docker compose up --build
```

- Ingest API: `http://localhost:5000` (Swagger UI at root)
- Core API: `http://localhost:5001/swagger`
- Health checks: `/health` on both services

### 2. Install the CLI

```bash
cd services/cli
go build -o gamehub-cli .
```

### 3. Create a config file (optional)

Save credentials in `~/.gamehub.yaml` so you don't need flags every time:

```yaml
backend-url: http://localhost:5000
steam:
  key: "YOUR_STEAM_API_KEY"
  id: "YOUR_STEAM_ID_64"
gog:
  user: "your_gog_username"
  token: "your_gog_al_cookie_value"
psn:
  cookies: '{"JSESSIONID":"abc","sb-cdn.session":"xyz"}'
```

### 4. Ingest your libraries

```bash
# Steam — requires API Key + Steam ID 64
# Get key: https://steamcommunity.com/dev/apikey
# Get ID:  https://steamdb.info/calculator/
gamehub-cli ingest steam --key YOUR_KEY --id YOUR_STEAM_ID

# GOG — requires gog-al cookie + username
# Log into gog.com → DevTools → Cookies → copy "gog-al" value
gamehub-cli ingest gog --user YOUR_USERNAME --token YOUR_GOG_AL_COOKIE

# PSN — requires browser cookies from store.playstation.com
# Log into store.playstation.com → DevTools → Cookies → export as JSON
gamehub-cli ingest psn --cookies '{"JSESSIONID":"...","sb-cdn.session":"..."}'
```

Or with the config file, just:

```bash
gamehub-cli ingest steam
gamehub-cli ingest gog
gamehub-cli ingest psn
```

### Alternative: Server-Side Retrieval (no CLI needed)

The Ingest service can also fetch game libraries directly — no CLI required.
Just POST the credentials and the service handles both fetching and ingesting:

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

### 5. Trigger unification

```bash
curl -X POST http://localhost:5001/api/library/sync
```

### 6. Query your library

```bash
# List all games
curl http://localhost:5001/api/library

# Search
curl http://localhost:5001/api/library?search=witcher
```

## API Reference

### Ingest Service (`:5000`)

| Method | Endpoint | Description |
|--------|----------|-------------|
| `POST` | `/api/ingest/{platform}` | Batch upsert raw game data |
| `GET` | `/api/ingest/{platform}?page=1&pageSize=20` | List ingested items |
| `GET` | `/api/ingest/{platform}/{externalId}` | Get single item detail |
| `GET` | `/api/export/{platform}?page=1&pageSize=200&updatedAfter=` | Paginated export for Core sync |
| `POST` | `/api/retrieve/steam` | Fetch + ingest Steam library `{ apiKey, steamId }` |
| `POST` | `/api/retrieve/gog` | Fetch + ingest GOG library `{ cookie, user }` |
| `POST` | `/api/retrieve/psn` | Fetch + ingest PSN library `{ cookies: {...} }` |

### Core Service (`:5001`)

| Method | Endpoint | Description |
|--------|----------|-------------|
| `POST` | `/api/library/sync` | Trigger incremental sync from Ingest |
| `GET` | `/api/library?search=` | Query unified library |
| `POST` | `/api/library/merge` | Merge two games `{ sourceGameId, targetGameId }` |
| `POST` | `/api/library/split` | Split a platform entry `{ libraryItemId }` |

## Tech Stack

- **.NET 10** — Ingest + Core services (ASP.NET Core, EF Core)
- **Go 1.24** — CLI (Cobra + Viper)
- **SQLite** (dev) / **PostgreSQL** (prod)
- **Docker Compose** — orchestration with health checks

## Project Structure

```
services/
├── ingest/                 # Raw data storage service
│   ├── Controllers/
│   │   ├── IngestControllerBase.cs   # Generic base (shared logic)
│   │   ├── SteamIngestController.cs  # Platform-specific
│   │   ├── PsnIngestController.cs
│   │   ├── GogIngestController.cs
│   │   ├── EpicIngestController.cs
│   │   └── ExportController.cs      # Paginated export for Core
│   └── Data/
├── core/                   # Unified library service
│   ├── Controllers/
│   │   └── LibraryController.cs      # Library + merge/split
│   ├── Services/
│   │   ├── UnificationService.cs     # Slug matching + manual links
│   │   └── IngestClient.cs           # HTTP client for Ingest
│   └── Data/
├── shared/                 # Shared DTOs and interfaces
│   ├── IRawItem.cs
│   ├── RawGameDto.cs
│   ├── PagedResult.cs
│   └── Platform.cs
└── cli/                    # Go CLI for data ingestion
    ├── main.go
    └── cmd/
        ├── root.go
        ├── config.go         # ~/.gamehub.yaml support
        ├── helpers.go        # Shared PostToBackend logic
        ├── ingest.go         # Parent command
        ├── ingest_steam.go
        ├── ingest_gog.go
        └── ingest_psn.go
```

## Notes

- **Schema changes:** Run `dotnet ef migrations add <Name> --project services/<service>/<project>.csproj` and restart. Migrations are auto-applied on startup.
- **Swagger:** Available at `/swagger` on both services (Ingest also serves it at `/`).
- **Health checks:** Both services expose `GET /health` with DB connectivity checks.
