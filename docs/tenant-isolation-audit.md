# Core Tenant-Isolation Audit

**Date:** 2026-06-21

**Scope:** Roadmap item #8, user-owned Core service paths

**Result:** Isolation and initial account-lifecycle gates complete; multi-user operation remains
disabled pending production authentication and guarded rollout work.

## Audited paths

| Area | Reads and writes reviewed | Isolation boundary |
|------|---------------------------|--------------------|
| EF Core ownership | `UnifiedGame`, `LibraryItem`, `Tag`, `TagAssignment`, `ManualLink`, `EnrichmentAuditLog`, `ActivityLog`, `SyncAuditLog`, and `SyncMetadata` | Global query filters select the trusted `ITenantContext`; the save guard assigns ownership and rejects foreign-tenant creates, updates, and deletes. Tenant-aware keys and relationships enforce game and tag associations below the query-filter layer. |
| Library API | Library list/detail/random, tags, developers, publishers, enrichment history/stats, sync history, analytics, retroactive ignore, personal tracking, merge, split, and activity feed | All durable reads and mutations use tenant-filtered Core sets. Direct cross-tenant tests cover personal tracking, merge, split, operational history, and activity reads. |
| Sync orchestration | Manual sync, `UnificationService` page processing, manual-link lookup, slug matching, `SyncMetadata` incremental cursors, and `SyncAuditLog` writes | Request sync uses the authenticated tenant. Every child context created by the unifier resolves the same trusted tenant. Metadata natural keys are unique per `(TenantId, Platform)`. |
| Background work | `TenantJobRunner`, scheduled sync, and background enrichment | A server-selected `TenantExecutionContext` creates an isolated scope per tenant. Tests verify per-tenant progress, audit/activity ownership, policy skips, error continuation, and enrichment batch limits. |
| Scheduler status | `/api/library/sync/status` and in-memory scheduler results | Results, error text, running state, and processed counts are stored per tenant. Only the schedule-enabled flag and next-cycle time are shared operational configuration. |
| Backup and restore | JSON/CSV export, validation context, empty-library check, restore, and restore activity | Export and restore are scoped to the active tenant; other tenants neither appear in output nor block or change restore. The V1 document remains tenant-neutral. |
| Credential policy | Core credential management, stored retrieval delegation, and `Tenant.PlatformSyncEnabled` updates | Core selects the tenant from trusted authentication, delegates it with the service key, and updates only that tenant's sync policy. Cross-tenant activation/deactivation tests exist. |
| Identity and sessions | Account bootstrap/provisioning/login and rotating refresh sessions | Account discovery is intentionally global until credentials establish the tenant. Email uniqueness, password verification, unguessable token-hash lookup, rotation, expiry, and revocation form the trust boundary; request bodies cannot select an authenticated tenant. |
| Account lifecycle | Password change, global refresh-session revocation, pending deletion, Core cleanup, and Core→Ingest deletion delegation | Signed account/tenant claims select the identity. Current-password confirmation protects password change and deletion. Pending deletion blocks ordinary JWT access; Ingest and Core cleanup are idempotent and tenant-filtered. |

## Operational-history verification

`ActivityLog`, `SyncAuditLog`, and `SyncMetadata` now have direct two-tenant read and write tests. The scheduled-sync integration test verifies tenant-owned activity and audit writes. SQLite and real PostgreSQL upgrade tests start before the tenant foundation migration and prove that existing operational rows are preserved and assigned to the default tenant.

The isolation audit itself required no new migration: the existing tenant-foundation migrations
already preserve these rows, add the default owner, tenant-scope the sync cursor uniqueness
constraint, and add tenant foreign keys for both providers. The lifecycle follow-up adds only the
nullable, default-preserving deletion-state column described in ADR-010.

## Account-lifecycle path verification

The lifecycle follow-up reviewed and tested these trust paths:

- Core `AuthController`, `AccountService`, `RefreshTokenService`, JWT issuance/validation, tenant
  context, account/refresh entities, and all tenant-owned Core sets.
- Core `IngestClient` delegation, including its separate service key and server-selected
  `X-GameHub-Tenant-Id` header.
- Ingest delegation authentication, tenant context/query filters, raw platform sets, audit rows,
  encrypted `PlatformCredential` rows, and the internal tenant-cleanup controller/service.
- Next.js HttpOnly-cookie login, refresh, logout, and Core BFF response handling for lifecycle
  actions that invalidate sessions.

Direct tests exercise mismatched account/tenant identities, stale refresh tokens, exclusive and
repeated deletion, fail-closed Ingest failure plus retry, encrypted credential removal, in-memory
scheduler cleanup, and SQLite/PostgreSQL upgrade preservation.

## Remaining Roadmap #8 work

- The initial account lifecycle is defined and implemented in ADR-010. Its explicit recovery
  boundary remains until a trusted production identity/email provider exists.
- Complete and select the production authentication flow; OAuth remains an option.
- Add an explicit guarded multi-user enablement path only after production authentication and
  recovery are implemented and verified.

Until those items are complete, GameHub must not advertise or enable production multi-user operation.
