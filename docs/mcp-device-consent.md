# One-time device consent transaction

[#100](https://github.com/happticde/gamehub/issues/100) implements the staged Core decision
transaction for [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) device consent.
It follows [Architecture Decision Record 016](adr/016-mcp-user-authorization.md) and builds on
the [grant admission](mcp-grant-admission.md) and [token lifecycle](mcp-token-lifecycle.md) barriers.
It adds no production route, startup registration, deployment setting, dependency or migration.
Production verification and token issuance remain closed. Browser consent and validated activation
remain [#71](https://github.com/happticde/gamehub/issues/71); release acceptance remains
[#75](https://github.com/happticde/gamehub/issues/75).

## Caller and transaction

The future verification endpoint calls `McpDeviceConsent.ExecuteAsync` only after enforcing its
trusted backend-for-frontend boundary, bounded input and guessing budget, cross-site request
forgery protection, and a displayed decision bound to the same live browser family and code.
Those browser safeguards are not supplied by this transaction service. Do not map it directly
to an anonymous or browser-controlled endpoint, or remove the current closed middleware gate.
Never put device/user codes or credentials in request logging, analytics or error details.

The service owns a serializable transaction on the request's scoped `CoreContext`. It explicitly
authenticates the signed browser bearer scheme, then takes the same owner, provider and browser
family locks as grant creation. A password family with an unrelated external identity link,
restricted linking/deletion token, operator key, integration credential, stale epoch, revoked
browser or provider, or pending deletion cannot approve or deny. No request owner or provider
selector is accepted, and `HttpContext.User` does not supply authentication proof.

OpenIddict authenticates the protected user code and supplies its original device-entry binding,
client, resource and scopes. The service additionally locks both stored entries and requires a
live, ownerless user code and inactive, ownerless device code for the registered public client.
This recheck matters even when the library authenticated before the transaction began. A stored
authorization or subject on either pending entry rejects the decision instead of reusing a grant.

## Approval and denial

Approval creates one independent family, owned protocol authorization and immutable grant through
`McpGrantAdmission`. Read permission is required. Tracking writes require both the original request
and explicit approval; renewal permission is retained only when originally requested. Ownership,
provider provenance and the original 64-bit epoch come from the verified browser family.

The supported `IOpenIddictTokenManager` populates and updates the pending device descriptor with
the new authorization ID before native sign-in. The encrypted approved principal carries that
same owner, family, epoch, resource and narrowed scopes. OpenIddict consumes the user code and
updates the existing device payload; it retains the explicitly bound storage authorization.
The service checks those final database outcomes before committing. Library parsing, protection,
validation and single consumption remain enabled.

Denial uses the library's native challenge to reject both pending entries without creating a
grant. A second approval, denial or competing owner's decision cannot change the completed result.
An unrelated connection is never selected by a pre-bound pending entry. A serialization conflict
fails closed; it is not automatically replayed.

The native response is buffered and discarded. Only a bodyless HTTP 204 is returned after commit;
an invalid decision returns bodyless 400, and storage/commit failures return bodyless 503. No code,
token, owner identifier, native redirect or error description is released by this service.
Responses forbid caching. Unsuccessful decisions roll back family creation, binding and code
consumption and clear tracked entities. Request cancellation propagates after rollback.
The earliest original user/device expiry is checked again after native processing, so waiting or
late processing cannot turn an expired request into consent.

## Lifecycle ordering and verification

The owner/provider/browser locks remain held through binding, native processing and commit.
A winning provider logout, epoch change, deletion or browser logout prevents approval. Approval
admitted first commits before the waiting lifecycle transaction. Local browser logout preserves
that independent grant; verified provider logout and account-wide revocation close it, including
when the local browser had already logged out. Other tenants and independent provider sessions
remain unaffected. Redemption and refresh keep the existing final admission transaction.

`McpDeviceConsentTests.cs` uses real signed browser tokens and OpenIddict endpoints with migrated
SQLite and real PostgreSQL. It covers permission narrowing, restart/refresh, competing owners,
approval/denial races, invalid provenance, pre-bound storage, rollback, cancellation, expiry during
processing, both lifecycle race orderings and provider logout after local logout. The test host
alone bypasses the closed verification middleware and supplies decisions; it provides no browser
form or production registration. Use the disposable-server
`GAMEHUB_TEST_MCP_POSTGRES_CONNECTION` setting; each case removes its own database.

## Remaining browser and activation gate

#71 must supply the session/code-bound preview and form decision, same-origin request protection,
explicit displayed account/instance/code and permissions, and the independent trusted service
boundary. It must preserve input/guess budgets and ensure native rejections outside this service
also receive safe browser responses. Validate proxy/origin, external provider trust and independent
persistent keys before wiring startup and removing either closed gate. Browser accessibility,
account switching, stale forms and full deployment lifecycle checks remain required there.
Client login/storage and the final Docker/operator release gate remain #72–#75.
