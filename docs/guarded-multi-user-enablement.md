# Guarded Multi-User Operations

This guide describes how to prepare, enable, verify, disable, roll back, and recover GameHub's
guarded production multi-user mode. The normative decision is
[ADR-012](adr/012-guarded-multi-user-enablement.md). OIDC protocol details remain in
[OIDC Authentication Architecture](oidc-authentication-architecture.md). The independent JIT
decision is [ADR-013](adr/013-personal-tenant-oidc-jit-provisioning.md). Provider-initiated session
termination is defined in [ADR-015](adr/015-oidc-back-channel-logout.md).

## State model

| State | Meaning | Activates provisioned tenants? |
|---|---|---:|
| OIDC disabled | No provider login/link path | No |
| OIDC enabled | Provider protocol can be used for the default tenant and explicit linking | No |
| Readiness satisfied | Local technical/data prerequisites pass | No |
| Multi-user switch enabled | Operator explicitly requested the mode | Only if readiness also passes |
| Multi-user active | Switch enabled and readiness satisfied | Yes |

`GAMEHUB_OIDC_ENABLED=true` never substitutes for
`GAMEHUB_MULTI_USER_ENABLED=true`. Missing multi-user configuration means `false` on both a new
installation and an upgrade.

## Deployment variables

Do not store values in the repository. Configure them in the deployment's secret/configuration
system without changing a checked-in or local `.env` file.

| Variable | Purpose |
|---|---|
| `GAMEHUB_MULTI_USER_ENABLED` | Explicit operator switch; default `false` |
| `GAMEHUB_FRONTEND_AUTH_ENABLED` | Enables BFF GameHub session handling; must be `true` before multi-user activation |
| `GAMEHUB_AUTH_COOKIE_SECURE` | Requires HTTPS cookie transport; must be `true` |
| `GAMEHUB_OIDC_ENABLED` | Separately enables OIDC; must be `true` |
| `GAMEHUB_OIDC_PROVISIONING_MODE` | `Disabled` or explicit `PersonalTenantJit`; default `Disabled` |
| `GAMEHUB_OIDC_ISSUER` | Exact HTTPS issuer |
| `GAMEHUB_OIDC_CLIENT_ID` | Provider client and ID-token audience |
| `GAMEHUB_OIDC_CLIENT_SECRET` | Optional provider confidential-client credential |
| `GAMEHUB_OIDC_REDIRECT_URI` | Exact external HTTPS callback |
| `GAMEHUB_OIDC_SCOPES` | Scopes including `openid` |
| `GAMEHUB_OIDC_COOKIE_KEY` | Independent 32-byte Base64 BFF transaction-cookie key |
| `GAMEHUB_OIDC_BFF_SERVICE_KEY` | Independent BFF-to-Core OIDC trust key |
| `GAMEHUB_OIDC_METADATA_ADDRESS` | Optional exact HTTPS discovery address |
| `GAMEHUB_OIDC_ALLOWED_ALGORITHMS` | Explicit asymmetric algorithm allowlist |
| `GAMEHUB_JWT_SIGNING_KEY` | Independent GameHub session-signing key |
| `GAMEHUB_API_KEY` | Strong operator key for disabled-mode provisioning and operator readiness |
| `GAMEHUB_INGEST_SERVICE_KEY` | Independent Core-to-Ingest tenant-delegation key |
| `GAMEHUB_CREDENTIAL_KEY_VERSION` | Active platform-vault key version |
| `GAMEHUB_CREDENTIAL_KEY_1` | Base64 AES-256 vault key for the configured version |

The Compose mapping sends the same switch to Core, Ingest, and Frontend and mirrors BFF auth/cookie
state into Core for startup comparison. Custom deployments must preserve that exact agreement.

## Safe initial enablement

Choose exactly one provisioning model before following the activation checklist:

- **Operator provisioning plus explicit first-link:** keep
  `GAMEHUB_OIDC_PROVISIONING_MODE=Disabled`, provision every owner while the gate is off, and link
  each account explicitly.
- **Personal-tenant JIT:** set `GAMEHUB_OIDC_PROVISIONING_MODE=PersonalTenantJit`, keep the guarded
  switch off until every technical prerequisite is ready, then activate the gate. The initial
  database may contain zero accounts; the first validated login creates one personal tenant.

Both options remain closed deployments. JIT access is controlled by Keycloak realm, client, and
authentication policies; it is not public registration.

1. Upgrade all components with `GAMEHUB_MULTI_USER_ENABLED=false` or absent.
2. Confirm Core, Ingest, and Frontend start normally and apply existing migrations.
3. Configure and verify OIDC, HTTPS callback routing, Secure cookies, JWT signing, independent BFF,
   operator, delegation, and vault keys.
4. In operator mode, bootstrap the default account and provision every intended tenant. In JIT
   mode, leave a new database empty or retain upgraded accounts; do not fabricate a bootstrap row.
5. In operator mode, link every account through the local compatibility credential. In JIT mode,
   verify Keycloak admission policy and the zero-account readiness state instead.
6. Sign in through OIDC and exercise tenant-scoped library, credential, sync, backup, restore,
   session revocation, and account deletion behavior in a non-production environment.
7. Call `GET /api/auth/multi-user/status` with the configured operator API key. Confirm
   `readinessSatisfied=true`, `multiUserEnabled=false`, `multiUserActive=false`, and no blockers.
8. Back up Core and Ingest databases and retain external vault/signing keys under the deployment's
   recovery policy.
9. Set `GAMEHUB_MULTI_USER_ENABLED=true` consistently for Core, Ingest, and Frontend, then restart.
10. Confirm all services start and the status response reports `multiUserActive=true`.

An enabled Core process refuses to start if a live account lacks the configured issuer link. Keep
the switch false while repairing provisioning/linking readiness.

## Bootstrap and first-link flow

The API-key provisioning request contains tenant name and initial owner credentials but no tenant
ID. Core generates the ID and starts platform sync disabled. Provisioning is rejected while the
switch is enabled, so the operator uses a maintenance disable/link/re-enable flow. While disabled,
submitting provisioned-account local credentials has only two safe outcomes:

- **Unlinked account:** a short-lived `oidc_link_only` access cookie is issued, no refresh session is
  created, and only `POST /api/auth/oidc/link` is authorized.
- **Already linked account:** local login returns `local_login_not_allowed` and creates no session.

A pending-deletion account is a separate lifecycle exception: valid local confirmation can issue
only a ten-minute `account_deletion_only` token accepted on the exact deletion retry endpoint. It
cannot authorize normal APIs or refresh.

The provider callback cannot choose tenant, account, role, or organization. Core takes the link
destination only from the signed link-purpose GameHub JWT and stores the validated exact
issuer/subject. After success, the BFF deletes the restricted cookie and redirects to the login
page for a fresh OIDC session.

## Personal-tenant JIT flow

JIT operates only when OIDC validation succeeds, `PersonalTenantJit` is selected, and guarded mode
is active. Unknown identities otherwise receive the same generic authentication failure.

Core generates every tenant/account identifier and transactionally creates the sync-disabled
tenant, `ExternalOidc` owner, exact mapping, refresh session, and secret-free tenant activity row.
Provider email is optional profile data and is not used; role, group, organization, and tenant
claims are ignored. Unique issuer/subject ownership makes parallel first login idempotent, and a
losing transaction cannot leave an orphan tenant.

With JIT selected, zero accounts do not add `no_active_account` to readiness. As soon as live
accounts exist, every one must still have an exact mapping for the configured issuer. Pending
deletion remains excluded. Readiness performs no writes.

## Normal login and local compatibility

When active, new full sessions come only from OIDC. Core independently validates the provider ID
token and exact mapping, then issues its own GameHub JWT/refresh pair. Provider access tokens are
not used for GameHub APIs.

When disabled, the default tenant retains the historical local password and optional OIDC paths.
A provisioned tenant cannot obtain a full local or OIDC session, but an unlinked provisioned
account can still obtain the restricted link-purpose token so rollout preparation remains possible.

Local password hashes remain stored for rollback compatibility and current-password confirmation
on existing lifecycle operations. They are not production login credentials.

## Provider-initiated account logout

Configure the provider's Back-Channel Logout URL as the public BFF route
`https://<gamehub-host>/api/auth/oidc/backchannel-logout`. A valid signed Logout Token resolves only
the exact persisted `(issuer, subject)`, advances that account's GameHub session epoch, revokes all
of its refresh sessions, and writes a secret-free tenant activity entry. Existing access JWTs then
fail their next live database check instead of remaining usable until expiry.

The current implementation is deliberately account-wide. It requires `sub` and does not use an
optional Keycloak `sid` as a tenant or account selector. Duplicate and unknown identities return
the same successful provider response. Invalid messages return 400; temporary discovery/JWKS/Core
failures return 503 so the provider can retry.

## Disable and rollback

To stop admitting provisioned tenants, set `GAMEHUB_MULTI_USER_ENABLED=false` consistently and
restart all components.

- No database migration is rolled back.
- No tenant, account, external identity, refresh session, platform credential, raw item, or library
  row is changed or deleted.
- New OIDC login for provisioned non-default tenants is rejected.
- Background tenant jobs run only for the default tenant.
- Existing access JWTs and refresh sessions continue under ADR-011 until expiry or revocation.
- Account deletion retries and pending-deletion blocking continue normally.

For an incident requiring immediate session termination, use existing account-scoped revocation or
deletion controls. The enablement switch is intentionally not a destructive global session control.

## Failure and recovery behavior

| Condition | Result |
|---|---|
| Invalid switch value | Component startup/configuration fails closed |
| Core/BFF switch disagreement | Core startup fails |
| Enabled without JWT/OIDC/Secure cookies/frontend auth | Core startup and BFF authentication fail closed |
| Missing/weak/reused operator, delegation, JWT, or BFF trust | Core startup fails |
| Enabled Ingest without delegation or vault encryption key | Ingest startup fails |
| Live account missing issuer link | Enabled Core startup fails; disabled Core reports blocker |
| Provider discovery/JWKS/token outage | New login/link fails; no claim/email fallback |
| Provider signing-key rotation | One bounded metadata refresh, then normal validation |
| Provider recovery | Restores only the same issuer/subject; never remaps tenant |
| Invalid Back-Channel Logout Token | Rejected with 400; no session or tenant state changes |
| Back-channel discovery/JWKS/Core outage | Retryable 503; no unsigned or claim-based fallback |
| Core→Ingest trust mismatch/outage | Delegated credential/sync/deletion operations fail closed |
| Pending deletion | Ordinary JWT, refresh, OIDC login, and linking remain blocked |

Successful provider login does not prove provider availability forever. Existing GameHub sessions
are locally issued and follow their configured expiry, database live-account check, rotation,
replay detection, and revocation behavior. A later accepted Back-Channel Logout advances the live
session epoch and revokes the account's refresh sessions independently of browser state.

## Readiness response

`GET /api/auth/multi-user/status` requires the operator API-key scheme and sets `Cache-Control:
no-store`. It reports only:

- `oidcEnabled`
- `oidcProvisioningMode` (`disabled` or `personal_tenant_jit`)
- `readinessSatisfied`
- `multiUserEnabled`
- `multiUserActive`
- `localPasswordMode`
- stable blocker codes

Current blocker codes include:

- `jwt_not_configured`
- `oidc_not_configured`
- `operator_api_key_not_ready`
- `ingest_trust_not_ready`
- `frontend_auth_not_enabled`
- `frontend_secure_cookies_not_enabled`
- `trust_secrets_not_independent`
- `no_active_account`
- `active_account_missing_oidc_link`
- `ingest_multi_user_mode_mismatch`
- `ingest_credential_vault_not_ready`
- `ingest_unavailable_or_trust_mismatch`

The response never includes secrets, configured URLs/values, tokens, provider claims, identities,
tenant data, account data, counts, credential status, or library state. No browser admin panel is
provided because this is a deployment-operator control, not a tenant-facing setting.

Core derives the Ingest blockers by calling a separate delegation-only internal endpoint. That
endpoint returns only Ingest's switch and vault-ready booleans, queries no tenant rows, and verifies
the real service-key trust path without returning the key or credential metadata.

## Audited implementation paths

The guarded rollout reviewed and directly tested:

- API-key bootstrap and operator tenant provisioning;
- local credential verification and restricted first-link token issuance;
- OIDC state, nonce, PKCE, exact callback, provider token exchange, Core validation, exact mapping,
  and claim non-selection;
- GameHub JWT validation, live account checks, refresh rotation/replay/revocation, pending deletion,
  and account deletion retry;
- Next.js login UI, HttpOnly/Secure cookies, OIDC callback cleanup, BFF Core proxy, and refresh;
- Core tenant context, default JWT policy, API-key operator exceptions, background tenant runner,
  scheduled sync, and enrichment;
- Core→Ingest tenant delegation, Ingest default authorization policy, credential vault readiness,
  and sync activation;
- tenant-scoped backup/restore and upgrade preservation;
- SQLite and real PostgreSQL migrations preserving the default tenant, local account hash,
  explicit external identity, refresh session, unified game, and library item.
- Back-Channel Logout form handling, BFF-only forwarding, signature/protocol validation, hashed
  replay detection, account session epochs, refresh revocation, tenant-owned activity, and
  cross-tenant negative behavior.

Guarded enablement itself adds no dependency. ADR-015 adds an automatically applied additive
SQLite/PostgreSQL migration for account session epochs and short-lived hashed logout replay markers.

## Deployment checklist

- [ ] Switch is false during upgrade and preparation.
- [ ] Provider issuer/client/callback and asymmetric algorithms are exact.
- [ ] The exact public Back-Channel Logout URL is registered and account-wide logout is exercised.
- [ ] Provider MFA, recovery, verification, abuse, and administrator policies are reviewed.
- [ ] Frontend auth and Secure cookies are enabled behind correctly configured HTTPS proxying.
- [ ] Operator, BFF, JWT, delegation, cookie, provider, and vault keys are independent.
- [ ] Every live account is operator-provisioned and explicitly linked.
- [ ] Readiness reports no blockers before activation.
- [ ] Cross-tenant negative tests and provider-outage exercises pass in staging.
- [ ] Core/Ingest backups and external key recovery are tested.
- [ ] SQLite or PostgreSQL upgrade/rollback procedure is rehearsed.
- [ ] Logs and metrics exclude secrets, tokens, passwords, authorization codes, and unnecessary PII.
- [ ] The switch is changed consistently across all components in one controlled deployment.
- [ ] Disable and session-revocation incident procedures are documented for operators.

## Known limits and non-goals

Guarded enablement plus optional ADR-013 JIT does not add public registration, invitations,
multiple owners/roles per tenant, provider-derived authorization, account switching, tenant
switching, unlink, subject replacement, provider disabled-user event reconciliation, or a
production admin console. It does not claim unrestricted SaaS readiness. These remain separate
design and security decisions.
