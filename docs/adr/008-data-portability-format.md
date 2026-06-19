# ADR-008: Data Portability Format

**Date:** 2026-06-19
**Status:** Accepted
**Deciders:** @jankahnt

## Context

GameHub needs a portable backup that can recreate the user-owned unified library independently of the configured database provider. A raw SQLite or PostgreSQL dump would be provider-specific, difficult to validate, and tightly coupled to internal table layouts.

The unified library contains nested and relational data: personal tracking, enriched metadata, tags, platform items, and manual merge/split overrides. A flat CSV file cannot represent those relationships without lossy conventions.

## Decision

GameHub will use a versioned JSON document as its canonical backup and restore format.

The initial format is identified by:

```json
{
  "format": "gamehub-backup",
  "formatVersion": 1
}
```

Version 1 includes:

- Unified games and their slugs.
- Personal play status, rating, manual-status flag, and ignored flag.
- Enriched metadata and metadata provider references.
- Tags.
- Platform library items.
- Manual merge/split links, nested under their target game.

Version 1 deliberately excludes:

- Database primary keys and foreign keys.
- API credentials and other secrets.
- Ingest raw data, which remains owned by the Ingest service.
- Sync metadata and audit/activity logs, which are operational history rather than user library state.

Export order is deterministic to make backups reviewable and diff-friendly. Restore will validate both `format` and `formatVersion`, run transactionally, and initially accept only an empty target library. CSV will be offered later as a human-readable export, not as a full-fidelity restore format.

## Rationale

- JSON preserves nested relationships without database-specific identifiers.
- Explicit versioning allows future schema evolution and migration.
- Excluding operational history keeps backups focused on durable user-owned state.
- Empty-library restore avoids ambiguous merge and conflict behavior in the first release.
- A read-only export can ship and be verified independently before destructive restore operations are introduced.

## Consequences

### Positive

- Backups work across SQLite and PostgreSQL deployments.
- The contract is stable, testable, and independent of EF Core entities.
- Future import code can map records to new database IDs safely.

### Negative

- Format migrations will be required when breaking changes are introduced.
- CSV cannot be used for complete restoration.
- Audit history and raw platform payloads are not restored.

## Delivery Plan

1. Add `GET /api/backup` for a downloadable V1 JSON document.
2. Add validation and transactional restore into an empty library.
3. Add an Admin UI for download, validation preview, and restore.
4. Add a separate flat CSV export for spreadsheet use.
