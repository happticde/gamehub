# ADR-013: Opt-in Personal-Tenant OIDC JIT Provisioning

**Date:** 2026-06-24
**Status:** Accepted
**Deciders:** @jankahnt

## Context

ADR-010 through ADR-012 deliberately require operator provisioning and an explicit first-link flow.
That model is appropriate for closed deployments whose operator creates every tenant in advance,
but it makes a Keycloak-authenticated personal-library deployment administratively expensive.

GameHub needs an optional path in which a completely validated, previously unknown OIDC
`(issuer, subject)` can receive one new personal tenant and one owner account. Enabling OIDC is not
sufficient authority for account creation, and provider profile or authorization claims must not
become GameHub tenant selectors.

## Decision

### Provisioning modes

Core has one explicit, immutable OIDC provisioning mode:

- `Authentication:Oidc:ProvisioningMode=Disabled` (default)
- `Authentication:Oidc:ProvisioningMode=PersonalTenantJit`

Compose maps `GAMEHUB_OIDC_PROVISIONING_MODE` to that setting and defaults it to `Disabled`.
Missing configuration therefore preserves operator-only provisioning. Any non-empty unknown value
aborts Core startup instead of being guessed or silently treated as enabled.

Provisioning mode is independent from `Authentication:Oidc:Enabled`. `PersonalTenantJit` can create
nothing unless OIDC is fully configured and guarded multi-user mode is active. Turning OIDC on by
itself or leaving `MultiUser:Enabled=false` preserves the generic unknown-identity failure.

Operator provisioning and explicit first-link remain supported. JIT is an additional opt-in path,
not a replacement or an upgrade-time conversion.

### Provisioning authority

In `PersonalTenantJit` mode, every subject authenticated by the one exact configured issuer may
receive one personal GameHub tenant. Access restrictions belong at Keycloak's realm, client, and
authentication-policy boundaries. GameHub does not reinterpret unchecked email, email-verification,
name, role, group, organization, or tenant-like claims as an allowlist or authorization decision.

Core generates the tenant ID, account ID, mapping ID, refresh-session ID, and all ownership
relationships. No browser value, request field, provider claim, or provider identifier chooses a
GameHub identifier or an existing tenant. New personal tenants start with
`PlatformSyncEnabled=false`.

### Account kinds and local credentials

`UserAccount` has an explicit kind:

- `Local` accounts retain canonical email, normalized email, and a usable ASP.NET password hash.
- `ExternalOidc` accounts have no normalized email and no password hash. Provider email is optional
  profile data only and may be absent; it never participates in lookup, uniqueness, linking, or
  authorization.

Existing rows migrate to `Local`. Local bootstrap, operator provisioning, explicit first-link,
password login, password change, and rollback compatibility remain unchanged for those accounts.
Local password authentication, password change, and local recovery/deletion confirmation reject
`ExternalOidc` accounts. Equal provider email claims never merge accounts.

Database check constraints and a filtered unique normalized-email index enforce these invariants on
SQLite and PostgreSQL. The one-owner-per-tenant unique relationship remains unchanged.

### Atomic and idempotent creation

After Core has independently validated signature, issuer, audience, authorized party, lifetime,
nonce, algorithm, subject, and JWKS, it first performs the exact external-identity lookup.

For a previously unknown identity, JIT creates the following in one Core database transaction:

1. a server-identified personal tenant;
2. one `ExternalOidc` owner account;
3. the exact `(issuer, subject)` mapping;
4. one normal rotating GameHub refresh session; and
5. one tenant-owned, secret-free activity entry.

The access JWT is derived from the committed server-owned account. The unique `(Issuer, Subject)`
index is the final concurrency authority. If first-login requests race, a losing transaction is
rolled back in full and returns the same generic authentication failure unless it can safely load
the mapping committed by the winner. It cannot leave an orphan tenant, account, session, or
activity row.

Logs record only safe outcome categories. They do not include subjects, provider tokens, provider
emails, tenant/account identifiers, or claim values for JIT decisions.

### Readiness and guarded admission

`Disabled` readiness retains ADR-012: at least one live account must exist and every live account
must have an exact mapping for the configured issuer.

`PersonalTenantJit` permits a new database with zero accounts to satisfy the data portion of
readiness because the first successful provider login is the provisioning event. All Core, BFF,
Ingest, OIDC, JWT, Secure-cookie, independent-key, delegation, and vault prerequisites remain
mandatory. Once any live account exists, every live account must still have an exact mapping for
the configured issuer. Pending-deletion accounts remain excluded and blocked. Readiness is
read-only and never provisions data.

The operator status may return only the stable labels `disabled` or `personal_tenant_jit` in
addition to its existing safe booleans and blocker codes. It returns no configured value,
identifier, count, profile field, or claim.

### Deletion, disable, rollback, and provider failure

External accounts cannot use a dummy password or skip reauthentication. The BFF starts a dedicated
OIDC deletion transaction, requests `prompt=login` and `max_age=0`, and retains state, nonce, PKCE,
exact callback, and sealed-cookie checks. Core repeats normal ID-token validation, requires a recent
signed `auth_time`, and requires the exact subject mapping to the account being deleted.

Core then issues a short-lived `account_deletion_only` GameHub token. Bearer validation accepts that
token only for `DELETE /api/auth/account`; it cannot access library, backup, credentials, sync,
password, provisioning, or any other lifecycle route. The BFF consumes it server-to-server for the
existing idempotent deletion operation. A separate retry intent can obtain the same narrow token
for an already pending external account after another fresh provider authentication.

Deletion writes a durable tombstone for each deleted external `(issuer, subject)` before cascading
the account mapping. JIT checks this tombstone, so a fully deleted identity remains blocked instead
of silently creating a new tenant. No unlink or subject-replacement behavior is implied.

Changing provisioning mode to `Disabled`, disabling OIDC, or disabling guarded mode is
non-destructive. It creates, deletes, or rewrites no tenant, account, mapping, session, credential,
raw record, or library row. Provider discovery, JWKS, token, or authentication outages fail closed
for new login, JIT, reauthentication, and retry. Existing GameHub sessions keep their local expiry
and revocation behavior.

User-triggered GameHub logout uses RP-Initiated Logout to terminate the current Keycloak browser
SSO session after local refresh-session revocation and cookie cleanup. Keycloak-initiated or
administrative logout, disabling, or deleting a Keycloak user does not immediately revoke already
issued GameHub sessions because Back-Channel Logout and provider-event propagation remain
unsupported. Immediate GameHub termination outside the GameHub sign-out flow still requires
account-scoped revoke-all or account deletion.

## Consequences

### Positive

- A closed Keycloak deployment can opt into low-administration personal-library onboarding.
- OIDC, provisioning, guarded admission, and provider access policy remain separate decisions.
- Account credentials and database constraints make the local/external boundary explicit.
- Concurrent first login is transactional and cannot create partial personal tenants.
- External deletion receives provider-grade fresh reauthentication without inventing a password.
- Disabling the capability is a reversible admission change rather than a data migration.

### Costs and limitations

- One OIDC issuer/client is supported per deployment.
- Each tenant still has exactly one owner account; shared memberships, invitations, roles, and
  tenant switching are not implemented.
- Realm/client/authentication policy is the access-control boundary for who may reach JIT. GameHub
  does not consume provider groups or roles as authorization.
- There is no SCIM or provider-event synchronization, subject replacement, provider lifecycle
  reconciliation, or Keycloak-initiated Back-Channel Logout. RP-Initiated browser logout is
  implemented without changing JIT or tenant selection.
- Provider disable/delete is not an immediate GameHub session-revocation signal.
- This is opt-in personal-tenant provisioning for guarded self-hosted operation. It is not public
  registration and does not establish general SaaS readiness.

## References

- [ADR-010: Initial Account Lifecycle](010-account-lifecycle.md)
- [ADR-011: Production Authentication with OIDC](011-production-oidc-authentication.md)
- [ADR-012: Guarded Production Multi-User Enablement](012-guarded-multi-user-enablement.md)
- [Guarded Multi-User Operations](../guarded-multi-user-enablement.md)
- [OIDC Authentication Architecture](../oidc-authentication-architecture.md)
