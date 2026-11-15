# Browser consent and guarded protocol activation

[#71](https://github.com/happticde/gamehub/issues/71) integrates the
[Model Context Protocol (MCP)](https://modelcontextprotocol.io/) device protocol with the signed
GameHub browser session, following [ADR-016](adr/016-mcp-user-authorization.md).
Activation is **default-off**. This delivery does not enable guarded multi-user mode, publish an
image or claim the completed Docker client experience. Explicit client login/storage and
credential-bound logout are documented in the [MCP guide](guides/mcp.md#staged-device-login-and-credential-volume).
Tool token use and connection management are implemented in #73/#74. The
[local release gate and operations guide](mcp-local-release-gate.md) covers integrated image
acceptance, key rotation/backup and the human enablement handoff.

## Browser flow

The device response advertises the fixed public `/mcp/verify` page, without a code-bearing URL.
Normal GameHub sign-in returns there through the existing local return-path and OpenID Connect
(OIDC) transaction. The person enters the assistant's code after sign-in. Codes are never placed
in URLs, browser storage, analytics or diagnostic messages.

The page first requests a preview. Core independently authenticates the signed browser bearer
ticket and the native OpenIddict user code. The account, tenant, epoch and browser family come
only from the verified ticket. A live owner-constrained provider-session mapping is mandatory;
a local password session or an external identity link alone cannot consent.

The preview displays the instance, current account and stable account reference, entered code,
public-client identity limits, required library reads and any requested optional tracking writes.
The write checkbox starts unchecked. Renewal is explained only when requested. A protected
confirmation binds both pending storage IDs, original resource/scopes, account, tenant, epoch,
browser family and an expiry no later than the original pending expiry or five minutes.
Only that confirmation and the entered code can decide the request. It carries no integration
access/refresh credential and is not browser authentication.

Approval/denial uses the existing [one-time transaction](mcp-device-consent.md), including native
code consumption and the [grant](mcp-grant-admission.md) and [token](mcp-token-lifecycle.md)
barriers. The confirmation expiry is checked again after native processing, before commit.
Account switching, code substitution, scope changes, stale/replayed forms and lifecycle races
fail closed. A receipt appears only after a committed bodyless 204 response. An uncertain response
returns to review instead of replaying the decision. Provider logout still revokes derived
connections after local browser logout.

## Server transport and public routing

The browser calls only `POST /api/mcp/consent` on its exact configured origin. The
backend-for-frontend (BFF), the Next.js server adapter, checks origin and fetch-site before body
reads, cookies, refresh or upstream requests. It accepts one bounded JSON shape with no ownership,
scope or destination selectors. It refreshes an expired browser cookie once **before** a request;
it never retries an upstream decision. Only a fixed preview shape or bodyless receipt returns
to JavaScript. Session tokens and service keys remain server-side.

Route these paths explicitly at the public HTTPS reverse proxy:

| Path | Destination |
| --- | --- |
| `/mcp/verify`, `/api/mcp/consent`, existing browser/auth paths | Next.js |
| `POST /connect/device`, `POST /connect/token`, `POST /connect/revoke` | Core |
| `/connect/verify` | No public route; dedicated BFF-to-Core HTTPS transport only |
| Integration REST requests under `/api/library` | Core's existing operation authorization |

The dedicated adapter targets only `/connect/verify` on `MCP_CORE_ORIGIN`, independently from
the generic adapter that appends `/api`. HTTPS certificate validation remains enabled. Its Host
header is pinned to the configured public issuer. Deploy an internal HTTPS Core listener or an
internal proxy with a certificate trusted by Node; route that path internally to Core. Never
reflect browser headers or follow upstream redirects. Restrict access to the internal listener.
The independent service key and signed browser ticket are both required even on that listener.

Core accepts no browser Origin or query string on protocol routes. Before native processing it
enforces the existing body, field, global guessing/polling and concurrency budgets. Verification
requires a trusted BFF key before parsing. Native failures outside the decision service are
buffered and stripped of bodies/redirects. All protocol/consent responses forbid caching.

Direct integration credentials authorize only the existing marked library operations: list,
detail, statistics, random game and tag reads, plus explicitly granted play-status/rating
changes. They do not authorize `IsIgnored`, imports, sync, credentials, backup or account routes.
See [library permission boundary](mcp-grant-admission.md) and
[`LibraryController`](../services/core/Controllers/LibraryController.cs).

## Operator settings

Do not enable this staged delivery for end users before the [#75 release gate](https://github.com/happticde/gamehub/issues/75).
Operators own secret provisioning and all deployment configuration. No environment file is needed
or managed by this implementation. For Compose, supply settings and key mounts through an
operator-owned override or deployment secret/configuration mechanism; the stock Compose file does
not forward these new settings automatically.

Core configuration keys use `:` below; .NET environment variables use double underscores instead.

| Core key under `Mcp:Device` | Requirement |
| --- | --- |
| `Enabled` | Boolean; absent/false disables every new endpoint and key load |
| `Issuer` | Exact root HTTPS GameHub issuer, including trailing `/`; distinct origin from provider OIDC |
| `Resource` | Exactly `{Issuer}api` |
| `PublicOrigin` | Exact browser origin, matching issuer authority |
| `BffServiceKey` | Dedicated 32–512 printable ASCII secret, independent of web JWT, OIDC BFF, operator and Ingest secrets |
| `KeyDirectory` | Existing absolute dedicated persistent encrypted key-ring directory |
| `SigningCertificate`, `EncryptionCertificate`, `ProtectionCertificate` | Absolute paths to three independent, valid RSA private PKCS#12 certificates, at least 2048 bits |
| Corresponding `SigningCertificatePassword`, `EncryptionCertificatePassword`, `ProtectionCertificatePassword` | Optional passwords from operator secret configuration |
| `RetiredProtectionCertificates` | Optional indexed absolute certificate paths for old ring decryption, using `ProtectionCertificatePassword` |
| `KnownProxies` | Optional indexed exact proxy IP addresses; forwarded scheme/host accepted only from these addresses, one hop |

Core additionally requires valid existing web-token and OIDC settings plus
`Authentication:Frontend:Enabled=true` and `Authentication:Frontend:SecureCookies=true`.
No development or ephemeral certificate fallback exists. Missing keys, incompatible persisted
client trust or invalid enabled configuration abort startup. Client registration runs after
migrations. Disabled configuration preserves compatibility and does not load certificates.

| Next.js server variable | Requirement |
| --- | --- |
| `MCP_DEVICE_ENABLED` | `true` to expose consent |
| `MCP_ISSUER` | Same exact issuer as Core |
| `MCP_BFF_SERVICE_KEY` | Same dedicated transport key as Core; independent of OIDC cookie/client/BFF keys |
| `MCP_CORE_ORIGIN` | Fixed internal HTTPS origin for `/connect/verify`; no path/query/credentials |
| `PUBLIC_ORIGIN` | Exact public HTTPS origin matching issuer and existing OIDC callback origin |

Existing `AUTH_ENABLED`, `AUTH_COOKIE_SECURE`, `OIDC_ENABLED` and complete OIDC trust must also be
valid. These are server-only variables; never use a `NEXT_PUBLIC_` key for any trust setting.

Forwarding trusts no request-selected issuer/resource. Configure only known proxy addresses,
preserve exact Host/HTTPS routing, bound proxy connection/body/time budgets, and disable code/query
or credential logging. Keep Core's internal consent path out of public routing. The
[OpenIddict integration](https://documentation.openiddict.com/integrations/aspnet-core.html)
retains HTTPS enforcement; use
[trusted forwarding](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0)
when terminating HTTPS upstream.

Protect private certificates, share the encrypted ring across replicas, and retain old decryption
material for outstanding grants. Restore keys and database together; changing the deployment's
issuer/resource or public-client permissions is not an in-place trust upgrade. Follow the
[rotation and recovery procedures](mcp-local-release-gate.md#persistent-keys-backup-and-recovery)
before changing persistent keys or restoring protocol state.
The production local-validation adapter uses the already centrally pinned OpenIddict 7.8.0
server-integration package, matching the proven stack; no protocol-library upgrade is included.

## Verification ownership

`McpBrowserConsentTests` exercise the production middleware and endpoints on SQLite and PostgreSQL,
including displayed permission narrowing, denial, account/code/proof substitution, expiry during
processing, replay, lifecycle races and persisted-ring restart. `McpDeviceStartupTests` execute
the actual Core entry point with migrated databases, opt-in/off/invalid trust, trusted forwarding,
issuance, direct library validation and live revocation. Existing grant/consent/token tests retain
the broader ownership, deletion, provider logout and refresh-replay matrix.

Frontend security tests execute the BFF handler and transport with controlled upstream responses
to verify origin rejection before side effects, bounded bodies, exact output shape, server-only
credentials, pre-request refresh and no mutation replay. Browser interaction checks cover the
shared form with synthetic fixture responses; final real-client/deployment acceptance remains #75.
Merge, image release, documentation publication and operator enablement stay human-owned.
