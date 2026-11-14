# OpenIddict compatibility proof

**Checked:** 2026-11-14. **Decision:** Go for the library choice in
[Architecture Decision Record (ADR) 016](../../docs/adr/016-mcp-user-authorization.md),
subject to the application work below. This completes the bounded protocol proof in
[#85](https://github.com/happticde/gamehub/issues/85), before production schema adoption in
[#68](https://github.com/happticde/gamehub/issues/68).

This executable uses the unmodified OpenIddict server, validation, and Entity Framework Core
database storage. It has no reference to production projects and is outside the application
solution. Its database is SQLite in memory; signing/encryption keys are ephemeral. The browser
credential is a generated fixture using GameHub's existing issuer/audience/algorithm checks,
not a real account session. The test-only approval endpoint is not a production consent design.

## Repeat the proof

From the repository root, with Docker running:

```bash
bash tools/openiddict-proof/run.sh
```

The runner creates and removes its own container from a digest-pinned official stable .NET 10
image. It copies five explicit proof inputs, including the package lock, without mounting the
repository or reading environment files. Restore uses locked versions and treats audit warnings
as errors. Network access is disconnected before the Release build and protocol checks run.
There are no published ports. ASP.NET Core's in-process TestServer sends HTTPS requests; transport
security and protocol validation are not disabled. This does not test TLS certificates or a proxy.

Output contains check names and public dependency metadata, never tokens or device/user codes.
Any failed assertion exits nonzero. Protocol logging is disabled in this isolated fixture.
Production still needs safe structured logging and abuse limits.

## Versions and dependency review

| Input | Checked version / evidence |
| --- | --- |
| OpenIddict server, validation, database integration | **7.8.0**, latest stable, released 2026-11-03; [official release](https://github.com/openiddict/openiddict-core/releases/tag/7.8.0). Version 8 remains preview. |
| .NET | SDK **10.0.404**, ASP.NET Core/runtime **10.0.15**, Linux ARM64, Ubuntu 24.04. Image digest is pinned in `run.sh`. |
| Microsoft test host, browser bearer handler, database packages | **10.0.14**, deliberately match the current production baseline; no production upgrade. |
| SQLite native dependency | SQLitePCLRaw **3.0.5**, SQLite **3.53.4**, pinned by `packages.lock.json`. |
| Model Context Protocol (MCP) C# library | Production declares and restores **2.3.1**; [upstream release](https://github.com/modelcontextprotocol/csharp-sdk/releases/tag/v2.3.1). This proof does not host MCP. |
| Candidate clients observed locally | Codex command-line client **0.167.0-alpha.4.1** and Visual Studio Code **1.143.1**. These are observed versions, not supported or latest-stable claims. Neither was used for an authenticated interoperability test. |

The complete resolved graph and integrity hashes are in `packages.lock.json`. OpenIddict's
resolved packages use [Apache-2.0](https://github.com/openiddict/openiddict-core/blob/7.8.0/LICENSE.md).
Microsoft packages use MIT; SQLitePCLRaw uses Apache-2.0; SQLite's packaged license states
[public domain](https://sqlite.org/copyright.html). These were read from restored package metadata.
No unresolved license metadata was found. Package notices must be regenerated when production
dependencies are adopted; this test executable is not included in a product image.

NuGet's advisory check, including transitive packages, reports no known vulnerabilities for this
resolved graph on the checked date. The published OpenIddict advisory
[GHSA-925x-4h4v-2792](https://github.com/openiddict/openiddict-core/security/advisories/GHSA-925x-4h4v-2792)
affects releases before 7.7 and is fixed in the selected 7.8.0 release. This is a dated advisory
result, not a security audit.
Repeat the review before #68. The runner prints the fresh advisory result.

## Observed results

**55 assertions pass**, including these boundaries:

| Scenario | Observed result |
| --- | --- |
| Device request before approval | `authorization_pending`; anonymous approval is rejected. A signed browser fixture explicitly approves before issuance. |
| Device-code redemption | One successful redemption; a second returns `invalid_grant` and revokes that grant's access token. |
| Resource requirement | Missing, unknown, substituted, multiple, and duplicate resources are rejected. Device redemption and refresh require the same exact resource. |
| Wider registered resource | Rejected even though the fixture registers both REST and remote resources and gives the client permission for both. The per-grant rule remains stricter. |
| Scope widening | Adding the registered `tracking:write` scope during refresh returns `invalid_grant`. Initial and renewed access keep only `library:read offline_access` and one audience. |
| Browser/integration scheme separation | Each accepts its own access token and rejects the other. Changing a browser token's audience does not turn it into an integration token. A refresh token cannot authenticate an API request. |
| Refresh rotation | A replacement refresh token is issued under the same protocol authorization. Reuse grace is explicitly disabled with `SetRefreshTokenReuseLeeway(null)`. |
| Confirmed refresh reuse | Original and successor access tokens fail on the next request; successor refresh returns `invalid_grant`. An independent authorization survives. |
| Reuse and durable family state | OpenIddict leaves the authorization row valid after revoking its token chain. GameHub must still revoke its own family/grant and protocol authorization. |
| Token-entry revocation | Immediately blocks the next access request. Revoking access alone does **not** stop the separate refresh credential. |
| Authorization-entry revocation | Immediately blocks access and refresh. Both token-entry and authorization-entry validation are enabled. |
| Standard revocation endpoint | Revoking a refresh token blocks renewal, but its existing access token remains valid. GameHub logout must additionally close the whole connection. |
| Isolation of revocation | Other protocol authorizations and the browser bearer remain valid. This is protocol isolation, not a tenant-isolation test. |

OpenIddict handles protocol validation, device/user-code storage, one-time redemption, rotation,
replay detection, and protocol revocation. Small supported event handlers add GameHub's stricter
single-resource requirement; endpoint code supplies fixture consent and preserves the approved
principal. No built-in handler or validation is removed. The matching source behavior is visible in
[token-entry validation](https://github.com/openiddict/openiddict-core/blob/7.8.0/src/OpenIddict.Server/OpenIddictServerHandlers.Protection.cs)
and [scope validation](https://github.com/openiddict/openiddict-core/blob/7.8.0/src/OpenIddict.Server/OpenIddictServerHandlers.Exchange.cs).

## Handoff to the schema package

Proceed to #68 only after the user merges this proof and verifies it. Preserve ADR-016's design:

1. Model one tenant/account-owned consent grant per independent `AccountSession`, referencing
   exactly one protocol authorization, client, approved scopes, resource, approved account epoch,
   creation time, absolute expiry, and revocation state. Enforce composite ownership keys.
2. Store an owner-constrained reference to the existing verified provider-session row. Multiple
   grants may reference it; do not change its existing one-to-one browser-family mapping or copy
   provider session identifiers from client input.
3. Use OpenIddict's refresh storage; do not add parallel browser refresh rows for the same token.
   Transactionally close GameHub family/grant/protocol state on confirmed reuse, explicit logout,
   provider logout, account invalidation, or deletion. A token-only revocation is insufficient.
4. Enforce grant absolute expiry without extending it on rotation. Check live account, tenant,
   epoch, family, and provider revocation state at approval, redemption, every refresh, and use.
5. Order approval/redemption/refresh/writes against revocation with database transactions and
   concurrency controls. Prove those races and migrations on SQLite and real PostgreSQL.
6. Keep authentication schemes explicit per endpoint. The production integration allowlist and
   read/write scope policies must reject account/operator routes and unsupported tracking fields.

No protocol fork or weakened validation was needed, so ADR-016 need not reopen. This is a go for
the library's demonstrated mechanics, not for enabling user authentication or remote access.

## Verification limits

The full existing .NET suite passes **591 tests** in a separate source snapshot on the stable SDK.
The dependency checker now respects the closest central package manifest so this proof can own
its packages without adding OpenIddict to production. Its nine tests pass, including rejection
of proof-only dependencies from production and invalid nested manifests.

Production startup, service references, package versions, configuration, schemas, keys, and
environment files are unchanged. No frontend build, product image, deployment, or publication is
needed for this proof. Live PostgreSQL checks, tenant ownership, account/provider logout races,
absolute expiry, cross-site request forgery protection for browser consent, proxy/TLS operation,
multi-process revocation/cache behavior, and real-client authorization
remain unimplemented/unverified here and belong to the downstream packages.
The wider tooling run has 51 passes and 20 skips because the pinned secret scanner and release
versioning tool are absent locally. The nine dependency tests above have no skips. Hosted checks
remain separate from this local verification.

The [2.3.1 library's protocol constants](https://github.com/modelcontextprotocol/csharp-sdk/blob/v2.3.1/src/Common/McpProtocolVersions.cs)
list **2024-11-05**, **2025-03-26**, **2025-06-18**, **2025-11-25**, and **2026-07-28**. The first
four use the initialization handshake; the July 2026 HTTP path uses per-request metadata instead
of that handshake and transport sessions. This is source-level library support, not an exercised
GameHub HTTP transport. The existing image smoke client requests MCP **2025-06-18**; that request alone proves neither
the newer authorization specification nor authenticated client compatibility. #75 and #80 must
record actual negotiated versions and test the agreed clients against their then-current builds.
Client pre-registration remains the accepted initial limit; automatic registration and metadata
document support are not established by this proof.
