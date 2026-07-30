# Core Tenant-Isolation Audit

**Date:** 2026-06-21

**Scope:** Roadmap item #8, user-owned Core service paths

**Result:** Isolation, account lifecycle, production OIDC authentication/recovery, and guarded
multi-user enablement are complete. The production switch remains deliberately disabled by default
and still requires explicit operator configuration.

## Audited paths

| Area | Reads and writes reviewed | Isolation boundary |
|------|---------------------------|--------------------|
| EF Core ownership | `UnifiedGame`, `LibraryItem`, `PhysicalGameCopy`, `Tag`, `TagAssignment`, `ManualLink`, `EnrichmentAuditLog`, `ActivityLog`, `SyncAuditLog`, and `SyncMetadata` | Global query filters select the trusted `ITenantContext`; the save guard assigns ownership and rejects foreign-tenant creates, updates, and deletes. Tenant-aware keys and relationships enforce game, physical-copy, and tag associations below the query-filter layer. |
| Library API | Library list/detail/random, tags, developers, publishers, enrichment history/stats, sync history, analytics, retroactive ignore, personal tracking, merge, split, and activity feed | All durable reads and mutations use tenant-filtered Core sets. Direct cross-tenant tests cover personal tracking, merge, split, operational history, and activity reads. |
| Sync orchestration | Manual sync, `UnificationService` page processing, manual-link lookup, slug matching, `SyncMetadata` incremental cursors, and `SyncAuditLog` writes | Request sync uses the authenticated tenant. Every child context created by the unifier resolves the same trusted tenant. Metadata natural keys are unique per `(TenantId, Platform)`. |
| Background work | `TenantJobRunner`, scheduled sync, and background enrichment | A server-selected `TenantExecutionContext` creates an isolated scope per tenant. Tests verify per-tenant progress, audit/activity ownership, policy skips, error continuation, and enrichment batch limits. |
| Scheduler status | `/api/library/sync/status` and in-memory scheduler results | Results, error text, running state, and processed counts are stored per tenant. Only the schedule-enabled flag and next-cycle time are shared operational configuration. |
| Backup and restore | JSON/CSV export, validation context, empty-library check, restore, and restore activity | Export and restore are scoped to the active tenant; other tenants neither appear in output nor block or change restore. V1/V2 documents remain tenant-neutral, and V2 physical copies receive ownership only from the trusted restore context. |
| Credential policy | Core credential management, stored retrieval delegation, and `Tenant.PlatformSyncEnabled` updates | Core selects the tenant from trusted authentication, delegates it with the service key, and updates only that tenant's sync policy. Cross-tenant activation/deactivation tests exist. |
| Identity and sessions | Account bootstrap/provisioning/login, session epochs, and rotating refresh sessions | Account discovery is intentionally global until credentials establish the tenant. Email uniqueness, password verification, exact external mapping, live epoch checks, unguessable token-hash lookup, rotation, expiry, and revocation form the trust boundary; request bodies cannot select an authenticated tenant. |
| Account lifecycle | Password change, global refresh-session revocation, pending deletion, Core cleanup, and Core→Ingest deletion delegation | Signed account/tenant claims select the identity. Current-password confirmation protects password change and deletion. Pending deletion blocks ordinary JWT access; Ingest and Core cleanup are idempotent and tenant-filtered. |
| Production identity | Next.js OIDC state/nonce/PKCE/callback handling, BFF-only Core exchange, Core ID-token validation, explicit external-identity linking, exact issuer/subject login, and local GameHub session issuance | The BFF uses an authenticated encrypted correlation cookie, exact redirect binding, and a separate Core service key. Core pins issuer, audience, algorithms, nonce, lifetime, and JWKS. A composite database relationship binds the linked account and tenant; email, roles, organization, and tenant-like claims are ignored for mapping. |
| Guarded enablement | Core/Ingest/BFF startup configuration, OIDC admission, local bootstrap linking, API-key compatibility, operator readiness, and disable/rollback | An immutable default-off operator switch is independent from OIDC. Active mode requires complete trust/cookie/vault/link readiness, JWT-only ordinary Core authorization, delegation-only ordinary Ingest authorization, and OIDC-only full login. Disable is non-destructive. |
| Personal-tenant JIT | Unknown-subject OIDC exchange, account/tenant creation, activity/session creation, deletion tombstones, and external reauthenticated deletion | JIT requires both an explicit provisioning mode and active guarded mode. Core generates every ID and commits tenant, external account, exact mapping, refresh session, and tenant activity atomically. Claims cannot select ownership; parallel first login and deleted subjects cannot create duplicates or orphans. |
| Provider-initiated logout | Public form-only BFF receiver, Core Logout Token validation, local session families, account epoch, refresh revocation, replay markers, and tenant activity | A validated issuer-bound hashed `sid` reaches only its previously persisted exact identity/account/tenant family; `sub` without `sid` resolves the exact external identity and revokes the account. Provider email, role, group, organization, request body, and browser cookies select nothing. Cross-tenant tests cover both scopes. |
| Active session management | Authenticated family list, one-family revoke, revoke-other-families, refresh races, Admin Security BFF/UI, and browser convergence after remote revocation | Account, tenant, and current family come only from validated GameHub claims. Target IDs are constrained again by account and tenant; foreign, unknown, cross-tenant, and repeated identifiers are no-ops. Local and OIDC families use the same transaction and expose no provider/device metadata. The affected browser clears only its own HttpOnly cookies after Core rejects both access and refresh; the still-active revoking family and other tenants are unchanged. |

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

## Production OIDC path verification

The production-authentication follow-up reviewed and tested:

- API-key bootstrap/provisioning and local account/password compatibility paths.
- GameHub JWT issuance/validation, rotating refresh sessions, live-account checks, pending deletion,
  account cleanup, and default-tenant preservation.
- Next.js login/proxy/logout cookies plus OIDC discovery, authorization redirect, encrypted
  correlation cookie, exact callback, code exchange, PKCE, and Core BFF calls.
- Core OIDC discovery/JWKS retrieval, fixed issuer/audience/algorithm validation, nonce checks,
  exact issuer/subject mapping, composite account/tenant ownership, and session issuance.
- SQLite and real PostgreSQL upgrades preserving local accounts, refresh sessions, default tenant,
  unified games, and library items while adding empty external-identity storage.

Direct tests reject wrong issuer, audience, algorithm, nonce, state, callback redirect, PKCE
verifier, tampered/expired correlation cookies, foreign-tenant mapping, email collisions, provider
outages, pending deletion, and deleted accounts.

## Guarded enablement verification

- New installations and upgrades default the switch to false; invalid values fail instead of being
  coerced. OIDC configuration by itself admits only the default compatibility tenant.
- Core startup validates JWT/OIDC, mirrored BFF auth/Secure-cookie/switch state, strong independent
  operator/delegation/JWT/BFF trust, and explicit issuer links for every live account.
- Ingest independently requires delegation trust and an active vault key. Active-mode default
  policies reject ordinary Core/Ingest API-key compatibility access. Core also probes an internal
  secret-free Ingest readiness endpoint through the real delegation key, detecting mode drift,
  vault unavailability, service-key mismatch, malformed responses, and outages.
- Local password authentication creates a normal session only for the default tenant while the gate
  is off. Otherwise, an unlinked account can receive only a ten-minute link-purpose JWT accepted on
  the exact OIDC linking POST; linked accounts cannot use passwords as a production login bypass.
  Active-mode provisioning is rejected until an operator disables the mode for maintenance. A
  pending account can receive only a deletion-retry-purpose JWT for the exact deletion endpoint.
- Provider email, roles, groups, organization, and tenant-like claims remain outside the mapping.
  Pending-deletion and deleted accounts remain unavailable.
- Background jobs enumerate provisioned tenants only while enabled. Disabling preserves every row,
  stops new provisioned-tenant admission, and leaves existing sessions under ADR-011 expiry and
  revocation behavior.
- Secret-free operator status tests reject configured keys, tokens, account data, counts, and tenant
  data in the response. Core configuration logs contain only safe state/blocker labels.
- SQLite and real PostgreSQL upgrades preserve the default tenant, local password hash, explicit
  external identity, refresh session, unified game, and library item while enabled readiness is
  evaluated without writes. No new database migration was necessary.

Roadmap #8 is technically complete. A deployment must still select and govern a trusted provider,
exercise its recovery/MFA/abuse policies and GameHub rollback checklist, then deliberately set the
default-off switch. This audit does not claim public registration or unrestricted SaaS readiness.

## Personal-tenant JIT verification

The ADR-013 follow-up adds an explicit `Local` versus `ExternalOidc` account boundary. Existing
rows migrate to `Local`; SQLite and PostgreSQL check constraints plus a filtered local-email index
reject external password hashes/normalized emails and invalid local credentials. Upgrade tests
preserve the default tenant, local email/hash, exact external identity, refresh session, unified
game, library item, and pending-deletion timestamp.

Direct tests cover default-off and invalid provisioning configuration, OIDC-only and gate-off
non-provisioning, server-generated sync-disabled personal tenants, missing/equal provider email,
provider claim non-selection, parallel first-login idempotency, no orphan rows, existing mapping
login, pending/deleted blocking, local credential rejection, secret-free activity/readiness, and
readiness without data mutation. Real PostgreSQL and SQLite fresh/upgrade paths exercise the same
constraints.

External deletion requires a fresh exact OIDC proof and produces only a deletion-purpose token
accepted on one method and route. Completed deletion persists an issuer/subject tombstone before
the live mapping cascades, so JIT cannot undo deletion. User-triggered RP-Initiated browser logout
and provider-neutral Back-Channel Logout are implemented. A validated issuer-bound hashed `sid`
reaches only its previously persisted tenant/account session mapping; `sub` without `sid` resolves
the exact external identity and revokes every local family for that account. Inconsistent
subject/session mappings are rejected and direct cross-tenant tests cover both paths. Other
provider lifecycle events remain separate.

## Physical collection follow-up verification (2026-07-13)

`PhysicalGameCopy` is tenant-owned and relates to `UnifiedGame` through `(TenantId, UnifiedGameId)`.
SQLite and PostgreSQL enforce the composite relationship, and both providers constrain the physical
platform to PS3, PS4, or PS5. Direct tests cover cross-tenant read/delete denial, foreign-game write
rejection, same-platform coexistence across tenants, merge/split behavior, and V2 backup export and
restore isolation. Request bodies contain only the physical platform; tenant ownership is never
accepted from client data.

Phase 2A adds a nullable, non-unique canonical GTIN to each copy and indexes it by tenant. Lookup
begins from the filtered `PhysicalGameCopies` set and returns every conflicting game candidate only
inside the authenticated tenant. Capture rejects a foreign game target; manual game plus copy is one
transaction; request bodies still cannot select ownership. Backup V3 restores repeated GTINs into
the authenticated tenant without importing source ownership. Direct tests cover same-GTIN
coexistence across tenants, non-inference, conflicting local mappings, foreign targets, duplicate
copies, manual capture, parallel slug allocation, and cross-tenant V3 restore.

Phase 2B keeps this tenant-owned mapping authoritative. The local query completes before any
external adapter decision; one or more local candidates suppress the provider call entirely.
On a local miss, the provider receives only the canonical GTIN and never receives a tenant ID,
game ID, copy ID, local candidate, credential, or other tenant data. Returned title/platform hints
are transient, untrusted, and contain no selectable game ID. They are neither persisted nor shared
between tenants, and the browser must explicitly copy one into search/manual fields before the
existing confirmed write can occur.

Direct tests prove that conflicting local mappings suppress the provider, another tenant's mapping
neither leaks nor suppresses a local-miss lookup, invalid GTINs never invoke the provider, and an
external hit performs no game/copy write or selection. Disabled, no-result, 429, temporary failure,
timeout, and malformed responses all return an empty external-hint list without raw GTINs,
credentials, provider payloads, diagnostics, or status details in logs/browser responses. No
database schema, backup format, authentication, cookie, digital sync, or cross-tenant learning path
was added in Phase 2B.

The manual-first IGDB follow-up removes barcode, GTIN, and catalogue suggestions from the active UI
while preserving that server-side infrastructure. The authenticated enrichment search is stateless
and writes no tenant data. A confirmed capture re-fetches the explicitly selected provider ID, then
queries `UnifiedGames` through the active tenant filter for the exact
`(MetadataSource, MetadataProviderId)` pair. It never uses an unfiltered query, caller tenant ID,
title equality, or another tenant's match. On a local miss, the verified metadata, automatic tags,
and first physical copy are committed atomically with ownership supplied by `CoreContext`.

Direct two-tenant tests prove that an identical IGDB ID in another tenant is neither reused nor
disclosed, an exact same-tenant identity is reused, provider-backed creation remains tenant-owned,
multiple same-platform copies remain valid, and no `LibraryItem` or `ManualLink` is created. Search,
missing-result, provider-failure, and unconfirmed requests are write-free and expose only sanitized
messages. No global IGDB mapping, schema, backup, authentication, cookie, or sync change was added.
