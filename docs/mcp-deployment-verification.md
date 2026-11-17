# Managed MCP deployment verification

Task [#114](https://github.com/happticde/gamehub/issues/114), release
[Remote access](https://github.com/happticde/gamehub/milestone/2), acceptance
[#80](https://github.com/happticde/gamehub/issues/80). **Not ready** until the required tasks are
human-merged and the final real-client/operator acceptance passes. The associated pull request
records the exact checked commit and hosted checks. Baseline: human-merged #116 at `fd355dc`.

## Delivered boundary

The [operator guide](guides/mcp-deployment.md) ships the single Compose addition, source/image
entry points, private state loaders, generated gateway admission, existing device consent,
readiness command and Frontend-only Caddy site. Native remote transport, authorization-code
consent, discovery and named-client interoperability remain #76–#80. No real provider, deployment,
publication or account-admission change is included in this evidence.

## Docker evidence

All application builds and tests ran in Docker. No dependency or lockfile changed.

| Check | Result |
| --- | --- |
| Full .NET Release suite with real SQLite and disposable PostgreSQL | 1,079 passed |
| Final managed deployment cases on both providers | 3 passed; HTTP-to-HTTPS context, duplicate/wrong proof, wrong generation/consent, signed owner and resource boundaries |
| Frontend security suite | 93 passed, including actual native Node requests and private-file rejection |
| Frontend lint and production build | Passed |
| Actual Core and Frontend production images | Built and ran as their existing non-root identities |
| Source Compose setup, then image-entry-point setup using a local fixture tag | Both ready; same named state volumes and existing identities reused |
| Effective Compose merge | Existing provider/database configuration, data volumes and loopback ports retained; setup has only its two state mounts and no ports/dependencies |
| Caddy 2.11.9, Node 26.13.1, separate edge/backend networks | Browser library, consent preview/approval, token issuance and scoped reads passed through actual images |
| Private paths and unapproved writes | Private/subpath routes 404; read-only token cannot write |
| Full container recreation and newer Frontend build, retaining database/state | Existing browser and integration connection remain usable |
| Disable, recreate, then re-enable with preserved state | Browser library retained, device route closed while disabled, existing connection usable after re-enable |
| Actual-image readiness diagnostics | Six bounded success/failure cases; disabled, origin/provider prerequisites, mismatched generation and mismatched consent; no key values printed |
| Public handbook | 16 contracts and production/search/publication-boundary build passed |
| Repository CI tooling tests | 75 cases, 20 tool-dependent skips; dependency/image guard and whitespace checks passed |

The first full test attempt exhausted Docker disk capacity. After removing rebuildable buildcache,
the final run used a named disposable database (not PostgreSQL's system database) and passed in full.
These were verification-environment corrections. No production data, images or state volumes were
removed to resolve them.

## Reproducing the image boundary

`McpDeploymentImageGateTests.cs` contains two explicit opt-in tests. They return without fixture
work in ordinary suites; ordinary green tests alone are not image-gate evidence.

1. In the development container, set `GAMEHUB_MANAGED_SEED_DIRECTORY` to a dedicated private
   temporary directory and run the `DeploymentImages_SeedDisposableDatabase` test. It prepares
   only a synthetic SQLite database and private signed browser fixture. Never use a real database.
2. Use the shipped Compose setup for a dedicated project at `https://games.example`. Put the
   fixture database in that project's disposable application-data volume; set the existing
   authentication/provider settings to the matching synthetic values from `StartupFixture`.
   Core uses its managed state volume; Frontend sees only its own read-only state volume.
3. Run the actual Core/Frontend images on a private backend network. Put Caddy and Frontend on
   a separate edge network; Caddy has no backend-network access or Core upstream. Adapt the shipped
   Caddy site to `games.example` and the fixture Frontend name. Only the fixture adds Caddy's
   `tls internal`; production keeps its normal trusted HTTPS certificate.
4. Make `games.example` resolve to Caddy for the test container. Copy only Caddy's public fixture
   root certificate to `root.crt` beside the seed receipt. Set `GAMEHUB_MANAGED_GATE_DIRECTORY`
   to that directory and run `DeploymentImages_CaddyAndNativeFrontend_ConsentPrivateRoutesAndRecreation`.
   It verifies the hostname and complete certificate chain against that root, without disabling
   transport-security enforcement or general certificate validation.
5. Recreate Core/Frontend with the same volumes and run the gate again. Its private connection
   receipt preserves the original integration token, proving continuity. Disable both managed
   switches, verify browser access and the closed device route, then re-enable and repeat the gate.
6. Run `node scripts/mcp-readiness.mjs` inside the actual Frontend image. Test wrong-generation
   and wrong-consent state using a separate private temporary copy, preserving the initialized
   volumes. Dispose only the dedicated fixture containers, networks, volumes and private receipts.

Tests use server-side browser cookies and verified synthetic owner/session provenance over real
HTTP routes. They do not automate a real provider sign-in or claim real Codex/Hermes acceptance.
The separately networked Caddy fixture exercises the approved separate-host routing boundary on
one Docker engine; deployment over an operator's actual private cross-host connection remains
part of #80. Readiness proves service wiring and storage, not public exposure or provider reachability.
