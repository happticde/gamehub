# Local authenticated MCP acceptance and operations

The [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) Docker client supports explicit
device login, an independent consent connection, protected local persistence and rotating renewal.
The capability stays **default-off**. [#75](https://github.com/happticde/gamehub/issues/75) records
the checked source commit, hosted results and release decision. A passing branch is awaiting review;
release readiness requires the gate to be merged and verified on the exact `main` candidate.
Publication, documentation deployment and production enablement are separate human steps.

## Reproduce the acceptance

Use disposable fixtures only. The commands never need provider credentials or environment files.
Build the image from the candidate checkout, restore/build the tests in the development container,
then run the host-side Docker driver:

```bash
docker build -f services/mcp/Dockerfile -t gamehub-mcp:local-gate .
docker exec -w /workspaces/gamehub gamehub_devcontainer-app-1 \
  dotnet restore tests/GameHub.Tests/GameHub.Tests.csproj
python3 scripts/ci/smoke-mcp-auth.py gamehub-mcp:local-gate \
  --container gamehub_devcontainer-app-1
```

The driver needs Python 3, a POSIX terminal and Docker on the host. Actual .NET execution occurs
in the container; no Docker socket is exposed to the client or fixture. Hosted Linux runners use
`--host` with the stable .NET 10 SDK. Both native image architectures run the authenticated smoke
before image verification succeeds; releases publish those same verified images.

The fixture executes the **production Core entry point**, migrations, protocol, browser consent,
library authorization and connection-management routes. It seeds two distinct OpenID Connect
(OIDC) owners and tenant-local games with the same slug, and explicitly enables guarded mode in
the fixture. Only the Ingest readiness probe is replaced; no Ingest/provider login is simulated as
an end-to-end identity-provider test. An isolated TLS adapter forwards the image's network requests
into Core. Only the public test certificate authority is mounted in the non-root image. Production
certificate validation remains enabled. The private fixture filesystem channel submits consent
through Core's real signed-browser/service-key boundary; it adds no product endpoint or bypass.

The actual image runs interactive `login` in a pseudo-terminal. The driver consumes the deliberate
code display privately and approves the selected fixture account. Each tool call starts a fresh
image process using the same dedicated named volume. The gate verifies all seven tools, both
write permissions, foreign-ID denial, independent connections, Core restart with persistent keys
and database, rotating refresh persisted before use, browser revocation, credential-bound logout,
and JSON-RPC-only stdout. To trigger renewal promptly it changes only the fixture client's local
expiry timestamp; the access/refresh credentials and Core's lifetime policy remain real.

Add `--postgres` with `GAMEHUB_TEST_MCP_POSTGRES_CONNECTION` or
`GAMEHUB_TEST_POSTGRES_CONNECTION` configured in the test container to run the integrated fixture
against PostgreSQL. Use a dedicated disposable database, never a deployment database. The full
suite also needs the general variable for historical upgrade tests. Tests that require PostgreSQL
return early when neither variable is configured; a green SQLite-only run is insufficient evidence
for the database gate.

| Acceptance boundary | Evidence |
| --- | --- |
| Actual image login, consent, two guarded owners, all tools, persistence/restart/renewal/revoke | `smoke-mcp-auth.py`, `McpDockerGateTests` |
| Non-root `0700`/`0600` volume, missing login, uncertain renewal after restart, isolated cleanup | `smoke-mcp-image.py`, `McpLoginTests`, `McpTokenClientTests` |
| Read/write scope, exact resource, no operator fallback, foreign account/tenant/family | `McpAuthorizationTests`, `McpUserToolsIntegrationTests`, `McpTrackingWriteTests` |
| Pending deletion, provider/global/individual revocation, independent browser logout | `McpConnectionRevocationTests`, `McpConnectionManagementTests`, `McpClientRevocationTests` |
| Consent/issuance/write/renewal races, replay and rollback on both databases | `McpDeviceConsentTests`, `McpBrowserConsentTests`, `McpTokenLifecycleTests`, `McpGrantAdmissionTests` |
| Fresh and historical SQLite/PostgreSQL upgrades and downgrade/re-upgrade | `McpGrantContractTests` |
| Production opt-in/off/invalid settings and trusted proxy handling | `McpDeviceStartupTests` |
| Browser origin, cookie, consent output and no decision replay | Frontend security contracts; browser fixture review in #71/#74 |

## Tested protocol and client boundary

The source pins **ModelContextProtocol 2.3.1**, **OpenIddict 7.8.0** and stable **.NET 10**.
The Docker driver is `gamehub-auth-gate` **1.0.0**, a line-oriented JSON-RPC 2.0 client negotiating
MCP **2025-06-18**. The separate image smoke uses the same protocol version. Runtime architecture,
source commit and hosted check URLs belong in #75/the delivery pull request. Do not label an
unexecuted desktop assistant or protocol version as tested: Codex, Claude Desktop, Cursor and
Visual Studio Code configuration examples are setup guidance, not a native-client test matrix.
Remote Streamable HTTP interoperability remains [#80](https://github.com/happticde/gamehub/issues/80).

The automated identity-provider boundary begins with persisted verified fixture provenance and a
signed browser session. A real provider sign-in and the deployment's browser/proxy path must still
be checked by the operator before enablement. Follow the [browser deployment settings and route
table](mcp-browser-consent.md#server-transport-and-public-routing). Never expose `/connect/verify`
publicly. Core's TLS listener or internal TLS proxy must be trusted by the browser adapter; the
public issuer and exact `/api` resource must also be trusted from the Docker host. Do not disable
TLS validation or follow redirects to make this work. The public proxy must forward only its
configured scheme/host, and Core trusts only the configured one-hop proxy addresses.

## Persistent keys, backup and recovery

Keep these boundaries independent:

| Material | Storage and responsibility |
| --- | --- |
| Core signing, encryption and key-ring protection private certificates | Three different valid RSA keys, at least 2048 bits; absolute private mounts; restrict to the Core identity |
| Core protocol data-protection ring | Dedicated persistent encrypted directory; same ring, protection keys and application identity across Core replicas/restarts |
| Browser token, OIDC adapter, MCP consent adapter, operator and Ingest secrets | Separate existing secret settings; no reuse with protocol keys or client storage |
| Docker client credentials | Dedicated local named volume; `0700` directories and `0600` files, stable runtime owner, working locks and atomic rename |

The client volume is **unencrypted bearer storage**. Host/Docker administrators and readable
backups can impersonate that connection. Use host disk/backup encryption and private access; keep
the volume off network filesystems with weaker locking/rename semantics. An encrypted keychain
requirement needs a separate supported store. Mount no Core private key into the client.

GameHub's library JSON backup deliberately excludes authentication secrets and protocol state.
For disaster recovery, stop admission/writes and take a consistent database backup plus the full
protocol ring, current signing/encryption/protection private certificates, retained protection
certificates and independent server secret configuration. Encrypt backups separately. Record the
issuer, exact resource, certificate identities/expiry, ownership, source version and restore point
without recording secret values. Test restore in an isolated deployment with no provider callback
or outbound sync. Restore the database and matching keys as one unit; do not merge independently
restored rings or treat old credential-volume snapshots as current rotating tokens.

After restoring an older database, previously revoked grants could exist again. Keep user access
closed and invalidate affected account sessions/grants through the supported operator/account
procedures before reopening. Clients whose refresh state may have rolled back must logout and
login afresh. Never remove `refresh.pending` to force replay. If material is missing or compromised,
keep admission closed, revoke affected connections, provision independent replacement material
and require fresh login; do not claim seamless token recovery.

## Rotation

The deployment currently loads one signing certificate and one encryption certificate. It does
**not** expose an overlapping retired signing/encryption key set. Plan a maintenance window:
stop new admission, revoke affected connections while the old keys are still usable, stop protocol
traffic, back up the matching database/ring/key set, replace the selected key with a new independent
valid RSA private certificate, then restart all replicas consistently. Verify fresh login, renewal
and revocation before reopening. Users must login again; do not promise uninterrupted rotation.
Preserve the old recovery material securely until the rollback window is explicitly closed.

Ring-protection certificate rotation has a separate overlap mechanism:

1. Back up the ring and database consistently. Provision a new independent protection certificate.
2. Set `Mcp:Device:ProtectionCertificate` to it and include old absolute certificate paths under
   `Mcp:Device:RetiredProtectionCertificates`. The current and retired files use the configured
   `ProtectionCertificatePassword`; prepare compatible secret provisioning before switching.
3. Restart every replica with the same new/retired set. Verify a pending consent and an existing
   connection across restart, then new login/renewal/revocation. Keep signing/encryption keys stable.
4. Retain old decryption certificates as long as any retained ring file or backup needs them.
   The setting does not re-encrypt historical ring files. Do not delete an old certificate merely
   because the ten-minute access lifetime elapsed; grants have a thirty-day absolute limit and
   backup retention can be longer.

An issuer/resource change or public-client trust drift is a new trust deployment, not key rotation.
Keep the [configuration boundary](mcp-browser-consent.md#operator-settings) and
[accepted authorization decision](adr/016-mcp-user-authorization.md) intact.

## Human release and enablement handoff

1. Merge the verified acceptance pull request. Recheck the exact `main` candidate, including changes
   outside the milestone, and record required green checks and any limitations in #75. Only then
   announce **Ready for manual release**. A later candidate requires reassessment.
2. Run the existing manual [Release workflow](ci-and-releases.md#release-a-version). Confirm its
   `publish-release` job, tag/source commit, published release and both MCP image architectures.
   Record their URLs in #75; close the milestone only after verified publication.
3. Publish the handbook through the separate manual documentation workflow. Deploy matching Core,
   frontend and MCP versions. Stock Compose does not automatically supply the opt-in settings or
   private mounts; the operator supplies them without committing secret configuration.
4. Verify existing OIDC and, if desired, the separate [guarded multi-user admission gate](guarded-multi-user-enablement.md).
   Configure independent persistent keys and exact HTTPS/proxy routing first, then explicitly opt
   Core and the browser adapter into device authorization. This does not switch guarded mode on.
5. With disposable accounts, execute real provider sign-in → `/mcp/verify` → read-only consent →
   assistant restart → read. Then approve tracking on a different connection, check both write
   tools and foreign-ID denial, revoke one connection in Admin Security, and check that another
   connection and the browser remain valid. Include local logout, provider/global logout and
   pending deletion in the deployment acceptance. Never copy codes/tokens into the issue.
6. Before broader enablement, record the deployment/client versions, result and accepted limitations.
   On failure, stop admission and investigate; never fall back to an operator key or relax trust.

API-key compatibility remains an explicitly separate mode. This local release does not deliver
remote OAuth discovery, HTTP MCP, client registration or a public remote endpoint.
