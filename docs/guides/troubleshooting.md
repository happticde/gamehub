# Troubleshooting

Start with the symptom below. Keep tokens, passwords, cookies, helper results, and raw provider
responses out of support messages. Useful details are the GameHub version, failing step,
sanitized error code, timestamp, and affected platform.

## Installation does not start

1. Run `docker compose ps` from the installation directory.
2. Read the failing service's recent logs:
   ```bash
   docker compose logs --tail=100 frontend core ingest
   ```
3. Check that required configuration names from [Installation](installation.md) are supplied.
4. Check port conflicts and database health. Wait for the initial source build and migrations.
5. After changing environment settings, recreate affected containers with `docker compose up -d`;
   restarting an existing container does not replace its environment.

Run from the folder containing your `compose.yaml`, or include your custom Compose file options.
Do not delete databases or use `down -v` to fix health checks.

## The site is unreachable or the proxy returns 502

Open `http://localhost:3000/login` on the Docker host.
If that works but the public site does not, check the reverse proxy upstream and frontend bind IP.
The default `127.0.0.1` binding is reachable only from the same host.
See [Proxy on another host](deployment.md#run-the-https-proxy-on-another-host).

For a remote Docker host, your laptop's `localhost` is not the server.
Use the configured HTTPS address.

## Login fails

For a local installation, complete the one-time [owner bootstrap](installation.md#5-create-your-first-account)
and use that email/password rather than the operator API key.
If an account already exists, do not reset its database.

For OIDC, compare the exact HTTPS issuer, callback URL, client ID, and provider admission policy.
A provider login does not grant GameHub access unless the account is linked or the configured
personal-account provisioning mode permits it. Check [Authentication](authentication.md).

If your session ended, sign in again. Ask the host to inspect sanitized service errors when login
still fails; do not work around the failure by disabling authentication.

## A page loads but saving returns invalid_request_origin

Set `GAMEHUB_PUBLIC_ORIGIN` to the exact browser origin, including scheme and nonstandard port.
It must match the public HTTPS address behind a proxy, not an internal container hostname.
Recreate the frontend and retry. Keep the origin check enabled.
See [Deployment](deployment.md).

## Games are missing

1. Clear library filters and check ignored entries.
2. Confirm the correct provider account in **Admin → Accounts**.
3. Open **Admin → Sync** and inspect the provider status and latest sync history.
4. Use **Refresh Platforms + Sync** for new provider data.
5. If a credential expired, reconnect that platform and retry once.
6. For Steam, check the account ID and game-details visibility.
7. If ownership imported but the cover is wrong, use [Fix Match](correct-matches.md).

**Sync Cached Library** only uses downloaded records; it cannot discover a new purchase by itself.
A stored credential or successful metadata enrichment is not proof of a successful library import.

## Import fails or is rate-limited

| Symptom | Next step |
| --- | --- |
| Expired/rejected session | Reconnect the affected platform in Accounts |
| `429` or cooldown/deferred status | Wait until retry is allowed |
| Timeout or provider outage | Check history; retry after provider recovery |
| Unexpected provider response | Preserve current data; report the sanitized status and timestamp |
| Wrong grouping or duplicates | Review [Merge and split](merge-split.md) |

**Force Refresh** still respects provider cooldowns. Repeated forced requests do not repair
an expired session or provider outage.

## The connection helper does not finish

1. Use the helper offered by your installed frontend version and the correct Mac architecture.
2. Check that Google Chrome is installed locally.
3. If macOS blocks the installer or CLI, use its explicit approval option for the trusted download
   and rerun the installer. The CLI remains in its stable installed location after a blocked run.
4. Start a fresh **Connect** or **Reconnect** attempt from **Admin → Accounts**.
5. Allow the browser to open GameHub Helper and verify the exact receiving origin in the native prompt.
6. Complete provider login in the opened browser and keep the GameHub dialog open.
7. If capture succeeds but saving fails, correct the shown error and use the dialog's save retry
   where offered. For `invalid_request_origin`, fix the server's public-origin setting first.

Closing a dialog stops frontend waiting but does not stop its local browser/helper process.
Do not reuse an expired handoff URL or an old launch link. For Sony rate limits, wait and reuse
the dedicated persistent Chrome profile. Avoid repeated fresh-profile attempts.

The guided installer supports desktop macOS. On other systems use supported advanced manual
options; do not assume a macOS protocol handler is available.
See [Connection helper](connection-helper.md).

## Covers or descriptions are missing or wrong

Check that the host supplied IGDB credentials, then run normal enrichment in **Admin → Enrichment**.
Digital ownership import can work without IGDB. Physical-game search needs IGDB.
For one incorrect result use [Correct a match](correct-matches.md).

## Backup validation or restore fails

Use a JSON library backup, not CSV. Wait for validation and read the displayed error.
Supported formats are `gamehub-backup` versions 1, 2, and 3; do not change the version number.
Restore requires an empty destination library and does not merge collections.
Check [Backup and restore](backup-restore.md) before confirming another attempt.

For a failed server upgrade or lost credentials, follow [Installation recovery](disaster-recovery.md).
