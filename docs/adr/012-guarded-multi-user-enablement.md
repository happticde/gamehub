# ADR-012: Guarded Production Multi-User Enablement

**Date:** 2026-06-22
**Status:** Accepted
**Deciders:** @jankahnt

## Context

GameHub now has tenant-scoped storage, background execution, portability, account lifecycle,
encrypted platform credentials, provider-neutral OIDC, and local GameHub sessions. Those
capabilities are necessary but are not permission to expose multiple provisioned tenants in a
production deployment.

OIDC configuration proves that an external provider can authenticate an explicitly linked
identity. It does not prove that an operator has reviewed tenant readiness, cookie transport,
Core/Frontend agreement, internal delegation, credential encryption, provider policy, recovery,
or rollout behavior. Inferring multi-user activation from OIDC would turn a provider setup change
into an authorization-mode change.

The local account path also cannot become a production bypass. It has no public recovery authority,
MFA, verified-email policy, or abuse-control system. At the same time, an operator-provisioned
account needs one narrowly bounded way to establish its first explicit OIDC mapping.

## Decision

### Independent state model

GameHub distinguishes four states:

1. **OIDC enabled:** the provider protocol and ID-token trust configuration are available.
2. **Readiness satisfied:** technical configuration is complete and every live account has an
   explicit mapping for the configured issuer.
3. **Multi-user enabled:** the operator explicitly set `MultiUser:Enabled=true` (deployment variable
   `GAMEHUB_MULTI_USER_ENABLED=true`) in Core, Ingest, and the Next.js BFF.
4. **Multi-user active:** the explicit switch is true and readiness is satisfied.

OIDC enabled never implies multi-user enabled. The switch defaults to `false` when absent on a new
installation or upgrade. Only the literal values `true` and `false` are accepted.

The switch is read once during process startup into immutable server state. Request bodies, query
parameters, headers, cookies, provider claims, browser storage, tenant IDs, organization claims,
groups, and roles cannot change it.

### Technical prerequisites

Before active mode is allowed, all of the following are mandatory:

- GameHub JWT signing is configured with the existing minimum-strength checks.
- Core OIDC is enabled with exact HTTPS issuer/audience/metadata, asymmetric algorithm allowlist,
  and a valid BFF service key.
- Next.js authentication and Secure cookies are enabled.
- Core's mirrored Frontend authentication, Secure-cookie, and multi-user states agree with the BFF.
- A strong operator API key is configured for explicit provisioning and readiness operations.
- A separate strong Core-to-Ingest tenant-delegation key is configured.
- Operator API key, Ingest service key, GameHub JWT signing key, and OIDC BFF service key are
  independent values.
- Ingest has the same explicit multi-user switch, a valid delegation key, and an active AES-256-GCM
  platform-credential encryption key.
- Core reaches Ingest's secret-free internal readiness handshake through the real delegation key;
  mode disagreement, trust mismatch, outage, malformed response, or missing vault readiness blocks
  activation.
- The Core database has at least one live account.
- Every live account has an explicit `(issuer, subject)` link for the configured issuer.
- Pending-deletion accounts remain excluded from readiness and remain blocked by lifecycle policy.

Core and Ingest abort startup when the operator enables the mode without their prerequisites. BFF
authentication routes return a generic unavailable response for an invalid enabled configuration.
The disabled mode remains startable so an operator can bootstrap, provision, link, diagnose, or
roll back safely.

### Operational prerequisites

Configuration validation cannot prove provider governance or deployment operations. The operator
must additionally verify:

- the provider's MFA, recovery, account lifecycle, abuse controls, and administrative access;
- exact external HTTPS callback and reverse-proxy scheme/host handling;
- backup and restore exercises for both Core and Ingest data plus external credential-vault keys;
- login, link, logout, refresh, revocation, deletion retry, provider outage, and signing-key rotation;
- SQLite or PostgreSQL upgrade and rollback procedures appropriate to the deployment;
- log/metric retention that does not capture secrets, tokens, authorization codes, or unnecessary
  personal data;
- explicit provisioning ownership and an inventory of accounts expected to be linked.

### Authentication allowed in active production mode

| Boundary | Allowed behavior |
|---|---|
| User login | OIDC only, resolving an exact persisted issuer/subject mapping |
| GameHub API access | GameHub JWT through the BFF; existing valid sessions retain ADR-011 behavior |
| Refresh | Existing rotating GameHub refresh sessions continue under local expiry/revocation rules |
| Core operator API key | Guarded readiness; bootstrap/provisioning also require a deliberate disabled-mode maintenance window |
| Ingest access | Core's internal tenant-delegation scheme only for ordinary protected endpoints |
| Local password | Never creates a normal production session in active mode; disabled-mode preparation may issue one ten-minute, non-refreshable OIDC-link-only token for an unlinked provisioned account; pending deletion may issue a deletion-retry-only token |

The link-only JWT is accepted exclusively by `POST /api/auth/oidc/link`. The deletion-retry JWT is
accepted exclusively by `DELETE /api/auth/account`. Neither can authorize
libraries, backup/restore, credentials, sync, lifecycle, tenant provisioning, roles, or another
route (apart from the deletion token's exact lifecycle retry). After linking, the BFF clears the
restricted cookie and requires a fresh OIDC login.
A linked account's local password cannot issue another GameHub session.

The password hash remains stored for upgrade/rollback compatibility and current-password
confirmation on existing destructive lifecycle operations. This retention is not permission to use
the password as production login. The authenticated password-change endpoint may maintain that
compatibility credential but does not widen its active-mode authorization.

### Provisioning and mapping

There is no public registration and no OIDC just-in-time provisioning. The API-key-authenticated
operator endpoints remain the only account/tenant creation paths. Tenant IDs are generated by
Core; request and provider data cannot select them. New tenants start platform sync disabled.
Bootstrap and provisioning return a conflict while the guarded switch is enabled. Operators must
use a controlled maintenance flow: disable, restart, provision/link, re-check readiness, enable,
and restart. This preserves the all-live-accounts-linked invariant for every active process.

The first mapping flow is:

1. With the switch disabled, the operator explicitly provisions the account/tenant.
2. The account submits its local bootstrap credential to Core.
3. If the configured issuer is not already linked, Core returns only a short-lived link-purpose JWT.
4. The BFF performs OIDC Authorization Code + PKCE and calls the exact linking endpoint.
5. Core validates both trust channels and persists only the exact issuer/subject mapping to the
   signed account/tenant.
6. The BFF deletes the bootstrap cookie; the user signs in again through OIDC.

Email, verified-email, role, group, organization, or tenant-like provider claims are ignored for
mapping. A provider identity cannot select, replace, or assume another GameHub tenant.

### Disabled mode and later deactivation

Turning the switch off is non-destructive and does not rewrite or delete tenants, accounts,
external identities, refresh sessions, credential ciphertext, raw platform rows, or libraries.

- New OIDC login for a provisioned non-default tenant is rejected generically.
- The default tenant keeps its historical local/OIDC compatibility behavior.
- Existing GameHub access JWTs and refresh sessions continue until their normal local expiry or
  revocation boundary, as selected in ADR-011. Deactivation is therefore a graceful admission
  stop, not instantaneous global logout.
- Background sync and enrichment visit only the default tenant while disabled. Provisioned tenant
  data and policies remain stored but dormant.
- Pending deletion remains retryable and deleted accounts remain unavailable.

If immediate session termination is operationally required, operators must use existing scoped
revocation/account lifecycle controls or a separately planned global incident procedure. Changing
the guarded switch alone intentionally does not destroy session or tenant state.

### Upgrade, rollback, recovery, and outages

This decision introduces no database schema migration. Existing default tenants, local password
hashes, external identities, refresh sessions, credentials, raw records, and libraries remain in
their existing tables. SQLite and real PostgreSQL upgrade tests run to the latest migrations and
then validate enabled readiness without mutating those rows.

Safe rollout is: upgrade with the switch absent/false, start services, provision/link/verify,
inspect readiness, back up, then set the switch true across all components and restart. Safe
rollback is: set it false everywhere and restart. Database rollback is not required for the gate.

Provider discovery/JWKS/token outages fail closed for new login and linking. Existing GameHub
sessions retain ADR-011 local validity, database live-account checks, refresh rotation, and
revocation. Provider recovery restores only the same linked issuer/subject and cannot remap a
tenant. Lost GameHub signing, BFF, delegation, cookie, or vault keys follow their existing recovery
boundaries; the gate does not invent a secret fallback.

### Observability and operator readiness

Core exposes `GET /api/auth/multi-user/status` only through the configured operator API-key scheme.
The response contains booleans, a local-password mode label, and stable blocker codes. It contains
no configured values, secrets, tokens, account identifiers, account counts, tenant identifiers,
tenant names, provider claims, or credential status.

Core obtains only two safe Ingest process booleans through
`GET /api/internal/multi-user/readiness`, which itself requires tenant delegation. This handshake
queries no tenant rows or credentials and proves that the configured Core key reaches an Ingest
process with matching mode and a usable vault key.

Startup logs record only enabled/disabled states and safe blocker codes. Authentication logs use
generic failures and existing structured server-selected identifiers where lifecycle/audit
diagnostics require them. Secrets, ID/access/refresh tokens, authorization codes, passwords,
decrypted credentials, and unnecessary provider profile data must never be logged.

No browser readiness dashboard is added. Readiness is an operator deployment concern and exposing
it through ordinary browser JavaScript would add no authorization value. The BFF exposes only the
minimum login/link presentation state needed for the current flow.

## Consequences

### Positive

- Production activation is an explicit, fail-closed operator action separate from OIDC.
- Local passwords cannot become a normal production login bypass.
- Provisioning and first linking remain possible without public registration or email matching.
- Disabling and re-enabling preserve all tenant-owned and authentication data.
- Core, Ingest, BFF, background jobs, and direct API-key compatibility paths enforce the same
  rollout boundary.

### Costs and limitations

- One issuer/client remains supported per deployment.
- One owner account per tenant remains the current model.
- There is no public registration, JIT provisioning, account invitation, unlink, subject
  replacement, provider back-channel logout, or provider-event session revocation.
- Switch deactivation does not immediately revoke already-issued GameHub sessions.
- Operator API-key provisioning remains an administrative compatibility interface rather than a
  complete production admin console.
- Readiness confirms local configuration/data invariants, not provider uptime or governance.
- This is guarded self-hosted multi-user enablement, not a claim of unrestricted SaaS readiness.

## References

- [ADR-008: Data Portability Format](008-data-portability-format.md)
- [ADR-009: Tenant Platform Credential Vault](009-tenant-platform-credential-vault.md)
- [ADR-010: Initial Account Lifecycle](010-account-lifecycle.md)
- [ADR-011: Production Authentication with OIDC](011-production-oidc-authentication.md)
- [Guarded Multi-User Operations](../guarded-multi-user-enablement.md)
