# GameHub Backend — Roadmap

Current status and future direction for the project. Ideas are grouped from **high-impact / foundational** to **nice-to-have**.

---

## Current Status

| Area | Status |
|------|--------|
| Ingest service (Steam, GOG, PSN, Epic) | ✅ Complete |
| Core unification (slug matching, manual merge/split) | ✅ Complete |
| Go CLI (Cobra/Viper, config file) | ✅ Complete |
| Docker Compose (SQLite dev, Postgres prod) | ✅ With health checks |
| Tests | ✅ xUnit + in-memory SQLite |
| EF Core Migrations | ✅ Auto-applied on startup |
| Structured Logging | ✅ All components, debugging guide |
| Auth | ✅ API key middleware (JWT-ready) |
| CI/CD | ✅ GitHub Actions (build, test, Docker) |
| Frontend | ✅ Mobile-First: Grid/List, Filters Dialog, Screenshot Carousel |
| Enrichment / metadata | ✅ IGDB provider with caching, rate-limiting, smart search |
| Statistics / Analytics | ✅ Complete board with charts and histograms |
| Sync / Ignore logic | ✅ Auto-hide keywords + retroactive ignore |
| Background Sync Scheduler | ✅ BackgroundService + status endpoint + admin UI |

---

## 🏗️ Tier 1 — Foundational

### 1. Unit & Integration Tests ✅
xUnit test suite with in-memory SQLite covering slug generation, unification, and controllers.

### 2. EF Core Migrations ✅
Replaced `EnsureCreated()` with proper migrations for schema stability.

### 3. API Key / Basic Auth ✅
Simple API key middleware protecting ingest and sync endpoints.

---

## 🎮 Tier 2 — Feature Enrichment

### 4. Web Frontend (Dashboard) ✅
A browsable UI for the unified library with cover grid, search/filter, game detail pages, and admin actions.
**Enhanced (2026-03-26)**: Fully mobile-optimized with responsive filtering dialogs and touch-native screenshot carousels.

### 5. Personal Tracking & Ignore ✅
Added 5-star ratings, Play Status tracking, and "Auto-Hide & Ignore" keyword logic for junk entries.

### 6. Background Sync Scheduler ✅
`SyncSchedulerService` (`BackgroundService`) runs `RunFullSyncAsync()` on a configurable interval (default 6h). `GET /api/library/sync/status` endpoint exposes scheduler state. Admin UI card with live status and auto-refresh. Disabled by default via `Sync:EnableScheduler`.

---

## 🔧 Tier 3 — Polish & Operations

### 7. CI/CD Pipeline ✅
GitHub Actions: build → test → Docker build → push to registry.

### 8. User Accounts & Multi-Tenancy ✅
Per-user game libraries with encrypted credential storage. Auth via JWT or OAuth (Steam/PSN/GOG login).
- **Foundation (2026-06-21)**: Default-tenant migrations, claim-based tenant contexts, tenant-scoped Core and Ingest storage/queries/uniqueness constraints, tenant-isolated background jobs, local account bootstrap, optional JWT token issuance, rotating/revocable refresh sessions, API-key-controlled tenant provisioning, a frontend HttpOnly-cookie login/BFF flow, service-key-authenticated Core→Ingest tenant delegation, an AES-256-GCM tenant platform credential vault, vault-backed Steam/GOG/PSN retrieval, authenticated Core/frontend credential management, and a fail-closed credential-derived sync activation policy are implemented. The current Steam/GOG/PSN credential-management scope is complete; multi-user operation is not enabled by default.
- **Epic boundary**: Epic remains a fully manual Ingest flow through API or CLI. No Epic credentials
  are stored and no automatic Epic stored retrieval runs. Core continues to synchronize manually
  ingested Epic raw data. Future Epic authentication and stored retrieval are independently tracked
  in item #16 and are not pending work for the current credential-management scope.
- **Isolation verification (2026-06-21)**: Tenant-scoped backup export and restore, the active-tenant
  empty-library restore rule, equivalent cross-tenant slugs/platform IDs, and Library mutation
  boundaries for personal tracking, merge, and split have direct cross-tenant test coverage.
  Scheduled sync cycles also verify isolated tenant scopes, tenant-owned activity/audit rows, and
  policy skips that do not block later tenants. Background enrichment verifies independent
  per-tenant batch limits, progress, writes, and audit ownership. Tenant-aware database relations
  prevent LibraryItems, ManualLinks, enrichment audits, and tag assignments from referencing
  another tenant's game or tag. SQLite and PostgreSQL upgrades preserve existing tag assignments;
  any legacy cross-tenant assignment is retained through an equivalent tenant-local tag.
- **Isolation gate complete (2026-06-21)**: The remaining user-owned Core paths were audited,
  including ActivityLog, SyncAuditLog, SyncMetadata, their API reads and sync/background writes,
  and the in-memory scheduler status. Direct tests prove tenant-isolated operational history,
  foreign-owner write rejection, tenant-specific scheduler results, and SQLite/PostgreSQL upgrade
  preservation. See [Core Tenant-Isolation Audit](tenant-isolation-audit.md).
- **Account lifecycle (2026-06-21)**: Operator-controlled provisioning, current-password-verified
  password changes, account-wide refresh-session revocation, fail-closed/idempotent Core+Ingest
  tenant deletion (including encrypted credentials), and the no-email-provider recovery boundary
  are defined in [ADR-010](adr/010-account-lifecycle.md) and directly tested. This does not enable
  multi-user operation.
- **Production authentication/recovery gate (2026-06-21)**: Provider-neutral OIDC Authorization
  Code Flow with PKCE is implemented through the server-side BFF. Core independently pins issuer,
  audience, algorithms, nonce, and JWKS and maps only an explicitly linked `(issuer, subject)` to
  one account/tenant. Recovery, MFA, email verification, and provider credential security remain
  the configured trusted provider's responsibility. See [ADR-011](adr/011-production-oidc-authentication.md)
  and the [OIDC architecture walkthrough](oidc-authentication-architecture.md).
- **Guarded production enablement (2026-06-22)**: ADR-012 adds the independent,
  default-off `GAMEHUB_MULTI_USER_ENABLED` operator gate across Core, Ingest, and Frontend. OIDC
  configuration alone cannot activate provisioned tenants. Startup validates JWT/OIDC, Secure
  cookies, mirrored Core/BFF state, independent operator/BFF/delegation/signing trust, Ingest vault
  readiness, and explicit issuer links for every live account. Active mode permits normal user
  login only through OIDC; disabled-mode preparation may create only a short-lived,
  non-refreshable first-link token for an unlinked operator-provisioned account. Active-mode
  provisioning requires an explicit maintenance disable. API-key and Ingest compatibility access,
  background jobs, disable/rollback, readiness, and data-preservation boundaries are directly
  tested. See [ADR-012](adr/012-guarded-multi-user-enablement.md) and the
  [operator guide](guarded-multi-user-enablement.md).
- **Activation boundary**: Item #8 is technically complete, but production multi-user operation
  remains disabled after installation and upgrade until an operator deliberately satisfies the
  deployment/provider checklist, sets the guarded switch consistently, and restarts. This does not
  add public registration, JIT provisioning, unlimited SaaS readiness, tenant switching, or
  provider-derived authorization.

### 9. Notifications & Activity Feed
`ActivityLog` table + activity endpoint for social sharing/feed.

### 10. Statistics & Analytics ✅
Comprehensive dashboard with charts for platform distribution, genre breakdown, rating histograms, and play status tracking.

### 11. MCP Server (Model Context Protocol) ✅
AI agent integration layer — let assistants interact with the library via natural language.
See [docs/mcp.md](mcp.md) for client setup (VS Code, Claude Desktop, Cursor).

---

## 🚀 Tier 4 — Advanced Features & Portability

### 12. Data Portability (Backup & Restore) ✅
- **Feature**: GameHub-specific import/export via JSON/CSV.
- **Goal**: Full manual backup of entire unified library (metadata, ratings, play status).
- **Current scope**: JSON V1 export, validation, transactional empty-library restore, Admin UI, and spreadsheet-safe CSV export are implemented for the single-user deployment model.
- **Multi-tenancy status**: Export and restore are scoped to the authenticated tenant while the V1
  document remains tenant-neutral. Cross-tenant isolation and active-tenant empty-library behavior
  are covered by tests. See [ADR-008](adr/008-data-portability-format.md).

### 13. Physical Collection (Barcode Scanning)
- **Scanner**: Web-based barcode scanner (MediaDevices API) for physical game cases.
- **Platforms**: Support for PS3, PS4, and PS5 as distinct physical platforms.
- **Lookup**: Resolve barcodes to titles and automatically enrich via IGDB.

### 14. Discovery: "Surprise Me" (Random Pick) ✅
- **Feature**: Random game selection button that respects active filters (e.g., "Random RPG I haven't finished").
- **Goal**: Help users conquer their backlog by removing decision paralysis.
- **Implemented**: The Core random-library endpoint respects active filters, and the frontend
  provides a "Surprise Me" action that navigates to the selected game.

### 15. Power-User Maintenance Tools
- **Direct Enrichment**: Allow manual input of IGDB ID for instant, accurate matching.
- **Advanced Pagination**: Customizable page sizes, FIRST/LAST navigation, and direct page jumping.
- **Manual Split**: Ability to separate platform entries from a unified game into their own records.
- **Robust Sorting**: Logic to gracefully handle missing years or ratings (coalesce nulls).

### 16. Epic Authentication & Stored Retrieval
- **Current behavior**: Epic ingestion is manual through API or CLI; Core synchronizes the resulting
  raw data alongside other platforms.
- **Future scope**: Evaluate an Epic authentication mechanism, secure credential or token handling,
  and tenant-scoped stored retrieval as a standalone integration.
- **Boundary**: This work is not a missing part of the Steam/GOG/PSN credential-management feature
  and does not block that feature from being considered complete.

---

## 🌐 Tier 5 — Multi-User Productization & SaaS

These items are optional follow-up work after the completed guarded self-hosted multi-user mode in
item #8. They are not prerequisites for operator-provisioned deployments and must not weaken the
existing explicit tenant-mapping, isolation, lifecycle, or default-off activation boundaries.

### 17. Controlled Registration & Invitations
- **Goal**: Replace direct operator API calls with an auditable onboarding workflow when a
  deployment intentionally permits additional users.
- **Scope**: Invitation issuance, expiry, cancellation, single-use acceptance, verified-provider
  identity binding, rate limits, abuse controls, and an operator administration surface.
- **Security boundary**: Registration must never accept a caller-selected tenant or infer ownership
  from email, organization, role, or other provider claims. Cross-tenant invitation and replay tests
  are required before this can be enabled.
- **Non-goal**: Guarded multi-user deployments remain closed and operator-provisioned by default;
  this item does not imply anonymous public signup.

### 18. JIT Provisioning & External Identity Lifecycle
- **Goal**: Evaluate narrowly controlled just-in-time account/tenant provisioning and automated
  identity lifecycle integration as an explicit opt-in capability.
- **Scope**: Trusted provisioning authority, allowlists or assignment requirements, deterministic
  issuer/subject handling, deprovisioning, disabled-user behavior, subject replacement recovery,
  SCIM or provider-event evaluation, and reconciliation after missed events.
- **Security boundary**: Provider email, groups, roles, organizations, and tenant-like claims remain
  untrusted unless a future ADR defines their exact authority. Provisioning must be idempotent,
  auditable, revocable, and covered by cross-tenant isolation and lifecycle tests.
- **Dependency**: Requires a new architecture decision for provisioning and recovery trust; it
  cannot be activated merely by configuring OIDC.

### 19. SaaS Operations, Administration & Release Gate
- **Goal**: Define and satisfy the additional security, reliability, privacy, and operational bar
  required before claiming general SaaS readiness.
- **Scope**: Tenant administration and roles, support-access controls, global incident/session
  response, provider event and back-channel logout handling, audit retention, privacy and data
  deletion obligations, quotas and abuse prevention, availability targets, disaster recovery,
  key rotation, monitoring, capacity planning, and staged rollout controls.
- **Release gate**: Complete threat modeling, external security review, restore and regional-failure
  exercises, load and isolation testing, incident runbooks, and documented service ownership before
  any unrestricted SaaS claim.
- **Boundary**: This is an umbrella production gate, not evidence that public registration, JIT
  provisioning, billing, tenant switching, or enterprise administration already exist.

---

## Suggested Next Steps

| Goal | Path |
|------|------|
| **Stability & confidence** | ~~#1 Tests → #2 Migrations → #3 Auth~~ ✅ Done |
| **A demo-able product** | ~~#4 Metadata~~ ✅ Done → #5 Frontend → ~~#10 Stats~~ ✅ Done |
| **Production readiness** | ~~#7 CI/CD~~ ✅ → ~~#6 Scheduler~~ ✅ Done |
| **Multi-user readiness** | #8 technically complete; operator activation remains explicit and default-off ✅ |
| **AI integration** | #11 MCP Server (High Value) |
| **Data & Portability** | #12 Import/Export → #13 Barcode Scanning |
| **Maintenance & UI** | #15 Power-User Tools → ~~#14 Random Pick~~ ✅ Done |
| **Future platform integration** | #16 Epic authentication and stored retrieval |
| **Controlled onboarding** | #17 Registration/invitations → #18 JIT provisioning and identity lifecycle |
| **General SaaS readiness** | #19 operations, administration, security review, and release gate |
