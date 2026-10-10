# Development Environment Context

## Environment Restrictions
- **dotnet is NOT installed on the host machine.** 
- **npm / npx are NOT installed on the host machine.**
- Use **Docker Exec** for all building, testing, and dependency management.
- CI and shared release workflows are documented in `docs/ci-and-releases.md`. The reusable
  checks include source/CLI/image security scans; release publication is manually dispatched
  from `main` and gated by every check. Keep versions, tool archive hashes, and Action/image pins
  reviewed. Go remains on the 1.26 line with the patched 1.26.9 toolchain/helper builder.
- Image CI uses native Ubuntu 26.04 AMD64/ARM64 runners, with no QEMU build emulation.
  Keep the frontend helper build independent from Next.js by copying downloads only into
  the runtime stage. Docker cache export is best-effort; build/scan/release gates are mandatory.
- `dotnet-ef` is installed system-wide by the devcontainer image and pinned to the EF Core version
  used by the service projects. Rebuild the devcontainer after changing that version.

## Git Workflow
- The `main` branch is protected. **Never commit or push directly to `main`.**
- Before editing files, verify the current branch. If it is `main`, create and switch to a new task-specific branch first.
- Push only the task branch and integrate changes into `main` through a pull request.

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
