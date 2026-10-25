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
- Follow [authentication](authentication.md) and the
  [guarded activation procedure](authentication.md#check-and-activate-guarded-admission).

OIDC configuration does not itself enable production multi-user operation. The default-off
operator switch and readiness checks remain mandatory. For a self-hosted identity provider,
use the detailed [Keycloak guide](keycloak.md).

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

## Use published release images

Set `GAMEHUB_VERSION` to a published release tag, such as `v0.2.0`.
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
For a source build, clone the repository, check out your intended release tag, supply the same
required configuration, and run `docker compose -f docker-compose.yml up -d --build` from that
checkout. Keep a development checkout separate from your installed release and its volumes.

## Persistence and databases

The baseline uses `postgres_data` for PostgreSQL and `gamehub_data` for Core/Ingest SQLite paths.
Keep these volumes when recreating containers. A library JSON export is not a database backup.
Review [Installation recovery](disaster-recovery.md) and test recovery before relying on the deployment.

Database provider and connection settings are service-specific. SQLite is suitable for a local
installation; PostgreSQL operations require the corresponding database and connection string.
Review provider-specific migrations before changing an existing deployment's database provider.

## Updates and recovery

Follow [Update GameHub](upgrades.md) for source-build and release-image commands.
Create a consistent [installation backup](disaster-recovery.md) before upgrades.
A [library JSON export](backup-restore.md) alone does not recover provider credentials.

## Operational checks

Check Core and Ingest `/health`, frontend login, provider status, sync history, and storage growth.
After a configuration change, recreate the affected containers; a restart alone keeps their old
environment. Use [Troubleshooting](troubleshooting.md) to diagnose failures without deleting data.
