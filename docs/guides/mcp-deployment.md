# Prepare the MCP server

This is the supported server preparation path for [Model Context Protocol (MCP)](https://modelcontextprotocol.io/)
user connections in versions containing [#114](https://github.com/happticde/gamehub/issues/114).
It reuses your existing GameHub installation, database, provider sign-in and Caddy upstream.
Native remote assistant login at `/mcp` remains unavailable until the
[Remote access acceptance gate](https://github.com/happticde/gamehub/issues/80) is merged and verified.
Preparing these files does not publish or enable that future transport.

## Before setup

Your browser must already sign in through an external [OpenID Connect provider](https://openid.net/specs/openid-connect-core-1_0.html)
at the public HTTPS GameHub origin, with secure cookies. Preserve the existing provider settings,
account admission, guarded multi-user switch, project name and volumes. This setup changes none of them.
Caddy may run on another server; its existing connection to Frontend must be restricted to that
proxy over a trusted private network or encrypted tunnel. Secure that website connection first.

Use the same release's base Compose file, `compose.mcp.yaml`, Core and Frontend images.
For a published installation, obtain the files from that release's included
`gamehub-source-VERSION.tar.gz`, keeping your existing base filename `compose.yaml` and installation
directory. Update the installed base from `docker-compose.images.yml`, preserving your deployment
customizations. Older base files lack the matching `mcp-setup` image definition. Never download
setup files from a newer development branch for an older release. Source installations already
contain both entry points.

The shipped files are:

| Location | Purpose |
| --- | --- |
| `docker-compose.yml` | Source build entry point, including the dormant setup build |
| `docker-compose.images.yml` (installed as `compose.yaml`) | Published images at your existing selected release tag |
| `compose.mcp.yaml` | The single MCP addition for either entry point |
| `deploy/Caddyfile.mcp` | Complete site example using the existing Frontend upstream |
| `frontend/scripts/mcp-readiness.mjs` | Installed inside Frontend; checks both services without printing secrets |

Only explicit enablement and your existing canonical public origin are new setup inputs. Keep
that origin byte-for-byte consistent with `GAMEHUB_PUBLIC_ORIGIN` and your working provider callback;
for example `https://games.example.com`, without a trailing slash. A non-default HTTPS port belongs
to the origin. Setup never reads or writes environment files. No secret value belongs in a command,
Compose addition or Caddy file.

## Prepare state, then deploy

From the existing installation directory, with the existing Compose project name, run:

```bash
docker compose -f compose.yaml -f compose.mcp.yaml run --rm --no-deps mcp-setup \
  --enable --public-origin https://games.example.com
```

For source installations replace `compose.yaml` with `docker-compose.yml` and add `--build`
to `run` to build the matching setup image. For the repository image entry point use
`docker-compose.images.yml`. Continue using your existing `-p PROJECT` or Compose project selection
on every command; a different project creates different data volumes.

Expected setup output is `{"Code":"ready","DaysRemaining":...}` and exit status `0`.
The short-lived setup runs as root only to assign the existing Core and Frontend service owners.
It prepares two private named volumes without starting Core, changing Caddy or contacting a provider.
Repeating the same command validates and reuses the existing identities. A different origin,
partial state or damaged initialized storage fails instead of replacing keys.

Review the addition, then recreate Core and Frontend through your normal deployment process:

```bash
docker compose -f compose.yaml -f compose.mcp.yaml up -d core frontend
docker compose -f compose.yaml -f compose.mcp.yaml exec frontend node scripts/mcp-readiness.mjs
```

Source deployments add their normal `--build` to `up`. The addition enables managed wiring in both
services; it leaves remote transport and the existing multi-user/account-admission choice alone.
Readiness exits `0` with `code: ready` when provider prerequisites, origin, private state and both
generated service credentials/generation agree with running Core. It is not a public Caddy,
provider-availability or native-client acceptance test. Never print private state or a merged
configuration containing your existing deployment secrets as a diagnostic shortcut.

## Update the existing Caddy site

Use the complete shipped `deploy/Caddyfile.mcp` as the site definition. Replace
`games.example.com` in both the site address and `header_up Host`, and replace
`FRONTEND_PRIVATE_ADDRESS:3000` with the existing protected Frontend upstream. Preserve any existing
upstream transport/timeouts required by your protected connection. A non-default public port must
also appear in the canonical upstream Host. Caddy supplies normal public certificates; no new
private certificate authority, OpenSSL commands, copied trust file or MCP credential is needed.

The site blocks private consent and readiness paths, strips incoming internal proof headers and
keeps the existing Frontend destination. Frontend independently denies private/unknown protocol
routes, including trailing-slash/subpath variants. Core requires generated transport proof before
restoring the canonical HTTPS context; the separate consent credential and signed browser owner
remain mandatory. Native Node requests use the internal Host, so a manually copied public Host
header is not the service-trust mechanism.

Validate and reload Caddy using your existing service procedure, for example on the proxy server:

```bash
caddy validate --config /etc/caddy/Caddyfile
caddy reload --config /etc/caddy/Caddyfile
```

Keep access logs disabled for this site; protocol queries, codes, authorization headers and bodies
must not enter request logs. See the official
[Caddy reverse-proxy reference](https://caddyserver.com/docs/caddyfile/directives/reverse_proxy).
After the explicit deployment/reload, check your usual browser library and provider sign-in.
`/mcp/verify` should lead through normal sign-in. Public `/connect/verify`, its subpaths,
`/connect/authorize/consent` and `/internal/mcp/readiness` must return `404`. `/mcp` is still `404`
in this delivery. The existing Docker client uses the same public issuer and `/api` resource;
the gateway supports only the delivered device/token/revocation and scoped library operations.

## Diagnose readiness

The command prints one bounded code and concrete fix, with no keys, paths, accounts or tokens.

| Code | Action |
| --- | --- |
| `disabled` | Include the MCP addition for both service deployments after setup. |
| `managed_storage_invalid` | Check the matching mounted volume and owner. Initialize only fresh storage; restore a consistent backup for an initialized installation. |
| `configuration_conflict` | After verified adoption, remove the old MCP override. Keep provider settings intact. |
| `provider_prerequisites` | Restore working provider sign-in, secure cookies and complete existing browser configuration. |
| `origin_mismatch` | Match setup's canonical origin to the working public origin/provider callback. |
| `invalid_core_destination` | Restore the existing private `CORE_URL` origin; do not use a public proxy or a URL path. |
| `core_unavailable` | Check Core health, service networking, matching image version and inclusion of the addition. |
| `pairing_rejected` | Recreate both services with their matching state volumes. Never regenerate initialized stores to hide a mismatch. |
| `renewal_due` | Run the supported key renewal, then recheck. |
| `managed_storage_unavailable` | Inspect Core storage permissions or restore a matching database/key backup. |

## Persistence, upgrades and disabling

The project-scoped `mcp_core_state` volume contains Core's private keys and persistent protection
ring. Core mounts it read-write as `1654:1654`. The separate `mcp_frontend_state` contains only its
paired configuration/credentials, mounted read-only as `1001:1001`. Private directories use `0700`,
files `0600`. Frontend never mounts Core certificates. Both survive ordinary service recreation.
Back up both volumes and the existing Core database consistently, with encrypted, restricted
backups. Missing keys cannot be repaired by restoring only the database.

Upgrade all matching images/files together, keep the same project/volumes, recreate the services,
and rerun readiness. Repeated setup is optional validation, never an automatic key reset.
Key renewal runs automatically while Core is up; explicit maintenance uses:

```bash
docker compose -f compose.yaml -f compose.mcp.yaml exec core dotnet GameHub.Core.dll mcp-keys check
docker compose -f compose.yaml -f compose.mcp.yaml exec core dotnet GameHub.Core.dll mcp-keys renew
```

Core detects offline renewal on its next hourly check or normal restart. Retained decryptors
preserve grants across supported renewal/recreation. For retention limits, expiry during prolonged
downtime and recovery, see the
[managed-key reference](https://github.com/happticde/gamehub/blob/main/docs/mcp-managed-keys.md).

To disable managed MCP, recreate Core and Frontend using only the original base file and remove
the optional Caddy protocol exposure. Keep both state volumes and the database. Browser/provider
settings remain as before. Do not run `down -v` or delete initialized keys. Re-enabling uses the
same addition/state and readiness check; disabling is not revocation, so explicitly revoke unwanted
connections before disabling if they must remain unusable after re-enablement.

## Existing manual installations

Keep the legacy path until scheduled migration. Stop Core and Frontend, preserve rollback files,
and take a consistent database/certificate/ring backup. The same setup service supports
`--adopt-existing /run/legacy/import.json` with explicit read-only legacy mounts and the private
descriptor documented in the
[managed-key adoption reference](https://github.com/happticde/gamehub/blob/main/docs/mcp-managed-keys.md#adopting-a-manual-installation).
Those one-time mounts are the migration exception to the fresh setup budget. Never paste keys
into command arguments, edit environment files with a generator or initialize over existing state.

After verified import, replace the legacy MCP override with `compose.mcp.yaml`, retaining every
existing provider/database setting. Remove conflicting legacy MCP environment entries from your
deployment override; the generator does not edit them. Use the Frontend-only Caddy site and verify
readiness, browser access and an existing connection before removing the old Core HTTPS listener,
aliases and trust mounts. Retain legacy private files through your tested rollback window.
No database migration, account provisioning or automatic connection revocation is part of setup.
Merge, product release, documentation publication and actual deployment remain operator actions.
