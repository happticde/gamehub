# Transactional MCP grant admission

[#94](https://github.com/happticde/gamehub/issues/94) supplies the reusable database barrier for
[#71](https://github.com/happticde/gamehub/issues/71), following
[Architecture Decision Record (ADR) 016](adr/016-mcp-user-authorization.md).
[Model Context Protocol (MCP)](https://modelcontextprotocol.io/) consent and usable production
issuance remain closed. This package adds no startup registration, endpoint, configuration,
dependency or migration. It does not complete browser consent or enable Docker user login.

## Caller contract

`McpGrantAdmission` requires the caller's transaction on the same `CoreContext`. Keep that scoped
context and transaction through pending-code binding, OpenIddict verification/consumption, or the
sensitive database write. Commit before releasing a buffered response. On a null result, exception,
failed verification or unsuccessful protocol response, roll back; dispose the scoped context so
tracked approval rows cannot be saved by later work. Never commit a partial approval.

`CreateAsync` accepts only a successful, normal browser authentication ticket from the explicit
GameHub bearer scheme. The future endpoint must obtain it using `AuthenticateAsync` for that
scheme, after the independent backend-for-frontend service boundary. A constructed ticket or
`HttpContext.User` alone is not cryptographic verification. The service rejects restricted
purposes, competing identities, duplicate owner claims and inconsistent subject/account claims.
Account, tenant, browser family and epoch come from this authenticated ticket, never a form.

Pass requested scopes only from the OpenIddict-validated pending device request. Read is required;
tracking writes require both the original request and explicit user consent. The service persists
only those approved application permissions and retains requested `offline_access` in the protocol
authorization. It creates an independent family, owned authorization and immutable grant in one
transaction, with the original 64-bit epoch and a thirty-day absolute end. The existing provider
row is selected through the browser family and must have a live owner-matching external identity.
A password family with an external link elsewhere cannot approve. No provider identifier is input.

`AdmitAsync` accepts the original, already protocol-validated principal and the token type selected
by the server operation. It checks exact resource/presenter, original consent, optional required
permission, owner/epoch, absolute expiry, provider marker, family, authorization and live token.
Access-token validation expresses the resource as an audience; device and refresh principals
retain resource metadata. Admission requires exactly the configured resource in that token type's
native representation, with no missing-resource fallback.
Device redemption, rotating refresh and access-backed sensitive writes use this same boundary.
The returned immutable snapshot contains no credential and is internal server data, not a browser
response contract. It never extends expiry or upgrades an epoch.

## Database ordering

Conditional no-op updates acquire database write locks on the tenant, account, verified provider
row and family, followed by grant, authorization and token for existing connections. They retain
all stored values. Existing lifecycle transactions write these same rows, so admission orders
against provider logout, account epoch changes, pending deletion and family revocation across
replicas. This works with the existing lifecycle transactions on SQLite and PostgreSQL; a process
mutex or an earlier live read would not provide that ordering.

A winning revocation causes admission to reject. Work admitted first commits before the waiting
revocation, which then closes its family/grant/protocol rows. Previously committed application
writes are not undone. Deadlock, lock-timeout, serialization, storage and cancellation failures
must abort the transaction and release no credential or successful write response. Do not replay
token work automatically. Admission performs database writes for locking, so use it at the
transactional mutation/issuance boundary; ordinary reads retain the existing live validation.

Local browser logout prevents new consent but preserves independent grants. Existing-grant
admission therefore checks `ReissueBlockedAtUtc`, not the browser's local revocation marker.
The real provider-logout path still revokes the derived families and authorizations after local
browser logout. Unrelated owners remain unchanged.

## Personal-tracking write integration

[#96](https://github.com/happticde/gamehub/issues/96) integrates this barrier with the actual
`PATCH /api/library/{id}/personal` action. The integration authentication handler retains a clone
of the successfully validated protocol principal in a server-only request feature. The narrowed
library principal still contains no protocol token identifier. Request fields or reconstructed
library claims alone cannot supply the validated feature.

`McpTrackingWriteFilter` runs after authorization, verifies that the current request identity
matches that feature, and opens the action's database transaction. It calls `AdmitAsync` for the
original access token and `tracking:write` before the tenant-scoped game lookup or mutation.
The existing action still rejects unsupported fields and foreign identifiers. The filter commits
only a successful action result, before the result executes; error results and exceptions roll
back. Commit/storage failure returns a bodyless 503, cancellation propagates, and neither exposes
a success response. Rolled-back tracked entities are cleared. An uncertain response never causes
an automatic write retry.

Browser and explicitly selected operator requests retain their existing action behavior without
the integration transaction. Local browser logout preserves independent integration writes;
provider logout, epoch/deletion and family revocation order against admission. A write admitted
first can commit before the waiting revocation; a winning revocation prevents the write.

Real HTTP/reference-token tests cover both orderings on SQLite and PostgreSQL, actual provider
logout after local browser logout, foreign data, missing/mismatched server identity, storage and
commit failures, request cancellation, and browser/operator compatibility. This adds no endpoint,
startup activation, migration, dependency or deployment setting. It does not enable user login.

## Remaining #71 integration gate

- Add bounded, cross-site-request-forgery-protected browser consent and account-switch/replay
  protection; show the exact instance, entered code, client-identity limits and permissions.
- Use [#100's device decision transaction](mcp-device-consent.md) for pending-code binding and
  native one-time verification. Grant creation alone does not consume a pending request or
  prevent two approvals of the same code. Keep its transaction and browser caller requirements.
- Preserve [#98's integrated token lifecycle](mcp-token-lifecycle.md): serializable buffered
  redemption/refresh admission, original resources/scopes/epoch, absolute-expiry clamping and
  connection-wide revocation on confirmed refresh reuse. These handlers do not activate startup.
- Preserve #96's validated access-token identity and final personal-tracking admission transaction.
  The authorization handler's earlier read alone is insufficient. Future sensitive operations
  need the same boundary before they can be permitted.
- Validate deployment origin/proxy, external identity provider, independent persistent keys and
  existing trust before startup activation. Run browser and full protocol lifecycle tests on both
  database providers. Final Docker/client and operator release acceptance remains #75.

`McpGrantAdmissionTests` use migrated SQLite and optional real PostgreSQL databases. They cover
restricted/foreign/stale identities, password-family provenance, scope narrowing, independent
families, rollback, token/authorization/grant mismatch, absolute expiry and both race orderings
for approval or existing-grant work against provider/epoch/deletion/family changes. The actual
provider-logout service participates in concurrent and post-local-logout cases. Supply only a
disposable server through `GAMEHUB_TEST_MCP_POSTGRES_CONNECTION`; each case removes its own database.
