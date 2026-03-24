# Session History

## Last Session Summary (2026-03-24)

### What Changed

**Backend (`services/core/`):**
- **Activity Feed (#9)**: New `ActivityLog` entity tracking system events (sync start/finish, merge, split). New `ActivityLogService` (scoped) for centralized event logging. New `GET /api/activity` endpoint returning latest 20 entries.
- **Event Integration**: `SyncSchedulerService` logs `SyncStarted`/`SyncFinished` events. `LibraryController` logs events for manual sync, merge, and split actions.
- **Migration**: `AddActivityLog` migration generated for the new table.

**Frontend (`frontend/`):**
- **Activity Panel**: New `ActivityPanel.tsx` component in the admin dashboard showing recent activity with relative timestamps ("5 mins ago"), event-type icons/badges (sync, merge, split), auto-refresh (30s), and empty/loading/error states.
- **Admin Dashboard**: Fourth tab "Activity" added with Bell icon.
- **Proxy**: `/api/activity` route added to `next.config.ts`.

### Known State / Pending
- All tests pass (71/71, up from 68), both builds clean.
- Sync scheduler defaults to disabled — set `Sync:EnableScheduler=true` to activate.
- Remaining roadmap: MCP Server (#11), User Accounts (#8)

