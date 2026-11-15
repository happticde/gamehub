# Assistant connection management

The Admin **Security** tab lists a signed-in account's active
[Model Context Protocol (MCP)](https://modelcontextprotocol.io/) consent connections separately
from browser sessions. Each connection shows its local ID, public client ID, approved permissions,
exact resource, creation time and absolute expiry. Identical clients are distinguished by their
local connection IDs. Those IDs are not credentials. No tokens, device codes, provider identifiers,
IP addresses or device metadata are returned.

Choose **Zugriff widerrufen** and confirm the displayed connection. Core transactionally closes
that connection's family, consent grant, protocol authorization and tokens. The next request or
renewal is rejected after commit. An already running operation is not automatically rolled back;
tracking writes retain the existing final transactional admission check. Other connections and
the browser session remain valid. Reconnect through explicit client login and fresh consent.

## Browser boundary

- `GET /api/auth/mcp-connections` lists owned active consents, newest first. Expired, revoked,
  provider-blocked and account-epoch-invalid grants are omitted.
- `DELETE /api/auth/mcp-connections/{id}` revokes exactly one owned consent. Unknown, foreign
  and previously revoked IDs all return an empty `204`; they never reveal another owner's data.
- Both routes accept only a live ordinary browser bearer session. Operator keys and integration
  tokens cannot manage connections. Ownership comes from signed account/tenant claims, never
  request fields. Responses are not cached.
- Browser components call the existing same-origin Backend for Frontend (BFF) proxy at
  `/api/core/auth/mcp-connections`. Access and refresh tokens stay in HttpOnly cookies. The
  existing exact-origin mutation guard and final-session-ended handling apply.
- A failed or timed-out revocation remains uncertain until confirmed. The dialog supports an
  explicit idempotent retry; it never claims success or removes a connection before Core confirms.

## Lifecycle and verification

[ADR-016](adr/016-mcp-user-authorization.md) defines the lifecycle. Local browser logout preserves
independent connections. Verified provider-session logout blocks all connections derived from
that provider row, even after local logout. Account-wide revocation and deletion block all
affected grants; unrelated tenants remain isolated. **Alle anderen Sitzungen abmelden** retains
its existing account-family scope, including other assistant families.

`McpConnectionManagementTests` exercises the real production routes, two owners/tenants, exact
response shape, credential-scheme rejection, repeat/foreign revocation and protocol invalidation.
`McpTokenLifecycleTests.Token_ConcurrentLifecycleChange_OrdersIssuanceAndRevocation` includes the
browser connection controller in both winning orderings against device issuance and refresh.
The existing lifecycle, provider logout, deletion, tracking-write and admission suites remain
required. Execute these on SQLite and PostgreSQL in Docker; use the synthetic test connection
variable `GAMEHUB_TEST_MCP_POSTGRES_CONNECTION` for PostgreSQL.

This management surface does not complete the local authenticated release gate
[#75](https://github.com/happticde/gamehub/issues/75), enable guarded mode or publish a release.
