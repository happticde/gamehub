# Deploy and upgrade GameHub

## Plan the deployment

GameHub has a frontend/BFF, Core, Ingest, and persistent databases. The CLI and MCP server are
clients rather than additional database owners.
Use the standalone [release Compose template](../../docker-compose.images.yml).
Save it as `compose.yaml` in your installation folder.
It downloads published images and uses PostgreSQL for Core and SQLite for Ingest.
Follow [Installation](installation.md) for the first setup.

For a production installation:

- Expose the frontend through a trusted HTTPS reverse proxy.
- Compose binds the frontend to `127.0.0.1` by default. If the HTTPS proxy runs on another host,
  set `GAMEHUB_FRONTEND_BIND_IP` to a reserved private/VPN address on the GameHub host and restrict
  port 3000 to the proxy host. Keep Core, Ingest, and PostgreSQL on loopback.
- Keep Core, Ingest, PostgreSQL, and internal service-key routes on private networks.
- Supply strong independent secrets through your deployment system.
- Keep databases and vault keys on persistent, backed-up storage.
- Follow [authentication](../authentication.md) and the
  [guarded activation procedure](../guarded-multi-user-enablement.md).

OIDC configuration does not itself enable production multi-user operation. The default-off
operator switch and readiness checks remain mandatory. For a self-hosted identity provider,
use the detailed [Keycloak guide](../keycloak-oidc-setup.md).

## Run the HTTPS proxy on another host

Supply these settings through your deployment environment, replacing the example private IP
with a reserved address on the GameHub host and the example origin with the browser-facing URL:

```text
GAMEHUB_FRONTEND_BIND_IP=192.168.1.20
GAMEHUB_PUBLIC_ORIGIN=https://games.example.com
GAMEHUB_AUTH_COOKIE_SECURE=true
```

Point the proxy upstream at `http://192.168.1.20:3000` and restrict port 3000 to the proxy host
through your network firewall. Publishing on `0.0.0.0` also accepts connections on other IPv4
interfaces. Docker-published ports can bypass ordinary UFW rules; use network filtering or
[Docker-aware firewall rules](https://docs.docker.com/engine/network/packet-filtering-firewalls/).

The bind IP controls how the proxy reaches GameHub. The public origin independently controls
which browser origin may submit writes, including provider credential storage. Recreate the
frontend after changing these settings:

```bash
docker compose up -d --no-deps --force-recreate frontend
```

If the public domain returns a proxy `502` while `/login` returns `200` on the GameHub host,
check the proxy upstream and the frontend bind IP. The default loopback binding accepts only
connections local to the GameHub host.

If a provider helper captures a result but saving returns `invalid_request_origin`, verify that
the frontend's `PUBLIC_ORIGIN` matches the exact external HTTPS origin. Compose supplies it from
`GAMEHUB_PUBLIC_ORIGIN`, whose default is `http://localhost:3000`. An existing signed-in session
can still render pages while writes are rejected. Keep the origin check enabled and correct the
deployment setting before retrying the save.

## Choose a release

Set `GAMEHUB_VERSION` to a published release tag, such as `v0.1.0`.
The release Compose file uses that tag for all three images:

```text
ghcr.io/happticde/gamehub-core:vX.Y.Z
ghcr.io/happticde/gamehub-ingest:vX.Y.Z
ghcr.io/happticde/gamehub-frontend:vX.Y.Z
```

Replace the example tag with an existing release. Release images support Linux AMD64 and ARM64.
Keep numbered tags in deployment configuration; `latest` changes when another release is
published. Private packages require authenticated registry access.

Run `docker compose pull`, then `docker compose up -d --no-build`.
The file is complete on its own. It needs no Compose override or source checkout.
The repository's separate `docker-compose.yml` is for
[source builds](development.md#build-from-source).

## Persistence and databases

The baseline uses `postgres_data` for PostgreSQL and `gamehub_data` for Core/Ingest SQLite paths.
Keep these volumes when recreating containers. A library JSON export is not a database backup.
Review [Backup and restore](backup-restore.md) and test recovery before relying on the deployment.

Database provider and connection settings are service-specific. SQLite is suitable for a local
installation; PostgreSQL operations require the corresponding database and connection string.
Review provider-specific migrations before changing an existing deployment's database provider.

## Upgrade safely

1. Read the target release notes and select the same handbook version.
2. Back up both databases, configuration, and required key versions consistently.
3. Stop or drain writes and background jobs according to your maintenance procedure.
4. Update all application components and helper downloads together to the selected release.
5. Start the services and inspect startup/migration logs and health checks.
6. Verify login, the intended account's library, credential status, and one retrieval/sync cycle.

For the release Compose installation, change `GAMEHUB_VERSION` to the target tag.
Then run these commands from the same installation folder:

```bash
docker compose pull
docker compose up -d --no-build
docker compose ps
```

If you move an existing installation to this template, keep its Compose project name.
Use `docker compose -p YOUR_EXISTING_PROJECT_NAME` for each command when needed.
Keep the existing volume names and database paths too.
A different project name creates new volumes and can make the library appear empty.
The release template defaults Ingest's SQLite path to `/app/data/gamehub.db`.
Set `INGEST_DB_CONN` to your existing path if it differs.

The frontend embeds the helper downloads from its own release. An old installed helper can
disagree with a newer frontend's launch protocol; reinstall the matching package when release
notes require it.

## Rollback and recovery

Application images and database schema must remain compatible. If an upgrade changed the schema,
reverting the image alone is not a verified rollback. Restore a matching, tested database backup
when required by the migration/release notes.

Disabling guarded multi-user admission preserves tenant data but changes which accounts and jobs
may run. Use the documented [disable and recovery procedures](../guarded-multi-user-enablement.md)
rather than weakening authentication to regain access.

## Operational checks

Check Core and Ingest `/health`, frontend availability, provider retrieval history, sync status,
and storage growth. Keep logs sanitized and never include cookies, tokens, vault keys, or raw
provider responses in support material. [Troubleshooting](../DEBUGGING.md) separates provider,
network, and metadata failures.
