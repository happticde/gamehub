# Backend Agent Context

## Architecture Details
Two ASP.NET Core microservices:
- **Core (`services/core/`)**: Unified library, slug matching, enrichment (IGDB), sync orchestration.
- **Ingest (`services/ingest/`)**: Raw data fetching from Steam, GOG, PSN, IGDB.
- Ingest raw/audit rows are tenant-owned and filtered by the trusted authentication claim.
  Existing rows belong to the default tenant; arbitrary tenant IDs are never accepted in bodies.

## Design Patterns
- **IngestControllerBase<T>** — generic base with shared upsert/pagination; platform controllers only implement JSON key extraction.
- **Slug-based matching** — `UnificationService.GenerateSlug()` normalizes titles for cross-platform deduplication.
- **ManualLinks** — user overrides for merge/split that persist across syncs.
- **AuthenticationHandler<T>** — API key auth using ASP.NET's pipeline, not raw middleware.

## Configuration
- Auth: `Authentication:ApiKey` in appsettings.json or `Authentication__ApiKey` env var. Missing keys fail closed. Anonymous local services require both Development and `Authentication:AllowAnonymousLocalDevelopment=true`, incompatible with JWT/frontend/OIDC/multi-user authentication. Production API/service keys require 32–512 printable non-space ASCII characters. `Authentication:TenantId` maps the API key to a trusted tenant claim. Setting `Authentication:Jwt:SigningKey` (minimum 32 UTF-8 bytes) enables JWT validation and token issuance. `Authentication:Jwt:RefreshTokenLifetimeDays` controls rotating opaque refresh sessions (default 30, range 1–90).
- Auth attempt limits use hashed normalized-email, refresh-token, provider-token, or trusted account/tenant buckets after bounded model binding. They never use browser-selected forwarding headers or a shared BFF source IP. A process-wide concurrency budget protects verification work.
- Physical product catalogue: `ProductCatalog:Provider` accepts only `None` until ADR-014's
  permission and representative benchmark gate is satisfied. `ProductCatalog:TimeoutMilliseconds`
  defaults to 1500 and accepts 250–5000. No catalogue credential exists in Phase 2B. Local
  tenant-confirmed GTIN mappings always suppress an external call; failures return no hint and do
  not block manual capture. The adapter and GTIN lookup remain technical infrastructure but are not
  exposed by the active manual-first IGDB physical-capture UI.
- Guarded production multi-user mode: `MultiUser:Enabled` defaults to `false` and is independent
  from OIDC. Active mode requires validated OIDC/JWT, mirrored Frontend auth/Secure-cookie/switch
  state, strong independent operator/delegation/JWT/BFF trust, Ingest vault readiness, and an
  explicit configured-issuer link for every live account. See ADR-012.
- Core→Ingest delegation: Core reads `Services:IngestServiceKey`; Ingest validates the matching `Authentication:ServiceKey`. Only this internal key may authenticate `X-GameHub-Tenant-Id`. Normal API keys cannot select a tenant through request headers.
- Platform credential vault: Ingest reads versioned Base64-encoded 32-byte AES keys from `Credentials:Keys:{version}` and selects the write key with `Credentials:ActiveKeyVersion`. Empty keys disable writes; invalid key-ring configuration fails startup. Vault endpoints require the internal tenant-delegation scheme and never return plaintext. Steam/GOG/PSN/Epic use strict schema-v1 payloads; Core triggers the service-key-only stored-retrieval route before exporting raw data.
- Epic ingestion: Epic has a small Legendary-inspired MVP. The Admin UI accepts an Epic
  authorization code and Ingest exchanges it server-side for a vault-stored refresh token, or accepts
  a manual refresh-token fallback. Retrieval refreshes an access token in-memory and maps Epic
  library records to the existing `app_name`/`app_title` raw-ingest shape. No Epic access or refresh
  token is returned by status APIs or browser JavaScript after storage.
- Credential management: Authenticated clients use Core's `/api/platform-credentials` endpoints;
  Core alone delegates the tenant and forwards credential bodies to Ingest. Responses contain only
  configuration status and timestamps. `PlatformSyncEnabled` is never client-settable.
- DB: `Database:Provider` (`Sqlite`/`Postgres`), connection strings in `ConnectionStrings` section.
- Tenant-owned game relationships: Library items, manual links, and enrichment audit rows reference
  games through `(TenantId, UnifiedGameId)`. PostgreSQL enforces composite foreign keys; SQLite
  migrations preserve existing tables and enforce the same insert/update boundary with triggers.
- Physical game copies are separate from digital `LibraryItem` providers. They support only PS3,
  PS4, and PS5, allow duplicates, and reference games through the same composite tenant/game
  boundary in both SQLite and PostgreSQL. Confirmed IGDB capture re-verifies the provider ID and
  reuses only an exact tenant-local metadata identity, or atomically creates the enriched game and
  first copy; title similarity and cross-tenant matches are never linkage authority.
- Tag assignments are explicit tenant-owned join rows with composite foreign keys to both the game
  and tag. SQLite and PostgreSQL upgrades preserve existing assignments and re-home any legacy
  cross-tenant tag name into the game's tenant before enforcing the boundary.
- Custom tag assignments are marked on the tenant-owned join row. Provider enrichment replaces
  only automatic genre assignments; custom assignments and unassigned tag definitions remain
  durable so tenant-local suggestions survive edits.
- Tag definitions also retain their custom/system origin. Suggestion reads expose that category and
  usage count within the active tenant; deletion accepts only custom definitions and cascades their
  tenant-owned assignments. Provider-created system definitions are protected from deletion.
- Core→Ingest URL: `Services:IngestUrl` (default `http://localhost:5000`).
- Provisioned tenants start platform-sync disabled. Core enables them only after a fresh vault
  status confirms at least one Steam, GOG, PSN, or Epic credential. Deletion disables first and only
  re-enables after another supported credential is verified; failures remain disabled.
- Account lifecycle: password changes require the current password and revoke all refresh
  sessions. Explicit global revocation is tenant/account-claim scoped. Account deletion persists
  a fail-closed pending state, delegates idempotent full Ingest cleanup (including encrypted
  credentials), then transactionally deletes Core data. See ADR-010.
- Pending deletion blocks ordinary JWT access and refresh rotation. Only the signed deletion route
  remains retryable. The local compatibility path still has no email/password recovery; configured
  OIDC delegates recovery to its trusted provider. Multi-user operation remains disabled.
- Sync Scheduler: `Sync:EnableScheduler` (bool, default `false`), `Sync:IntervalMinutes` (int, default `360`). Scheduled cycles use refresh-if-stale plus cached sync and never force providers.
- Provider freshness: `Sync:ProviderFreshnessMinutes` (int, default `30`). The last complete provider retrieval is persisted per tenant/platform in operational `SyncMetadata`; cached sync never contacts providers, partial/failed retrievals do not advance freshness, and force refresh still respects active cooldowns.
- Sync scheduler results are tenant-indexed in memory. The status endpoint resolves the active
  tenant through `ITenantContext`; only enabled configuration and the next-cycle time are shared.
- Roadmap #8 isolation, lifecycle, production authentication/recovery, and guarded enablement are
  complete. The switch remains default-off and operator-controlled. See
  `docs/tenant-isolation-audit.md` and `docs/guarded-multi-user-enablement.md`.
- Production authentication: optional provider-neutral OIDC uses Authorization Code + PKCE in the
  Next.js BFF and independent ID-token validation in Core. Core pins exact issuer, audience,
  asymmetric algorithms, nonce, lifetime, and JWKS, then resolves only a persisted issuer/subject
  mapping. Email and provider tenant/role claims never select an account. See ADR-011.
- OIDC Back-Channel Logout: the public BFF accepts only one form-encoded `logout_token` and forwards
  it over the independent BFF service-key boundary. Core independently validates signature,
  issuer, audience, algorithm, freshness, `jti`, event shape, optional exact type, absent nonce,
  and standard `sub`/`sid` shape before using either identifier. Normal access JWTs and rotating
  refresh rows share a durable `AccountSession`. An issuer-bound SHA-256 `sid` mapping revokes one
  previously validated tenant-bound family; `sub` without `sid` advances the account epoch and
  revokes all families. Both claims must map consistently. Hash-only tombstones order login/logout
  races without allowing `sid` to select an account or tenant directly. See ADR-015.
- Active session management uses only the existing provider-neutral `AccountSession`. Authenticated
  account/tenant/current-family claims can list the minimal local ID/creation/current shape, revoke
  one owned family, or revoke all other families without advancing the epoch. A central
  transactional revoker closes the family, refresh rows, and optional local OIDC mapping for every
  logout path. No provider claim, API, SDK, Keycloak integration, user agent, IP, or device metadata
  participates.
- OIDC provisioning: `Authentication:Oidc:ProvisioningMode` accepts only `Disabled` (default) or
  `PersonalTenantJit`. JIT requires active guarded mode and atomically creates a server-identified,
  sync-disabled tenant, `ExternalOidc` owner, exact mapping, refresh session, and secret-free
  tenant activity. Deleted external identities are tombstoned and cannot JIT-recreate a tenant.
- Account kinds are explicit. `Local` rows require email, normalized email, and password hash;
  `ExternalOidc` rows have no normalized email or password hash. Local authentication, password
  change, and local recovery/deletion confirmation reject external accounts.
- The learning-oriented implementation walkthrough, sequence diagrams, trust boundaries, failure
  matrix, and source-file map live in `docs/oidc-authentication-architecture.md`.
- External identities are linked only from a live signed GameHub account and use a composite
  `(TenantId, UserAccountId)` foreign key. OIDC requires secure cookies and complete HTTPS
  configuration; failures close. Local accounts remain a non-production compatibility path.
- In active mode, ordinary Core `[Authorize]` policies require JWT and ordinary Ingest policies
  require tenant delegation. API keys remain only on explicitly declared operator endpoints.
  Disabled-mode rollout may create only a short-lived OIDC-link token for an unlinked account;
  active-mode bootstrap/provisioning is rejected and linked accounts cannot use passwords for
  production login. Pending deletion has a separate deletion-retry-only token.
- `TenantJobRunner` enumerates provisioned tenants only while the immutable switch is enabled.
  Disabling the mode preserves their data and policies but keeps scheduled sync/enrichment dormant;
  the default compatibility tenant continues its historical local background behavior.
- `GET /api/auth/multi-user/status` is API-key-operator-only and secret-free. It exposes safe state
  and blocker codes, never configured values, secrets, identities, tenant data, or counts. Core
  uses the normal delegation key to query Ingest's internal mode/vault booleans, so mismatch,
  outage, or trust failure also blocks readiness.
- External account deletion starts a BFF-owned OIDC intent with `prompt=login` and `max_age=0`.
  Core requires a recent signed `auth_time` and exact mapping, then issues only an
  `account_deletion_only` token accepted on `DELETE /api/auth/account`.

## Conventions
- All controllers have `ILogger` injection with structured logging (`{Platform}`, `{Count}`, etc.).
- XML doc comments (`/// <summary>`) on all public members.
- Comments explain *why*, not *what*.
- Tests follow pattern: `MethodName_Scenario_ExpectedResult`.
