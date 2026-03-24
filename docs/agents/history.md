# Session History

## Last Session Summary (2026-03-17)

### What Changed

**Backend (`services/core/`):**
- **Background Sync Scheduler (#6)**: New `SyncSchedulerService` (`BackgroundService`) that runs `RunFullSyncAsync()` on a configurable interval. Disabled by default (`Sync:EnableScheduler=false`). Tracks state via thread-safe `SyncSchedulerStatus` singleton.
- **Sync Status Endpoint**: New `GET /api/library/sync/status` returns scheduler state (isEnabled, isRunning, lastRunAt, nextRunAt, lastResult, itemsProcessed).
- **Configuration**: New `Sync` section in `appsettings.json` (`EnableScheduler`, `IntervalMinutes`).
- **Docker Compose**: Improved with Postgres health checks, provider-specific EF Core migrations.

**Frontend (`frontend/`):**
- **Scheduler Status Card**: New card in the Admin Dashboard Sync tab showing live scheduler status with auto-refresh (30s), enabled/disabled badge, running state with pulse animation, last/next run timestamps with relative countdowns, and last result with item count.

### Known State / Pending
- All tests pass (68/68), both builds clean.
- Sync scheduler defaults to disabled — set `Sync:EnableScheduler=true` to activate.
- Remaining roadmap: MCP Server (#11), User Accounts (#8), Activity Feed (#9)
