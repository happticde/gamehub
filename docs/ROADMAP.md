# GameHub Backend — Roadmap

Current status and future direction for the project. Ideas are grouped from **high-impact / foundational** to **nice-to-have**.

Last audited against `origin/main`: **2026-07-30**.

---

## Current Status

| Area | Status |
|------|--------|
| Ingest service | ✅ Steam, GOG, and PSN complete; Epic stored-retrieval MVP |
| Core unification (slug matching, manual merge/split) | ✅ Complete |
| Go CLI (Cobra/Viper, config file) | ✅ Complete |
| Docker Compose (SQLite dev, Postgres prod) | ✅ With health checks |
| Tests | ✅ .NET, frontend, SQLite, and PostgreSQL migration/isolation coverage |
| EF Core Migrations | ✅ Auto-applied on startup |
| Structured Logging | ✅ All components, debugging guide |
| Auth | ✅ API key, local/JWT/OIDC, active sessions, JIT, Back-Channel Logout, encrypted vault, guarded multi-user mode |
| CI/CD | ✅ GitHub Actions (build, test, Docker) |
| Frontend | ✅ Mobile-First: Grid/List, Filters Dialog, Screenshot Carousel |
| Enrichment / metadata | ✅ IGDB provider with caching, rate-limiting, smart search |
| Statistics / Analytics | ✅ Complete board with charts and histograms |
| Sync / Ignore logic | ✅ Auto-hide keywords + retroactive ignore |
| Background Sync Scheduler | ✅ BackgroundService + status endpoint + admin UI |
| Provider refresh blockers | ✅ Tenant-scoped configuration/cooldown state at sync actions |
| Custom tags | ✅ Tenant-scoped assignments and saved suggestions |
| Physical collection | ✅ Manual-first IGDB capture; barcode/catalogue UI deferred |
| Activity feed | ✅ Tenant-scoped API and private Admin operations feed |
| Power-user maintenance | ✅ Direct matching, split, sorting, and customizable pagination |

---

## 🏗️ Tier 1 — Foundational

### 1. Unit & Integration Tests ✅
xUnit coverage includes service/controller behavior, tenant isolation, SQLite migration upgrades,
and PostgreSQL migration/integration paths. Focused frontend state tests cover critical interactive
workflows.

### 2. EF Core Migrations ✅
Replaced `EnsureCreated()` with proper migrations for schema stability.

### 3. API Key Authentication ✅
API-key authentication protects compatibility and operator endpoints. Item #8 contains the later
local/JWT/OIDC application-authentication and multi-tenant boundaries.

---

## 🎮 Tier 2 — Feature Enrichment

### 4. Web Frontend (Dashboard) ✅
A browsable UI for the unified library with cover grid, search/filter, game detail pages, and admin actions.
**Enhanced (2026-03-26)**: Fully mobile-optimized with responsive filtering dialogs and touch-native screenshot carousels.

### 5. Personal Tracking & Ignore ✅
Added 5-star ratings, Play Status tracking, and "Auto-Hide & Ignore" keyword logic for junk entries.

**Custom tags (2026-07-12):** Game detail pages support tenant-scoped custom tag assignment and
saved suggestions. Manual assignments survive metadata re-enrichment, while automatic genre tags
remain replaceable. Suggestions show custom definitions before system tags, and custom definitions
can be deleted inline. Cross-tenant reads, writes, suggestions, and deletion are covered by negative
tests.

### 6. Background Sync Scheduler ✅
`SyncSchedulerService` (`BackgroundService`) runs `RunFullSyncAsync()` on a configurable interval (default 6h). `GET /api/library/sync/status` endpoint exposes scheduler state. Admin UI card with live status and auto-refresh. Disabled by default via `Sync:EnableScheduler`.

**Provider refresh blockers (2026-07-11):** Cached sync, refresh-if-stale, and force refresh are
complete. The Admin Sync actions show only tenant-scoped, secret-free blockers that can change what
the user should do before a refresh: missing encrypted-vault configuration and active transient
cooldowns. They deliberately do not interpret a local successful-retrieval timestamp as evidence
that a provider has new data. Opening the check never contacts a provider, validates a credential,
or starts a refresh.

---

## 🔧 Tier 3 — Polish & Operations

### 7. CI/CD Pipeline ✅
GitHub Actions: build → test → Docker build → push to registry.

### 8. User Accounts & Multi-Tenancy ✅
Per-user game libraries with encrypted credential storage. App authentication uses local/JWT/OIDC
boundaries; platform connector credentials are stored separately in the Ingest vault.
- **Foundation (2026-06-21)**: Default-tenant migrations, claim-based tenant contexts, tenant-scoped Core and Ingest storage/queries/uniqueness constraints, tenant-isolated background jobs, local account bootstrap, optional JWT token issuance, rotating/revocable refresh sessions, API-key-controlled tenant provisioning, a frontend HttpOnly-cookie login/BFF flow, service-key-authenticated Core→Ingest tenant delegation, an AES-256-GCM tenant platform credential vault, vault-backed Steam/GOG/PSN/Epic retrieval, authenticated Core/frontend credential management, and a fail-closed credential-derived sync activation policy are implemented. Multi-user operation is not enabled by default.
- **Epic MVP boundary**: Epic now has a Legendary-inspired stored-retrieval MVP. Ingest can exchange
  an Epic authorization code for a vault-stored refresh token, refresh an access token in-memory,
  retrieve library records, and map them to the existing Epic raw-ingest shape. The Admin UI can use
  the local helper/protocol-handler handoff for the authorization-code capture, and manual entry
  remains available as a fallback. This is a pragmatic stored-retrieval integration, not an official
  Epic OAuth or production-proof provider partnership.
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
- **Provider-neutral active sessions (2026-07-30)**: Signed users can list only their active
  GameHub `AccountSession` families, revoke one other family, or revoke all families except the
  current one from the Admin Security tab. Account, tenant, and current session come exclusively
  from validated GameHub claims. One shared transactional boundary invalidates access, refresh,
  and optional local OIDC-session state while preserving account-wide password, revoke-all,
  provider-subject logout, and deletion behavior. Local and arbitrary OIDC logins behave
  identically; no provider API, Keycloak dependency, provider claim, user agent, IP address,
  device inference, dependency, configuration change, or migration was added.
- **Activation boundary**: Item #8 is technically complete, but production multi-user operation
  remains disabled after installation and upgrade until an operator deliberately satisfies the
  deployment/provider checklist, sets the guarded switch consistently, and restarts. This does not
  add public registration, unlimited SaaS readiness, tenant switching, or provider-derived
  authorization. The later optional personal-tenant JIT scope is tracked separately in item #19.

### 9. Tenant-Scoped Activity Feed ✅
- **Implemented (2026-03-24)**: `ActivityLog`, the centralized activity service, the latest-20
  activity endpoint, and the Admin activity tab are implemented. The UI auto-refreshes every 30
  seconds and covers sync, merge, split, restore, and account-provisioning operations.
- **Tenant isolation (2026-06-21)**: Activity rows are tenant-owned and query-filtered. Direct
  two-tenant read/write tests and scheduled-sync integration coverage verify that one tenant cannot
  observe another tenant's activity.
- **Boundary**: This is a private operational feed for the authenticated tenant. Social sharing is
  not implemented and is not part of the current product scope.

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
- **Current scope**: JSON V3 export, validation, transactional empty-library restore, Admin UI, and
  spreadsheet-safe CSV export are implemented. V2 adds physical copies and V3 adds an optional
  canonical GTIN per copy; supported V1 and V2 backups remain importable.
- **Multi-tenancy status**: Export and restore are scoped to the authenticated tenant while backup
  documents remain tenant-neutral. Cross-tenant isolation, active-tenant empty-library behavior,
  and physical-copy ownership are covered by tests. See [ADR-008](adr/008-data-portability-format.md).

### 13. Physical Collection — Manual-First IGDB Capture ✅
- **Phase 1 (2026-07-13)**: Tenant-owned physical-copy records with independent IDs can be added to
  an existing Unified Game, listed in detail and library payloads, filtered separately by PS3, PS4,
  or PS5, and removed with confirmation. Multiple copies of the same game/platform are valid.
- **Data invariants**: Physical copies do not extend the digital provider enum or participate in
  `LibraryItem`, sync, or `ManualLink` semantics. Composite tenant/game relationships are enforced
  in SQLite and PostgreSQL. Manual merge moves every copy to the target; manual split moves only the
  selected digital item and leaves physical copies on the original game.
- **Active capture workflow (2026-07-14)**: [ADR-014](adr/014-human-confirmed-physical-game-capture.md)
  now defines a manual-first IGDB funnel. It reuses the Fix Match provider search and result card,
  requires explicit result selection and an initially empty PS3/PS4/PS5 choice, shows a final
  summary, and writes only after confirmation.
- **Tenant-local create/reuse**: Core verifies the selected provider ID again, reuses only the
  authenticated tenant's exact `(MetadataSource, MetadataProviderId)` match, or atomically creates
  an enriched tenant-owned game plus its first physical copy. Title similarity never authorizes a
  link, another tenant's match is invisible, and repeated copies remain valid.
- **Search behavior**: Title search is debounced, cancelled, and protected by a request-generation
  guard. Loading, no-result, and sanitized provider-failure states are explicit. IGDB platform data
  is displayed only as a hint and never selects the physical platform.
- **GTIN and privacy**: Copies store a nullable, non-unique canonical GTIN-14 string with a
  tenant-appropriate lookup index. The active UI no longer offers scanning, typed GTIN, confirmed
  GTIN candidates, or external product-catalogue suggestions.
- **Portability**: Backup V3 and CSV include optional canonical GTINs. Restore accepts V1, V2, and
  V3, rejects invalid V3 values before writes, assigns ownership from authentication, and allows
  repeated GTINs.
- **Retained Phase 2B infrastructure**: A provider-neutral Core adapter keeps tenant-local confirmed
  mappings first, validates only normalized GTINs, bounds provider calls with a short timeout, and
  safely degrades across disabled/no-result/429/5xx/timeout/malformed outcomes. It and the benchmark
  harness remain default-disabled technical boundaries and are no longer wired into the active UI.
- **Provider decision**: No production provider was selected. Official-source review found the
  closest game-specific candidate (PriceCharting) has an unresolved third-party-software license
  boundary; MobyGames lacks a documented direct product-code REST lookup; generic barcode services
  publish no target-platform/region coverage. No representative dataset or paid access was
  available, so no hit-rate, accuracy, latency, or availability claim is made. See
  [Physical Product Catalogue Evaluation](physical-product-catalog-evaluation.md).
- **Benchmark and configuration**: The Docker-runnable neutral harness consumes an operator-owned
  EU/US PS3/PS4/PS5 dataset and emits sanitized measured outcomes without raw GTINs or payloads.
  Production remains `ProductCatalog:Provider=None`; there is no provider credential variable in
  this phase.
- **Experimental backlog (not a milestone commitment)**: [UPC Database](https://upcdatabase.org/api)
  may be evaluated as an opt-in, server-side **title-only** suggestion provider. Its documented
  UPC/EAN lookup and bearer-token API make it a plausible adapter experiment, but it publishes no
  verified PS3/PS4/PS5 or EU/US coverage, no reliable platform field, and no suitable evidence for
  a production selection. Any evaluation requires written usage review and a representative live
  benchmark; it is deliberately not assigned to a defined next milestone.
- **Unscheduled experimental backlog**: Barcode capture, OCR/cover recognition, provider selection,
  representative catalogue benchmarking, regional or edition data, and `PhysicalRelease` remain
  unassigned to a defined next milestone. IGDB coverage for physical editions or regions is not
  claimed.

### 14. Discovery: "Surprise Me" (Random Pick) ✅
- **Feature**: Random game selection button that respects active filters (e.g., "Random RPG I haven't finished").
- **Goal**: Help users conquer their backlog by removing decision paralysis.
- **Implemented**: The Core random-library endpoint respects active filters, and the frontend
  provides a "Surprise Me" action that navigates to the selected game.

### 15. Power-User Maintenance Tools ✅
- **Direct Enrichment ✅ (2026-07-15)**: Fix Match now keeps exact provider-ID entry separate from
  title search. Core verifies the positive provider ID before changing the tenant-owned game,
  rejects missing or mismatched provider results without altering existing metadata or custom tags,
  applies successful metadata and audit writes atomically, and returns sanitized temporary-provider
  failures. Direct cross-tenant game access fails before any provider call.
- **Advanced Pagination ✅ (2026-07-16)**: FIRST/LAST navigation, direct page jumping, and a
  mobile-friendly 25/50/100 page-size selector are implemented. Page size is URL-backed, changing
  it resets to page one without dropping filters or sorting, malformed values fall back safely, and
  Core clamps out-of-range pages to the filtered result before executing pagination.
- **Manual Split ✅**: Platform entries can be separated from a unified game into their own records.
- **Robust Sorting ✅**: Missing years and ratings are coalesced to deterministic sort positions.

### 16. Epic Authentication & Stored Retrieval ✅ MVP
- **Current behavior**: Admin Credentials can store Epic retrieval credentials through the encrypted
  Ingest vault. The primary MVP flow accepts an Epic `authorizationCode` and exchanges it
  server-side for a refresh token; the manual fallback accepts an existing refresh token. The local
  helper UX and `gamehub-helper://` protocol-handler launch path are implemented for Epic and can
  capture Epic's launcher-compatible authorization-code JSON page without asking for the user's Epic
  password.
- **Retrieval**: Core treats Epic as a stored-retrieval platform. Ingest refreshes an access token
  in-memory, persists an Epic-rotated refresh token back into the tenant-scoped encrypted Ingest
  vault before continuing, calls Epic's library service, maps records to `app_name`/`app_title`
  plus safe stable raw metadata such as catalog identifiers, developer/publisher names, release
  date, categories, description/summary, and catalog artwork URLs, and writes raw Epic items through
  the existing ingest pipeline.
- **Core/UI fallback consumption**: Core now consumes the safe Epic raw metadata as cautious
  fallbacks for existing unified-library fields (`Summary`, `CoverUrl`, `Artworks`, `Developers`,
  `Publishers`, and `FirstReleaseDate`) during sync. Existing IGDB/enriched values are preserved,
  the app-name/title matching path remains unchanged, and the existing library DTOs already expose
  these fields to the UI without a new screen or schema change.
- **Provider-failure handling**: Epic classifies authentication rejections, rate limits, unavailable
  upstream responses, and malformed provider payloads into secret-free tenant audit states. A
  single unavailable or malformed catalog record is skipped as a partial success so other Epic
  games remain ingestible; when stored retrieval fails, Core can still unify the tenant's existing
  raw Epic library and records that fallback as a partial sync result. The existing Admin audit log
  presents these states with concise operator guidance using its existing DTO surface.
- **Provider contract fixtures**: Redacted launcher-asset and catalog-metadata fixture cases cover
  supported identifier variants, optional fields, DLC, and marketplace records without making live
  Epic calls in CI. Further provider response variants should be captured as safe fixtures before
  extending parser behavior.
- **Shared provider throttle signals**: Steam, GOG, PlayStation, and Epic now share safe `429` and
  temporary `5xx` classification, including a provider-supplied `Retry-After` timestamp when present.
  The signal is audit-only; no automatic retry or token-exchange replay is introduced.
- **Tenant-scoped retrieval cooldown**: Core receives only the internal, secret-free `RetryAfterUtc`
  for stored-retrieval `429` responses and holds a transient cooldown per tenant and platform. While
  active, sync skips only the provider refresh and still unifies existing raw data; no cooldown is
  persisted or used to trigger an immediate retry.
- **Operator readiness**: The Epic stored-retrieval runbook now defines controlled live validation,
  safe audit-state response, cooldown discipline, and fixture-led payload-drift investigation. It
  deliberately does not add alerting, retry orchestration, or provider monitoring infrastructure.
- **Status boundary**: The Epic Stored Retrieval MVP works, including the local helper UX. Remaining
  follow-up scope includes continued provider-fragility handling. Do not describe this as official
  OAuth support or a fully production-proof provider integration.

### 17. Guided Platform Account Connect for GOG & PSN ✅ Local Helper MVP
- **Goal**: Replace the current manual DevTools cookie-copy workflow with a guided "Connect GOG" and
  "Connect PlayStation" experience that feels like account linking from the frontend while preserving
  the existing credential-vault boundary.
- **Preferred approach**: Use a local, user-visible browser helper/assistant for self-hosted
  deployments. The user signs in directly with GOG or PlayStation, including MFA/captcha when
  required; the helper extracts only the required session cookie/token from that controlled browser
  context and hands it to GameHub for encrypted tenant-scoped vault storage.
- **Non-goals**: Do not collect GOG/PSN usernames and passwords in a GameHub form, do not build a
  server-side hidden/headless password login flow, and do not claim official OAuth support unless
  GOG or Sony provide an appropriate public library-access authorization flow.
- **Security boundary**: Provider session material must be treated like existing platform
  credentials: plaintext may flow only through the minimal capture/upsert path, must never be
  returned by status APIs, and must remain tenant-scoped in the Ingest credential vault. This must
  preserve the tenant-isolation rules from ADR-008 and the credential-vault boundaries
  introduced for item #8.
- **Implemented helper status**: GOG local helper capture works through the installed
  `gamehub-helper://` protocol handler, the one-time loopback handoff URL, JSON payload fallback, and
  manual credential fields. PlayStation local helper capture also works, but the reliable path is the
  persistent Chrome profile exposed through loopback remote debugging. See
  [Platform Connect Helpers](platform-connect-helpers.md) for the operator runbook and
  [Platform Connect Helper Contract](gog-connect-helper-contract.md) for the detailed helper payload,
  BFF/Core upsert target, and security contract.
- **PSN validation status (2026-07-07)**: The PlayStation Admin connector should prefer a persistent
  GameHub Chrome profile exposed on loopback remote debugging, commonly
  `http://127.0.0.1:49333`, and then use the same one-time local handoff used by GOG. Real Sony
  sign-in testing showed that a helper-launched fresh Chrome profile can complete earlier sign-in,
  passkey, telemetry, and fingerprinting steps, but may still fail when Sony mints final session
  cookies with `429 Too Many Requests`. A persistent Chrome session was validated as the working path
  for reading already-issued PlayStation cookies such as `pdccws_p`. Keep the broader cookie map
  support because Sony may vary cookie names by surface or account state. The fresh
  helper-launched PSN browser remains a diagnostic/fallback path, not the recommended operator path.
- **PSN provider contract hardening (2026-07-17)**: Purchased-library page parsing now uses redacted
  contract fixtures for normal and empty pagination, the compatible alternate title identifier,
  GraphQL errors, missing structures, and pagination states that could otherwise loop. Malformed
  JSON and unsupported shapes produce one sanitized audit result without provider payload details;
  no automatic retry or credential replay was added.
- **GOG provider contract hardening (2026-07-17)**: Library statistics page parsing now uses
  redacted contract fixtures for normal and empty pagination, numeric and numeric-string game IDs,
  missing structures, invalid items, and stale or empty non-final pages. Unsupported shapes fail
  closed with one sanitized audit result instead of silently returning partial or falsely empty
  data; no automatic retry or credential replay was added.

---

## 🌐 Tier 5 — Multi-User Productization & SaaS

These items are optional follow-up work after the completed guarded self-hosted multi-user mode in
item #8. They are not prerequisites for operator-provisioned deployments and must not weaken the
existing explicit tenant-mapping, isolation, lifecycle, or default-off activation boundaries.

### 18. Controlled Registration & Invitations
- **Goal**: Replace direct operator API calls with an auditable onboarding workflow when a
  deployment intentionally permits additional users.
- **Scope**: Invitation issuance, expiry, cancellation, single-use acceptance, verified-provider
  identity binding, rate limits, abuse controls, and an operator administration surface.
- **Security boundary**: Registration must never accept a caller-selected tenant or infer ownership
  from email, organization, role, or other provider claims. Cross-tenant invitation and replay tests
  are required before this can be enabled.
- **Non-goal**: Guarded multi-user deployments remain closed and operator-provisioned by default;
  this item does not imply anonymous public signup.

### 19. JIT Provisioning & External Identity Lifecycle 🟡 JIT + Back-Channel Logout Implemented
- **Implemented (2026-06-24)**: ADR-013 adds independent default-off
  `Disabled`/`PersonalTenantJit` modes. Active guarded mode can atomically create one server-owned,
  sync-disabled personal tenant, `ExternalOidc` owner, exact issuer/subject mapping, refresh
  session, and secret-free activity entry. Account-type constraints, parallel first-login
  idempotency, deleted-identity tombstones, zero-account JIT readiness, and fresh OIDC
  reauthentication for deletion are implemented and verified on SQLite and PostgreSQL.
- **Access boundary**: Every authenticated subject of the exact configured issuer is eligible only
  when JIT is selected; Keycloak realm/client/authentication policy limits admission. Email, name,
  groups, roles, organizations, and tenant claims never select or link a GameHub tenant.
- **Back-Channel Logout (2026-07-18, session scope 2026-07-25)**: ADR-015 adds a public form-only
  BFF receiver and independent Core validation for signed standard Logout Tokens. Durable local
  session families bind access JWTs and rotating refresh rows. An issuer-bound hashed `sid`
  mapping revokes only one previously validated family; `sub` without `sid` advances the exact
  account epoch and revokes every family. Both claims must resolve consistently. Hashed tombstones
  order login/logout races, while duplicate, unknown, deleted, and already revoked targets remain
  externally indistinguishable. SQLite/PostgreSQL upgrades fail closed by revoking legacy sessions
  without a stable family reference.
- **Self-service session management (2026-07-30)**: The same local session families now have a
  provider-independent list/single-revoke/revoke-others API and Admin Security card. The feature is
  Keycloak-independent, exposes only a local stable ID, creation time, and current marker, and
  reuses the central family/refresh/OIDC-local-state revocation boundary.
- **Remaining scope**: Automated provider lifecycle integration, disabled-user events, subject
  replacement recovery, SCIM/provider-event evaluation, and missed-event reconciliation remain
  future work.
- **Security boundary**: Provider email, groups, roles, organizations, and tenant-like claims remain
  untrusted unless a future ADR defines their exact authority. Provisioning must be idempotent,
  auditable, revocable, and covered by cross-tenant isolation and lifecycle tests.
- **Dependency status**: [ADR-013](adr/013-personal-tenant-oidc-jit-provisioning.md) defines the
  implemented trust boundary. OIDC configuration alone still cannot activate provisioning.

### 20. SaaS Operations, Administration & Release Gate
- **Goal**: Define and satisfy the additional security, reliability, privacy, and operational bar
  required before claiming general SaaS readiness.
- **Scope**: Tenant administration and roles, support-access controls, global incident/session
  response, provider disabled-user events and reconciliation, audit retention, privacy and data
  deletion obligations, quotas and abuse prevention, availability targets, disaster recovery,
  key rotation, monitoring, capacity planning, and staged rollout controls.
- **Release gate**: Complete threat modeling, external security review, restore and regional-failure
  exercises, load and isolation testing, incident runbooks, and documented service ownership before
  any unrestricted SaaS claim.
- **Boundary**: This is an umbrella production gate, not evidence that public registration, JIT
  provisioning, billing, tenant switching, or enterprise administration already exist.

---

## Suggested Next Steps

| Priority | Recommendation | Rationale and boundary |
|----------|----------------|------------------------|
| **1 — Provider resilience** | Continue controlled validation for #16 and #17 | Epic, PSN, and GOG now have redacted contract-fixture baselines. Add only captured provider variants and controlled live-validation evidence before broadening behavior; do not add automatic credential replay. |
| **2 — Controlled onboarding, if product direction requires it** | Implement #18 invitations | Choose this only when the deployment should onboard additional users without direct operator API calls. Preserve the existing default-off multi-user gate and exact identity-to-tenant mapping. |
| **3 — External identity lifecycle** | Continue the remaining #19 provider lifecycle work | Personal-tenant JIT plus provider-session and subject-wide Back-Channel Logout are complete. Disabled-user events, subject replacement, and reconciliation are separate hardening milestones. |
| **4 — SaaS release gate** | Treat #20 as an umbrella program, not the next feature | Threat modeling, external review, recovery exercises, monitoring, privacy obligations, quotas, and operational ownership are required before any general SaaS claim. |

The physical barcode/catalogue experiments in #13 remain unscheduled until usage permission,
provider access, and a representative verified dataset exist. They should remain evidence-gated
rather than becoming the default next milestone.
