# ADR 004: Comprehensive Audit Logging Strategy

## Status
Proposed

## Context
Following the successful implementation of the `EnrichmentAuditLog` for metadata fetching, there is a clear necessity for visibility into other automated background processes across the GameHub backend. 

Currently, the system relies on standard application logging (`ILogger`) to track when the Ingest service fetches data from platforms (Steam, GOG, PSN) and when the Core service synchronizes that data. However, `ILogger` outputs are ephemeral (unless piped to an external log aggregator) and cannot be easily queried by a user interface or API.

## Decision
We will implement persistent, database-backed audit logging for all critical background synchronization and data ingestion boundaries.

### 1. Ingest Service: Platform Fetcher Logging
**Entity: `IngestAuditLog`** (resides in Ingest database)
When the Ingest service pulls data from Steam, GOG, or PSN, it encounters scenarios like expired cookies, rate limits, or private profiles. 

Fields:
- `Timestamp`
- `Platform` (Steam, GOG, PSN, Epic)
- `Status` (Success, TokenExpired, RateLimited, Error)
- `GamesFound` (int) - How many new/updated games were retrieved
- `ErrorMessage` (contextual details if failed)

**API:** `GET /api/ingest/history?platform=Steam`

### 2. Core Service: Unification Sync Logging
**Entity: `SyncAuditLog`** (resides in Core database)
When the Core service pulls data from Ingest (`UnificationService.RunFullSyncAsync`), it matches physical games against the unified library using slugs. Tracking this allows users to see exactly when new games were added to their library.

Fields:
- `Timestamp`
- `Platform`
- `Status` (Success, Error)
- `ItemsProcessed` (int)
- `NewGamesCreated` (int) - Count of games that didn't match an existing slug
- `ErrorMessage`

**API:** `GET /api/library/sync-history`

## Consequences
- **Positive:** Full visibility into the health of the system. The future Frontend can display a "System Health" dashboard showing exactly when Steam was last polled successfully, how many games were synced across services, and if any cookies need rotating.
- **Negative:** Increased database writes and storage requirements. 
- **Mitigation:** We will implement a cleanup task (or simple deletion endpoint) in the future to prune audit logs older than 30 or 60 days to prevent infinite table growth.
