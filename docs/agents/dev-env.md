# Development Environment Context

## Environment Restrictions
- **dotnet is NOT installed on the host machine.** 
- **npm / npx are NOT installed on the host machine.**
- Use **Docker Exec** for all building, testing, and dependency management.

## Docker Shortcuts (via `docker exec`)
The devcontainer name is: **`game-library-backend_devcontainer-app-1`**. 
The workspace is mounted at `/workspaces/game-library-backend`.

### Common Commands

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
