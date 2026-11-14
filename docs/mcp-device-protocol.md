# Device authorization protocol boundary

[#70](https://github.com/happticde/gamehub/issues/70) provides staged
[Model Context Protocol (MCP)](https://modelcontextprotocol.io/) device authorization in Core,
following [ADR-016](adr/016-mcp-user-authorization.md) and the
[connection contracts](mcp-grant-contracts.md). OpenIddict implements
[Device Authorization Grant, RFC 8628](https://www.rfc-editor.org/rfc/rfc8628).
This package enables no endpoint in `Program.cs`, introduces no deployment setting, and cannot
approve consent. Production activation belongs to #71 after its browser/session and race gates.
Docker user login remains gated by #75; operators do not need to configure anything for #70.

## Registration and protocol

`AddMcpDeviceProtocol` requires explicitly supplied HTTPS issuer/resource identifiers (a root
issuer without a path prefix) and
`McpProtocolKeys`. `RegisterMcpDeviceClientAsync`, called after migrations in a scoped service
provider, creates `gamehub-mcp-stdio` as a public client with explicit consent and only device
and refresh grants. An incompatible existing registration fails instead of silently gaining
permissions. A public client ID identifies software, never an account or a trusted device.

`UseMcpDeviceProtocol` must precede authentication. The staged surface consists of
`POST /connect/device`, `POST /connect/token` and reserved `/connect/verify`.
Requests require the configured host/port and HTTPS. Browser Origin headers and query parameters
are rejected. No request-derived host selects an issuer or resource. Trusted proxy configuration
and public routing still require #71/#75 verification; transport security is never disabled.

Device requests require exactly one configured REST resource and `library:read`, optionally
`tracking:write` and `offline_access`. Offline access requests renewal, not library permission.
Pending principals have no account, tenant or grant. Duplicate/unknown fields, including owner
selectors, are rejected. The token endpoint requires the same exact resource; OpenIddict retains
the approved scopes and rejects widening. Reference access/refresh tokens use encrypted payloads,
ten-minute access and thirty-day refresh defaults, rotating refresh and no reuse grace.
Application absolute expiry and connection-wide replay revocation remain mandatory #71 policy.

`MapClosedMcpConsent` denies verification and approved redemption. The middleware also denies
verification before parsing any user code. No browser, operator or local password credential can
bypass this gate. Tests alone install synthetic consent/issuance to exercise library mechanics.
Their fabricated account is not a production approval implementation.

OpenIddict normally creates an ad-hoc authorization even for a pending device request. GameHub
authorizations require an already owned connection family. The registration therefore replaces
only that automatic creation handler with a stricter requirement: non-pending sign-in must
already carry an explicit authorization ID. Pending device/user codes remain ownerless; token
and authorization storage/validation stay enabled. This uses the supported
[event model](https://documentation.openiddict.com/introduction#events-model), without changing
protocol parsers, code protection, lifetime checks or single-consumption behavior.

## Bounds and failures

| Boundary | Limit / behavior |
| --- | --- |
| Form body | 4096 bytes, also on non-seekable/chunked streams; bounded keys, values and field count |
| Concurrency | 16 operations per Core process, no queue |
| Anonymous work | 30 device/verification requests and 300 token requests per minute per process; fixed buckets, independent of caller-selected addresses or client IDs |
| Pending store | 256 live device requests; serializable admission and issuance, shared by replicas |
| Pending lifetime | 600 seconds; expired ownerless device/user entries are pruned in batches of 128 on admission |
| Polling | Initial five-second interval; premature polling returns `slow_down` and adds five seconds, bounded by pending lifetime |
| Poll state | Next time and interval in token properties; no raw code, durable across restarts/replicas |
| Redemption | Serializable admission plus OpenIddict consumption; a losing transaction cannot expose an uncommitted response |
| Failures | Stable error name only; storage/key/serialization failures deny access, never retry with another authentication scheme |

Responses remain buffered until the transaction commits and carry `no-store`. Exhausted work
budgets return HTTP 429; unavailable storage returns HTTP 503. Neither response is an approval.
Process budgets are conservative overload protection, not a distributed caller identity system;
the deployment still needs ordinary proxy connection/time limits. Durable pending and per-code
limits remain database-wide. Unexpected transaction failures require a later client attempt,
not an automatic credential or write replay.

OpenIddict diagnostic categories and framework request-start/finish logging are disabled when the
adapter is registered, including at trace level, because they can include protocol values or
query strings. Application diagnostics report only a generic failure type. Do not add body/query
logging, analytics, sensitive database logging or upstream proxy logs containing codes/tokens.
The intentional device response is the only code-bearing output of an anonymous request.

## Keys and next implementation gate

`McpProtocolKeys` requires an existing dedicated key directory and three distinct, currently valid
RSA private certificates (at least 2048 bits): signing, encryption, and key-ring protection.
[ASP.NET Core Data Protection](https://documentation.openiddict.com/configuration/token-formats)
uses the isolated application name `GameHub.Mcp.Protocol.v1`; its persisted ring is encrypted with
the protection certificate. Retired protection certificates can be supplied for decryption.
Missing keys fail closed; restart tests recover the same encrypted pending payload. Operators
must provision the ring outside source/image/database storage, protect private certificates
independently, share the ring across replicas, and retain old decryption material through all
outstanding lifetimes. Signing/encryption key rotation and deployment backup/restore are #75
operational gates; no certificate or ephemeral production fallback is generated by this code.

#71 must validate the complete deployment trust and independent existing keys before wiring this
adapter in Core startup. Replace the closed consent mapper with authenticated browser consent;
preserve body/guess limits when replacing the verification middleware gate. Atomically create the
owned family, authorization and immutable grant, and explicitly bind the pending device token's
authorization through the OpenIddict manager before completing verification. OpenIddict's
verification update retains that storage binding; an encrypted principal alone is insufficient.
Serialize approval/redemption/refresh against provider reissue blocking, account epochs and
deletion. Recheck the live consent through #69, preserve absolute expiry, and revoke the entire
connection on confirmed refresh reuse. Do not expose a merely protocol-valid credential early.

## Verification

`McpDeviceProtocolTests` use real OpenIddict 7.8.0 (latest stable checked on 2026-11-14,
[released 2026-11-03](https://github.com/openiddict/openiddict-core/releases/tag/7.8.0)).
They exercise pending/denied/expired/slow-down states, owner-selector and resource/scope rejection,
transport/body limits, anonymous work limits, encrypted persistence/restart, logging, closed
production consent, and synthetic concurrent one-time redemption/refresh. PostgreSQL cases use
the same disposable-server `GAMEHUB_TEST_MCP_POSTGRES_CONNECTION` setting as the connection tests.
Real browser consent, logout races, real clients, proxy certificates and product release remain
the explicit downstream gates above.
