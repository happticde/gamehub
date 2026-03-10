---
name: Build and Test via Docker
description: How to build, test, and run commands inside the devcontainer via docker exec
---

# Build and Test via Docker

The project runs inside a devcontainer. All build/test commands must be executed via `docker exec`.

## Prerequisites

The devcontainer must be running. Verify with:

```bash
docker ps --filter name=game-library-backend_devcontainer-app-1 --format '{{.Names}} | {{.Status}}'
```

Expected output: `game-library-backend_devcontainer-app-1 | Up ...`

If not running, the user needs to open the project in VS Code with the Dev Containers extension.

## Container Details

| Property | Value |
|----------|-------|
| Container name | `game-library-backend_devcontainer-app-1` |
| Workspace mount | `/workspaces/game-library-backend` |
| .NET version | 10.0 preview |
| Node.js version | 24 |
| Go version | 1.26 |

## Commands

### Backend Build

```bash
docker exec -w /workspaces/game-library-backend game-library-backend_devcontainer-app-1 \
  dotnet build services/core/GameHub.Core.csproj --verbosity quiet
```

To build the Ingest service:

```bash
docker exec -w /workspaces/game-library-backend game-library-backend_devcontainer-app-1 \
  dotnet build services/ingest/GameHub.Ingest.csproj --verbosity quiet
```

### Backend Tests

```bash
docker exec -w /workspaces/game-library-backend game-library-backend_devcontainer-app-1 \
  dotnet test --verbosity quiet
```

### Frontend Build

```bash
docker exec -w /workspaces/game-library-backend/frontend game-library-backend_devcontainer-app-1 \
  npm run build
```

### EF Core Migrations

```bash
docker exec -w /workspaces/game-library-backend game-library-backend_devcontainer-app-1 \
  dotnet ef migrations add <MigrationName> --project services/core/GameHub.Core.csproj
```

### Install npm Package

```bash
docker exec -w /workspaces/game-library-backend/frontend game-library-backend_devcontainer-app-1 \
  npm install <package-name>
```

## Recommended Verification Flow

After making code changes, run this sequence:

1. **Backend build** — catches C# compilation errors
2. **Tests** — catches logic regressions (68 tests as of 2026-03-10)
3. **Frontend build** — catches TypeScript errors and Next.js build issues

All three should exit with code 0 before considering work complete.
