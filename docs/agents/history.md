# Session History

## Current Session Summary (2026-07-12)

### What Changed

**Tenant-Scoped Custom Tags:**
- Added custom tag editing to the game detail page with tenant-local saved suggestions, reusable
  names, validation, immediate add/remove persistence, and support for removing assignments without
  deleting suggestion definitions.
- Marked manual tag assignments independently from provider-generated genres so re-enrichment can
  replace stale automatic tags without removing a tenant's custom tags.
- Added SQLite/PostgreSQL migrations plus direct update, enrichment-preservation, migration-default,
  and cross-tenant read/write isolation tests.

### Known State / Pending

- Saved suggestions include both provider-generated and user-created names within the active tenant;
  there is no separate tag administration or deletion screen.
- No dependency, `.env`, authentication-flow, or production configuration change was introduced.

## Current Session Summary (2026-07-11)

### What Changed

**Provider Refresh Blockers:**
- Added a tenant-scoped, secret-free Core endpoint for the only actionable pre-refresh blockers:
  missing platform credentials and active cooldowns. It never contacts a provider or exposes
  credentials, tokens, tenant identifiers, provider errors, or external health claims.
- The Admin Sync actions show those blockers inline before a platform refresh and reload them after
  cached, refresh-if-stale, and force sync actions. The UI deliberately does not infer that a
  provider has new data from a local successful-retrieval timestamp.

**Cached Sync and Provider Freshness:**
- Split the fast cached Ingest-to-Core sync from refresh-if-stale and forced provider refresh modes.
- Added persistent tenant/platform provider freshness with a configurable 30-minute default; only
  complete retrievals advance freshness, while force refresh continues to honor cooldowns.
- Made the scheduler explicitly use refresh-if-stale and updated the Admin UI with separate fast,
  slow, and force actions plus secret-free per-platform outcomes.
- Preserved content-hash source identity/fingerprints, tenant/auth boundaries, and Backup V1's
  exclusion of operational sync state.

## Current Session Summary (2026-07-09)

### What Changed

**Epic Stored Retrieval Operations:**
- Added an Epic-specific operator runbook for controlled live validation, safe responses to existing
  audit states, tenant/platform cooldown discipline, and fixture-led payload-drift investigation.
- Kept the scope documentation-only: no new retries, metrics, alerts, provider health checks,
  credentials, schema, or authorization flow were introduced.

**Epic Raw Metadata Mapping:**
- Extended Epic stored-retrieval raw mapping with safe stable top-level metadata from launcher
  assets and catalog metadata, including catalog identifiers, developer/publisher names, release
  date, description/summary, category paths, and catalog artwork URLs when present.
- Preserved existing `legendary_asset`/`legendary_metadata` raw debug fields and kept Core sync
  compatibility on the existing `app_name`/`app_title` shape.
- Added mapping, missing-optional-field, existing skip-logic, and no-secret-promotion tests.

**Epic Raw Metadata Core Fallbacks:**
- Added a schema-free Core sync fallback that consumes safe Epic raw metadata only when existing
  unified-library fields are empty, covering summary, cover/artwork URLs, developers, publishers,
  and first release date.
- Preserved existing enriched metadata and the Epic `app_name`/`app_title` unification path.
- Added export and unification tests for fallback population, enriched metadata preservation,
  missing optional fields, and stable Epic app-name linking.

**Epic Stored Retrieval Resilience:**
- Added safe Epic audit classifications for authentication rejection, rate limiting, unavailable
  upstream responses, and malformed provider payloads without recording provider response details.
- A failed catalog metadata lookup now skips only that record and records `PartialSuccess`; Core
  also exports existing tenant-scoped raw data when stored retrieval fails and marks its sync audit
  as partial rather than dropping the whole platform sync.
- Added direct Epic client and Core sync regression coverage for partial catalog failure, rate
  limiting, malformed responses, sanitized audit data, and the tenant-owned fallback path.

**Epic Audit Status UI:**
- The existing Admin ingest audit log now renders the safe retrieval states as readable labels with
  concise operator guidance for partial success, expired credentials, rate limits, unavailable
  providers, and malformed responses on both mobile and desktop layouts.
- The UI reuses the existing tenant-scoped audit DTO and does not add browser access to credentials,
  tokens, or provider response details.

**Epic Provider Contract Fixtures:**
- Added redacted, version-controlled launcher-asset and catalog-metadata contract cases for normal,
  compatible identifier, optional-field, DLC, and marketplace response variants.
- The mapper regression test reads these fixtures without any live Epic request, so provider-shape
  changes are explicit test updates rather than unobserved runtime assumptions.

**Shared Provider Throttle Signals:**
- Added a reusable Ingest transport failure type for safe rate-limit, temporary-upstream, and other
  failed-response classification across Steam, GOG, PlayStation, and Epic.
- `Retry-After` dates and delays are normalized to UTC for tenant audit messages when providers send
  them. The shared signal does not retry requests and never retains response content or credentials.

**Tenant-Scoped Retrieval Cooldown:**
- Ingest now returns only a safe `RetryAfterUtc` body for internal stored-retrieval rate limits; Core
  maps it to a typed exception and stores a transient cooldown by tenant and platform.
- Active cooldowns skip the next provider refresh while preserving existing raw-data export and
  unification. The state expires in memory, is isolated across tenants/platforms, and is never used
  to replay a request or persist provider data.

### Known State / Pending

- Epic remains the existing Legendary-inspired MVP, not official OAuth support or a production-proof
  provider integration. Core/UI now has a small fallback consumption path for safe raw metadata, and
  the first provider-resilience slice is implemented; continued provider-fragility handling remains
  follow-up scope.
- No `.env`, dependency, production configuration, schema, or new platform-auth flow change was
  introduced.

## Current Session Summary (2026-07-07)

### What Changed

**Epic Refresh Token Rotation Persistence:**
- Added tenant-scoped Epic refresh-token rotation persistence after a successful stored-retrieval
  refresh response. Rotated refresh tokens are written back through the encrypted Ingest credential
  vault before Epic library retrieval can continue.
- Preserved the Epic local-helper/Auth-Code MVP boundary and avoided adding a new platform auth
  flow. Stored-retrieval responses, status DTOs, logs, and audit messages remain secret-free.
- Added direct vault, cross-tenant, Epic client, and stored-retrieval failure-path tests for
  rotation, no-rotation, tenant/platform isolation, encrypted storage, and sanitized errors.

### Known State / Pending

- Epic remains a provider-fragile MVP and should not be described as official OAuth support or a
  fully production-proof provider integration. Richer Epic metadata mapping remains follow-up scope.
- No `.env`, dependency, production configuration, Core/BFF auth-flow, or schema change was
  introduced.

## Current Session Summary (2026-07-07)

### What Changed

**PSN Persistent Chrome Admin UX:**
- Kept the PlayStation Admin credential connector's one-click helper launch as the primary
  "Connect PlayStation" action.
- Added the validated persistent Chrome loopback remote-debugging handoff as a guided fallback for
  Sony fresh-profile or HTTP 429 failures.
- Added a secret-free `http://127.0.0.1:49333/json/version` reachability check that displays only
  the Chrome product string and ignores local debugger URLs or provider-looking fields.
- Preserved the existing one-time handoff URL, JSON payload fallback, manual cookie fallback, and
  Core/Ingest encrypted vault upsert path.

### Known State / Pending

- No credential-vault, platform-auth, provider-password, `.env`, or production configuration change
  was introduced.
- PSN remains provider-fragile; the persistent Chrome path is the recommended operator flow.

## Current Session Summary (2026-07-07)

### What Changed

**Platform Connect Helper Documentation:**
- Documented the local Epic, GOG, and PlayStation helper flows as the current project state.
- Added an operator runbook covering CLI build, macOS `gamehub-helper://` installation, Admin UI
  usage, handoff URL, JSON payload, manual fallback fields, troubleshooting, and secret-handling
  boundaries.
- Updated Roadmap items #16 and #17 to mark Epic Stored Retrieval with local helper UX as an MVP,
  GOG local helper capture as working, and PlayStation local helper capture as working through the
  recommended persistent Chrome loopback remote-debugging path.
- Clarified that fresh helper-launched PSN browsers remain a diagnostic/fallback path because Sony
  sign-in can reject fresh profiles or return HTTP 429 during final session-cookie minting.

### Known State / Pending

- No new platform authentication flow, provider OAuth claim, credential-vault refactor, or code
  change was introduced.
- PSN remains provider-fragile; operators should prefer the persistent Chrome remote-debugging path
  and keep manual fallbacks available.

## Current Session Summary (2026-06-30)

### What Changed

**Guided GOG/PSN Account Connect Roadmap:**
- Captured the product intent to replace manual DevTools cookie extraction with a guided
  "Connect GOG" and "Connect PlayStation" experience.
- Added Roadmap item #17 for a local, user-visible browser helper/assistant that lets the user sign
  in directly with GOG or PlayStation, handles provider MFA/captcha in the provider UI, extracts only
  the required session cookie/token from that controlled browser context, and stores it through the
  existing encrypted tenant-scoped credential vault.
- Documented the boundary that GameHub should not ask for GOG/PSN passwords in its own forms, should
  not build a hidden server-side headless password-login flow, and should not claim official OAuth
  support unless GOG or Sony provide an appropriate public library-access authorization flow.
- Preserved manual credential entry as the fallback path, with GOG as the recommended first vertical
  proof of concept and PSN as a follow-up once the helper contract is stable.

### Known State / Pending

- No implementation was added in this session.
- Future implementation must preserve existing credential-vault secrecy, tenant isolation, and
  status-API no-plaintext boundaries.

## Current Session Summary (2026-06-24)

### What Changed

**Opt-in Personal-Tenant OIDC JIT Provisioning:**
- Added ADR-013 and independent `Disabled`/`PersonalTenantJit` provisioning configuration. Missing
  configuration stays disabled, unknown values fail startup, and JIT requires active guarded mode;
  OIDC alone or a disabled gate creates nothing.
- Added explicit `Local` and `ExternalOidc` account kinds. Existing rows migrate to `Local`;
  external accounts have no normalized email or password hash. SQLite/PostgreSQL constraints and a
  filtered local-email index enforce the credential boundary.
- Unknown validated subjects can atomically receive one server-identified, sync-disabled personal
  tenant, external owner, exact mapping, refresh session, and secret-free tenant activity. Unique
  mapping constraints and transaction rollback prevent duplicate or orphan rows under parallel
  first login.
- Completed external deletion persists an exact identity tombstone, preventing JIT recreation.
  Fresh BFF OIDC reauthentication uses `prompt=login`, `max_age=0`, nonce/PKCE/state, and recent
  signed `auth_time`; Core issues only a short-lived deletion-purpose token for the exact DELETE
  route. Pending cleanup has a fresh OIDC retry path.
- JIT readiness may be satisfied with zero accounts when every technical prerequisite passes.
  Once live accounts exist, every live account still requires an exact configured-issuer mapping.
  Status exposes only the safe provisioning-mode label.

### Verification

- Direct tests cover default-off/invalid configuration, gate-off non-provisioning, exact mapping,
  claim/email non-selection, server IDs, sync-disabled tenants, atomic activity/session creation,
  parallel first login, tombstones, local credential rejection, restricted deletion, and read-only
  readiness.
- Provider-specific EF migrations and SQLite/real PostgreSQL fresh/upgrade tests preserve the
  default tenant, local email/password hash, external identity, refresh session, unified game,
  library item, and pending-deletion state while enforcing account-kind constraints.
- Core and Ingest builds pass with zero warnings; the full .NET suite passes 258 tests and the BFF
  security suite passes 15 tests. Frontend production build and changed-file lint pass. Repository
  lint still reports only the pre-existing errors in unchanged frontend files.

### Known State / Pending

- `GAMEHUB_MULTI_USER_ENABLED` and personal JIT remain default-off. No production configuration or
  provider instance was changed.
- Public registration, shared tenants, invitations, roles, SCIM/provider-event lifecycle,
  subject replacement, and back-channel logout remain unimplemented. This is not a general SaaS
  readiness claim.

## Current Session Summary (2026-06-22)

### What Changed

**Guarded Production Multi-User Enablement:**
- Added ADR-012 and an operator guide defining OIDC/readiness/enablement as separate states. The
  immutable `GAMEHUB_MULTI_USER_ENABLED` switch defaults to false and must agree across Core,
  Ingest, and Frontend; OIDC configuration alone never activates provisioned tenants.
- Core startup now validates complete JWT/OIDC trust, mirrored BFF auth/Secure-cookie/switch state,
  strong independent operator/delegation/JWT/BFF keys, at least one live account, and an explicit
  configured-issuer link for every live account. Ingest independently requires tenant delegation
  and an active credential-vault key. Invalid enabled configuration fails closed.
- Active-mode ordinary Core authorization is JWT-only and ordinary Ingest authorization is
  tenant-delegation-only. The API key remains limited to explicitly declared operator endpoints.
- Local passwords cannot issue normal production sessions. During disabled-mode preparation, an
  unlinked operator-provisioned account can obtain only a ten-minute, non-refreshable JWT accepted
  on the exact OIDC link POST; the BFF clears it after linking and requires a fresh OIDC login.
  Active-mode provisioning is rejected until an operator performs a maintenance disable.
- Disabling admission is non-destructive. Provisioned tenants cannot begin new login, their
  background sync/enrichment stays dormant, and all tenant/account/identity/session/credential/raw
  and library data remains intact. Existing GameHub sessions retain ADR-011 expiry/revocation.
- Added an operator API-key-only, no-store readiness endpoint that reports safe states/blocker codes
  without secrets, configured values, account/tenant data, counts, claims, credentials, or tokens.

**Verification:**
- Added direct configuration, local-bypass, link-purpose, readiness, non-mutation, claim
  non-selection, background-admission, and BFF fail-closed tests.
- The full .NET suite passes with 246 tests; BFF security passes with 10 tests. SQLite and three
  real PostgreSQL migration tests preserve default tenant, account hash, explicit identity,
  refresh session, unified game, and library item through latest-schema readiness validation.
- No dependency or database migration was added.

### Known State / Pending

- Roadmap item #8 is technically complete. Production multi-user operation remains disabled until
  an operator deliberately completes the provider/deployment checklist, sets the switch, and
  restarts all components.
- Public registration, JIT provisioning, multi-owner roles, tenant switching, provider-derived
  authorization, and unrestricted SaaS readiness remain explicit non-goals.

## Previous Session Summary (2026-06-21)

### What Changed

**Production OIDC Authentication Gate:**
- Added a detailed source-oriented architecture guide with protocol concepts, trust boundaries,
  linking/login/session sequence diagrams, validation order, data model, failure matrix, test map,
  debugging guidance, and a provider integration checklist. Security-critical OIDC source paths
  now explain the reasoning behind each validation layer inline.
- Added ADR-011 selecting provider-neutral OIDC Authorization Code Flow with PKCE while keeping
  recovery, MFA, email verification, and provider credentials at the trusted identity provider.
- Next.js now owns state, nonce, PKCE, exact callback checks, HTTPS discovery, and an encrypted
  HttpOnly/Secure/SameSite correlation cookie; Core independently validates issuer, audience,
  signing keys, lifetime, nonce, and an asymmetric algorithm allowlist.
- External identities use exact issuer/subject mappings to one account and tenant. Linking requires
  a current GameHub session; email, roles, organizations, and tenant-like claims never select or
  create a mapping. Pending-deletion/deleted accounts remain blocked.
- SQLite and real PostgreSQL upgrade tests preserve existing local accounts, refresh sessions,
  default tenant, unified games, and library items. Direct BFF/Core tests reject state, nonce,
  redirect, PKCE, issuer, audience, algorithm, provider-outage, email-collision, and cross-tenant
  attacks.

**Initial Account Lifecycle:**
- Added ADR-010 covering operator-controlled provisioning, authenticated password changes,
  account-wide refresh-session revocation, the no-email-provider recovery boundary, deletion,
  partial failures, and the explicit non-activation of multi-user mode.
- Password changes verify the current password and atomically revoke every refresh session.
  Explicit global revocation is scoped to the signed account/tenant pair.
- Account deletion enters a durable fail-closed state, disables sync and revokes sessions, then
  idempotently deletes all Ingest data including encrypted credential rows before transactionally
  removing Core user-owned data, identity, and provisioned tenant rows.
- Direct two-tenant tests cover password/session isolation, stale refresh tokens, exclusive
  deletion, repeated deletion, Ingest failure/retry, encrypted credential cleanup, and preserved
  default-tenant upgrades on SQLite and real PostgreSQL.

**Remaining Core Tenant-Isolation Audit:**
- Audited user-owned Core API, service, sync, background-job, backup, credential-policy, and
  identity/session paths, with a durable inventory in `docs/tenant-isolation-audit.md`.
- Added direct two-tenant coverage for ActivityLog, SyncAuditLog, and SyncMetadata reads and writes.
- Fixed `/api/library/sync/status`: per-tenant results, error text, running state, and processed counts
  can no longer leak through the formerly global singleton status.
- Added SQLite and real PostgreSQL upgrade tests proving that legacy operational history/cursors are
  preserved and assigned to the default tenant by the existing tenant-foundation migrations.
- Marked the Roadmap #8 isolation gate complete. The lifecycle and production-authentication work
  above followed that audit; guarded multi-user enablement remains open.

**Core/Frontend Platform Credential Management:**
- Core now exposes authenticated, tenant-scoped credential status, upsert, and deletion endpoints
  while forwarding secrets only to Ingest's internal service-key-protected vault API.
- `PlatformSyncEnabled` is server-derived: a verified Steam, GOG, or PSN vault credential enables
  sync; Epic cannot activate it, and deleting starts by disabling sync before any Ingest call.
- Delete and verification failures remain fail-closed, and cross-tenant tests prove that changing
  one tenant's credential policy cannot activate or deactivate another tenant.
- The Admin dashboard has a credential vault tab. Secret fields are cleared after successful
  storage and the browser receives only configuration status and timestamps.
- Immediate retrieval and tenant-scoped audit history now run through Core's trusted delegation;
  the frontend no longer sends platform credentials or a global API key directly to Ingest.

**Epic Product Scope:**
- Epic remains a fully manual Ingest flow through API or CLI. GameHub does not store Epic
  credentials and does not perform automatic Epic stored retrieval.
- Core continues to synchronize Epic raw data that was ingested manually.
- A future Epic authentication and stored-retrieval integration is tracked separately in Roadmap
  item #16; it is not unfinished work in the current credential-management scope.

**Tenant Isolation Verification:**
- Direct cross-tenant controller tests now prove that personal tracking cannot mutate another
  tenant's game, merge cannot use a foreign source or target, and split cannot detach another
  tenant's library item.
- Backup export and restore were confirmed to already use the active tenant boundary. Existing
  tests cover non-leaking exports, restore alongside another tenant's data, tenant-owned writes,
  and equivalent slugs/platform identifiers.
- The scheduled-sync cycle is independently testable without hosted-service delays. Its integration
  test covers isolated per-tenant scopes, tenant-owned activity/sync audit rows, and a disabled
  tenant policy skip alongside a successful tenant in the same cycle.
- The background-enrichment batch is independently testable as well. A two-tenant integration test
  proves that the batch limit is applied per tenant and that enriched games and audit rows retain
  the correct owner without starving later tenants.
- Core now enforces tenant-aware game relationships below the query-filter layer. Library items,
  manual links, and enrichment audits use composite tenant/game relationships in the EF model and
  PostgreSQL; SQLite uses equivalent insert/update guard triggers in its upgrade-safe migration.
- Cross-tenant writes are rejected for all three relationships in both newly created schemas and
  databases upgraded from the previous migration.
- Tag-to-game assignments are now tenant-owned join rows with composite game/tag foreign keys.
  SQLite and PostgreSQL upgrade tests prove that existing assignments survive, legacy cross-tenant
  rows are retained through tenant-local tags, and new cross-tenant relations are rejected.
- ADR-008 and Roadmap item #8 reflect that tenant-scoped portability is implemented. The remaining
  isolation audit and initial account lifecycle were subsequently completed in this session.

### Known State / Pending

- Roadmap item #8 now has complete isolation, lifecycle, and production OIDC authentication/recovery
  gates. Explicit guarded multi-user enablement and rollout review remain open; multi-user operation
  is still disabled and must not be advertised as production-ready.
- Future Epic authentication and stored retrieval are independently tracked in Roadmap item #16.

## Previous Session Summary (2026-06-20)

### What Changed

**Tenant Platform Credential Vault:**
- Ingest owns tenant-scoped encrypted platform credentials with a unique `(TenantId, Platform)`
  boundary.
- AES-256-GCM uses a fresh 96-bit nonce, full 128-bit tag, tenant/platform-bound associated data,
  and versioned external keys for rotation.
- Internal service-key-only endpoints provide secret-free status, encrypted upsert, and deletion.
- SQLite/PostgreSQL-compatible migration and encryption, spoofing, validation, and cross-tenant
  isolation tests were added.

**Vault-backed Platform Retrieval:**
- Steam, GOG, and PSN credentials now use strict schema-v1 payloads and are decrypted only for
  immediate fetcher use; Epic stored retrieval remains unsupported.
- Core asks Ingest to refresh each supported platform through an internal service-key-only route
  before exporting the tenant's raw catalogue. Missing credentials preserve existing raw-data sync.
- Steam's secret-bearing query URL is excluded from default HttpClient logging, and upstream
  errors/responses no longer expose secret-bearing details.
- Stored retrieval has service-key authorization, spoofing, schema, and cross-tenant tests.

### Known State / Pending

- Core/frontend credential management and the explicit sync-activation policy were completed in
  the following session.

## Last Session Summary (2026-05-25)

### What Changed

**Global Navigation Live Search:**
- **New Component**: `frontend/components/GlobalSearchBar.tsx` — SLEEK glassmorphic live autocomplete input that queries the unified game library debounced by 300ms. Shows cover art, rating, release year, and colored platform badges.
- **Navbar Integration**: Placed in the top-middle for desktop view (`md:`), taking up centered space.
- **Mobile Responsive Dialog**: Collapses into a touch-friendly search icon button next to the theme toggle. Tapping it opens a clean, modern full-screen `Dialog` to perform live search.
- **Keyboard Power-User Navigation**: Supported global keyboard hotkeys (`/` and `⌘K` / `Ctrl+K`) to focus, arrow keys (`ArrowUp`/`ArrowDown`) to select, and `Enter` to navigate to detail pages or full search page.
- **Always Present**: Search bar remains visible on all pages (landing page `/`, library list `/library`, stats `/stats`, admin dashboard `/admin`, and game details page `/library/[id]`).

### Known State / Pending
- Fully compiled and typechecked locally on host Mac architecture with zero errors.

---

## Previous Session Summary (2026-05-02)

### What Changed

**MCP Server (#11 — ✅ Complete):**
- **New project**: `services/mcp/GameHub.Mcp.csproj` — stdio MCP server console app using `ModelContextProtocol` v1.2.0.
- **CoreApiClient**: Typed HTTP client proxying calls to the Core REST API (`http://localhost:5001`).
- **LibraryTools**: 7 MCP tools with rich descriptions for AI model understanding:
  - `search_library` — paginated, filterable search
  - `get_game` — full game details by ID
  - `get_random_game` — random pick with optional filters
  - `get_statistics` — comprehensive library stats
  - `list_tags` — genre tag list
  - `set_play_status` — update play status (with validation)
  - `set_rating` — set personal star rating (1–5)
- **`.vscode/mcp.json`**: Auto-detected VS Code MCP config for GitHub Copilot.
- **`docs/mcp.md`**: Client setup guide for VS Code, Claude Desktop, and Cursor.
- **Roadmap**: Marked #11 as ✅.

### Known State / Pending
- Build pending (devcontainer was offline during session — verify with `dotnet build services/mcp/GameHub.Mcp.csproj`).
- No DB migrations needed (MCP proxies Core, no direct DB access).
- Next steps: User Accounts (#8) or Data Portability (#12).

---

## Previous Session Summary (2026-03-31)

### What Changed

**"Surprise Me" (Random Pick):**
- **Backend**: Added `GET /api/library/random` to `LibraryController.cs`. Reused `BuildLibraryQuery` to ensure filters are respected.
- **Frontend**: Added a "Surprise Me" (Shuffle) button to `LibraryFilters.tsx` that fetches a random game ID and redirects the user.

**Power-User Maintenance Tools:**
- **Robust Sorting**: Updated `LibraryController.cs` to coalesce null ratings/dates to the bottom of the list.
- **Direct IGDB Match**: Updated `IgdbClient.cs` to support direct ID queries when a numeric string is entered in the "Fix Match" dialog.
- **Advanced Navigation**: 
    - Added "First" and "Last" page jumps to `LibraryPagination.tsx`.
    - Added a direct page selection dropdown (HTML `<select>`) for instant jumping.
    - Implemented a **floating bubble** pagination UI (sticky, compact pill) at the bottom-center of the library view.

### Known State / Pending
- Manual Split was deferred to its own task.
- Build verified (Backend 0 errors, Frontend TypeChecks pass).

---



## Previous Session Summary (2026-03-26)

### What Changed

**Frontend & Mobile UX:**
- **Mobile-First Refactor**: Implemented `LibraryFilters` as a responsive `Dialog` on mobile, freeing up screen space for game covers.
- **Screenshot Carousel**: Replaced static vertical screenshot lists with a touch-native horizontal carousel (`ScreenshotCarousel.tsx`) using CSS scroll-snap.
- **Admin Dashboard**: Enabled tab wrapping for the statistics and management dashboard on narrow screens.
- **iOS Viewport Fix**: Added `userScalable: false` to the global layout to prevent unwanted browser zooming on mobile inputs.
- **Build Recovery**: Rescued the frontend build after a PWA-related dependency corruption by performing a clean `npm ci` natively inside the Linux devcontainer.

**Backend & Integration:**
- **Manual Match Override**: Finalized integration of the "Fix Match" UI with the `SearchManyAsync` provider logic, allowing users to manually correct IGDB mismatches.

### Known State / Pending
- Mobile UI is now fully responsive and tested with a production build.
- PWA implementation was deferred/reverted to prioritize build stability and mobile layout quality.
- Next steps: Implement User Authentication (#8) or the MCP Server integration (#11).

## Previous Session Summary (2026-03-24)

### What Changed

**Backend (`services/core/`):**
- **IGDB Search Robustness**: `IgdbClient` now generates multiple candidate variations (specific to general) and uses them sequentially as fallbacks if the primary search fails. Base-name fallback retained.
- **Manual Metadata Override (ADR-007)**: Added `SearchManyAsync` to `IGameMetadataProvider` for retrieving raw search results from providers. Added `GET /api/library/enrichment/search` and `POST /api/library/{id}/enrichment/override` endpoints to `LibraryController` to support manual matching.

**Frontend (`frontend/`):**
- **Fix Match Dialog**: Created `FixMatchDialog.tsx` component to allow users to search IGDB from the Game Detail page, pick the correct result, and forcefully override the game's metadata.
- **UI Enhancements**: Resolved UI scrolling issues for long search result lists within `DialogContent` by enforcing flexbox constraints.

### Known State / Pending
- Manual override dialog is fully functional and overrides `MetadataProviderId`.
- Search fallbacks significantly reduce mismatches from editions or beta tags.
- Remaining roadmap: MCP Server (#11), User Accounts (#8).
