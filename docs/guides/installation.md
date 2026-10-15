# Install GameHub

## Before you start

You need Git, Docker with Docker Compose, and enough persistent storage for the databases and
downloaded metadata. Docker builds the services; host installations of .NET, Go, and Node are not
required for a Compose installation. Obtain IGDB/Twitch application credentials if you want
metadata enrichment. Platform connections are configured after installation.

Choose a local test installation first, or follow [Deployment](deployment.md) for an internet-facing
instance. Keep an unauthenticated test instance on a trusted machine and network.

## Get the source matching your version

```bash
git clone https://github.com/happticde/gamehub.git
cd gamehub
git checkout vX.Y.Z
```

Replace `vX.Y.Z` with an actual published release. Access to a private repository requires GitHub
authentication. If you intentionally want the development version, use `main` and select Next in
this handbook. Available release artifacts and checksums are described in
[CI and releases](../ci-and-releases.md).

## Supply configuration

Use your deployment configuration or secret manager to provide the variables listed in
[Configuration](configuration.md). Environment files, if you choose to use them yourself, are
local operator-owned files and must never be committed or included in support reports.

For an authenticated local installation, configure independent GameHub API, internal delegation,
JWT signing, and credential-vault keys, and enable frontend authentication. Keep
`GAMEHUB_MULTI_USER_ENABLED=false` during initial setup. Set `GAMEHUB_AUTH_COOKIE_SECURE` according
to whether the browser actually reaches the frontend through HTTPS. Never reuse one secret for
all trust boundaries.

The Compose baseline defaults Core to PostgreSQL and Ingest to SQLite. Supply a strong PostgreSQL
password before starting. The configuration reference explains both database providers.

## Start and check the services

```bash
docker compose up -d --build
docker compose ps
```

| Service | Local address | Check |
| --- | --- | --- |
| Frontend | `http://localhost:3000` | Open the login or landing page |
| Core | `http://localhost:5001` | `GET /health`; Swagger at `/swagger` |
| Ingest | `http://localhost:5000` | `GET /health`; Swagger at `/` |
| PostgreSQL | `localhost:5432` | Compose health status |

The baseline publishes these ports. For production, restrict backend/database access and expose
the frontend through an HTTPS reverse proxy. A container being started is not the same as being
healthy; use [Troubleshooting](../DEBUGGING.md) if startup dependencies do not become healthy.

## Create the initial local account

In local JWT compatibility mode, bootstrap the default owner once through Core Swagger's
**Authorize** dialog and `POST /api/auth/bootstrap`. Use your operator API key and a unique
password. Then sign in at `/login`.

Production OIDC account linking and provisioning follow a different rollout procedure. Continue
with [Authentication](../authentication.md) and [Guarded multi-user operations](../guarded-multi-user-enablement.md)
before exposing that deployment to users.

## Continue

Follow [Your first library](first-library.md) to connect a platform, retrieve its games, and
verify the unified collection. Keep both persistent volumes when restarting; do not use
`docker compose down -v` as a troubleshooting shortcut.
