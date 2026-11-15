# Transactional MCP token lifecycle

[#98](https://github.com/happticde/gamehub/issues/98) integrates the
[Model Context Protocol (MCP)](https://modelcontextprotocol.io/) token endpoint with the
[grant admission transaction](mcp-grant-admission.md), following
[Architecture Decision Record 016](adr/016-mcp-user-authorization.md). It adds no production
endpoint registration, deployment setting, dependency or migration. Browser consent, pending-code
binding and validated startup activation remain [#71](https://github.com/happticde/gamehub/issues/71).
Docker authentication remains gated by [#75](https://github.com/happticde/gamehub/issues/75).

## Issuance and renewal

`McpTokenIssuance` runs before OpenIddict consumes the source device or refresh token. It takes
the original library-validated principal from the authentication transaction, performs live
owner/provider/family/grant/authorization/token admission, and retains those database locks through
consumption, credential creation and commit. A passthrough endpoint cannot replace that principal's
owner, epoch, scopes or resource. Tenant, family and epoch claims explicitly reach the access
token so the existing integration validation and sensitive-write barrier can check them.

`McpTokenExpiry` clamps the prepared access and refresh principals before OpenIddict creates their
database entries or encrypted payloads. Access lasts at most ten minutes; every credential ends
no later than the original grant. Refresh uses non-sliding expiration and never upgrades the
approved account epoch or extends the thirty-day absolute connection end. The buffering boundary
checks the absolute end again before committing a successful token response.

These handlers use the library's supported
[event model](https://documentation.openiddict.com/introduction#events-model). Cryptography,
parsing, token-entry validation, authorization validation, consumption and rotation remain enabled.
The integration relies on OpenIddict 7.8.0's handler ordering; upgrades must rerun the endpoint tests.

## Confirmed reuse

`McpRefreshReuse` runs after token protection, issuer/type, expiration, presenter and proof checks,
before the library rejects a consumed token entry. A raw reference lookup alone is insufficient.
For a consumed refresh token, the same grant admission locks revalidate the exact owner, resource,
approval epoch, provider and stored token binding. Confirmed reuse revokes that connection's
family, grant, authorization and tokens, including the latest successors. Independent connections,
the browser family and other tenants remain valid.

The rejected response commits this confirmed revocation. Invalid, foreign-bound, expired or
resource-substituted credentials cannot select another connection to revoke. Device-code replay
is rejected without ending its already established connection. There is no refresh reuse grace
or automatic retry. A serialization failure can occur before reuse is confirmed; the client must
treat an uncertain renewal as requiring recovery or new login, rather than replaying automatically.

## Buffered failure boundary

The existing serializable middleware keeps durable device polling state before a savepoint.
Unsuccessful issuance rolls back protocol work after that savepoint, including consumed source
tokens and newly generated credentials, while retaining polling backoff. Confirmed refresh-reuse
revocation is the explicit error outcome that commits. Error bodies contain only the stable error
name. Commit, storage and cancellation failures release no successful response; tracked rollback
state is cleared and the original request is never retried automatically.

A winning lifecycle revocation prevents new issuance. If issuance obtains the locks first, it
commits before the waiting revocation; that revocation then closes the just-issued credentials.
Provider logout after local browser logout still reaches independent connections through retained
provider provenance. Previously completed responses are not retroactively recalled.

## Verification

`McpTokenLifecycleTests.cs` extends the real device-protocol host with synthetic test-only consent.
It exercises endpoint redemption/rotation/replay, protected restart, 64-bit epochs, absolute expiry,
two-tenant and independent-connection isolation, late rejection/commit rollback, and both race
orderings against provider logout, account epoch, deletion and family revocation. Both SQLite and
real PostgreSQL are required. Use a disposable server through
`GAMEHUB_TEST_MCP_POSTGRES_CONNECTION`; every PostgreSQL case removes its own database.
Production browser consent and operator/client acceptance are still downstream work.
