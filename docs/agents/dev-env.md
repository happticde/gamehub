# Development Environment Context

## Environment Restrictions
- **dotnet is NOT installed on the host machine.** 
- **npm / npx are NOT installed on the host machine.**
- Use **Docker Exec** for all building, testing, and dependency management.
- The root `global.json` requires a stable .NET 10 SDK and allows newer 10.0 feature bands
  and patches. The devcontainer uses Microsoft's stable `10.0-noble` image. Rebuild an older
  preview-only container before building this checkout. Check `dotnet --version` from
  `/workspaces/gamehub`.
- CI and shared release workflows are documented in `docs/ci-and-releases.md`. The reusable
  checks include source/CLI/image security scans; release publication is manually dispatched
  from `main` and gated by every check. Keep versions, tool archive hashes, and Action/image pins
  reviewed. CLI, helper, notice, and devcontainer builders use the patched Go 1.27.3 toolchain.
- PR and push CI select relevant jobs with `scripts/ci/select-checks.py`. Documentation-only
  changes keep Gitleaks and the aggregate gate but skip builds/images. Shared library changes
  select Core/Ingest; shared build inputs also select MCP. MCP source changes select its image.
  CLI/helper inputs select the frontend image. Scheduled/manual CI
  and releases always run all checks. Never accept unexpected skips in the aggregate gate.
- Pure Action SHA/comment updates in `checks.yml` select their consuming jobs and workflow
  validation. Selector/aggregate changes and non-pin edits still select all checks. Dependabot
  and Actionlint configuration select workflow validation. Dependabot automatic rebases are
  disabled; refresh a PR when needed and wait for checks on the new commit.
- Image CI uses native Ubuntu 26.04 AMD64/ARM64 runners, with no QEMU build emulation.
  Keep the frontend helper build independent from Next.js by copying downloads only into
  the runtime stage. Docker cache export is best-effort; build/scan/release gates are mandatory.
- `dotnet-ef` is installed system-wide by the devcontainer image. Its version is read from
  `Directory.Packages.props` at image build time. Rebuild the devcontainer after EF Core updates.

## Git Workflow
- The `main` branch is protected. **Never commit or push directly to `main`.**
- Before editing files, verify the current branch. If it is `main`, create and switch to a new task-specific branch first.
- Push only the task branch and integrate changes into `main` through a pull request.

## Docker Shortcuts (via `docker exec`)
The devcontainer name is: **`gamehub_devcontainer-app-1`**.
The workspace is mounted at `/workspaces/gamehub`.

### Common Commands

```bash
# Backend build
docker exec -w /workspaces/gamehub gamehub_devcontainer-app-1 dotnet build services/core/GameHub.Core.csproj --verbosity quiet

# Backend tests
docker exec -w /workspaces/gamehub gamehub_devcontainer-app-1 dotnet test --verbosity quiet

# Frontend build
docker exec -w /workspaces/gamehub/frontend gamehub_devcontainer-app-1 npm run build

# EF Core migrations
docker exec -w /workspaces/gamehub gamehub_devcontainer-app-1 dotnet ef migrations add <Name> --project services/core/GameHub.Core.csproj
```
