# Simplified MCP setup release acceptance

The [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) setup improvements form an
independent release: [Simplified setup](https://github.com/happticde/gamehub/milestone/4), final
acceptance [#118](https://github.com/happticde/gamehub/issues/118). The owner requested this split
on 2026-11-17. A technical prerequisite for remote access can deliver useful setup improvements
before the remote feature is available.

## Release scope

| Included | Evidence |
| --- | --- |
| Approved deployment/key boundary and bounded operator inputs, #112 | Human-merged #115 at `5be04c2` |
| Durable provisioning, adoption, retained keys and maintenance, #113 | Human-merged #116 at `fd355dc`; [key reference](mcp-managed-keys.md) |
| Single source/image Compose addition, gateway, browser consent, readiness and Caddy guide, #114 | Human-merged #117 at `1143c47`; [deployment evidence](mcp-deployment-verification.md) |
| Existing Docker user login, scoped tools, renewal and revocation | [Local acceptance](mcp-local-release-gate.md), plus #114's new deployment boundary |
| This separate release plan and final candidate handoff | #118 and its linked documentation pull request |

The [operator guide](guides/mcp-deployment.md) is the supported setup path. Existing provider
settings, browser sessions, account admission and database volumes retain their boundaries.
Setup derives matching addresses and generates independent service credentials in protected
state volumes; operators do not generate or copy certificates. It does not change the existing
guarded multi-user switch or enable remote transport.

Native remote access, new authorization/discovery and actual Codex/Hermes remote login in
#76–#80 are excluded. The released assistant connection still uses the existing local Docker
standard-input/output process with explicit user login. Remote work depends on the merged setup,
but its implementation and client acceptance cannot hold this setup release open.

## Reviewed candidate and evidence

The reviewed product baseline is `1143c4730d8d42feef3f83bed313eca52b762376`, after the owner's merge
of #117. The previous published release is
[v0.4.1](https://github.com/happticde/gamehub/releases/tag/v0.4.1), source
`c2e7ec0e0506f5c66addc7d6136327b4161e3967`. The complete difference contains #112–#114, the
ModelContextProtocol `2.4.0` update in #110 and the handbook's Mermaid layout `1.0.2` update in
#109. These dependency updates are included in the candidate and its verification; milestone
membership does not exclude them from the release. No #76–#80 remote runtime is merged into
this baseline.

[Hosted verification on that exact main commit](https://github.com/happticde/gamehub/actions/runs/43997444357)
passed backend/frontend checks, documentation, dependency/configuration and source security,
all eight native image build/startup/security checks, and the aggregate continuous integration
gate. CodeQL analysis also passed on that commit. #113/#114 retain their detailed SQLite and
PostgreSQL, key lifecycle, actual-image and native Node evidence; do not repeat unrelated checks
merely to move tasks between milestones.

The acceptance documentation itself still needs human merge. Before declaring **Ready for manual
release**, reassess its resulting exact `main` commit and confirm the required selected checks
are green. If runtime or dependency changes reach `main`, review the full changed candidate and
rerun the checks its scope requires. Publish this setup candidate before merging later remote
runtime work, preserving the independent publication boundary.

## Explicit limits and operator checks

The production-image fixture covers signed synthetic owners and separate Caddy/backend networks
on one Docker engine. It verifies browser library access, consent, token issuance, allowed reads,
denied writes/private routes, state pairing, recreation and disable/re-enable. It is not a real
provider sign-in or a physical cross-host deployment. Before enabling an installation, the operator
must verify its existing provider login and protected Caddy-to-Frontend connection, follow the
shipped guide, check readiness and private-route rejection, and test its existing Docker user
connection. Native remote-client acceptance remains #80 and is not claimed here.

Preserve a consistent backup of the existing database and both managed state volumes before
adoption or upgrade. Follow the key reference for expiry, renewal, retention and recovery;
unrecoverable key loss does not have a seamless recovery promise. Client credential storage keeps
its existing permission-protected, unencrypted boundary. Setup and release publication do not
change disk encryption, provider configuration or account-admission policy.

## Human publication handoff

1. Merge the reviewed #118 acceptance change, then record the exact final `main` candidate and
   green required checks in #118. Announce **Ready for manual release** only at that point.
2. Run the existing manual [Release workflow](https://github.com/happticde/gamehub/actions/workflows/release.yml)
   on that candidate through `main`, following [release planning](ci-and-releases.md#release-planning).
   The existing workflow calculates the version and runs full verification before publication;
   do not create a version tag or publish assets separately.
3. Confirm `publish-release` succeeded. Record the release URL, tag, matching source commit and
   successful run in #118. Only then close the setup milestone. A green build or merged gate
   alone is not evidence of publication.
4. Publish the matching public handbook through its separate manual workflow. Upgrade and
   enable an installation explicitly using that release's images/files and the operator checks
   above. No remote endpoint is enabled by this handoff.

The remote release retains its own milestone and final gate #80. Setup publication can complete
while those tasks remain open; its operational guarantees remain prerequisites for that later work.
