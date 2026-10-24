# Install GameHub

## Before you start

You need Docker with Docker Compose and storage for the databases and game metadata.
GameHub provides ready-made images for Linux AMD64 and ARM64.
You do not need Git, the source code, or local build tools.

Start with a local installation. Login is enabled by default.
For a public server, also follow [Deployment](deployment.md).

## Create your installation folder

```bash
mkdir gamehub
cd gamehub
```

Open the [release Compose template](../../docker-compose.images.yml) on GitHub.
Use **Download raw file** and save it in this folder as `compose.yaml`.
You only need this one file. It contains all four services and their health checks.
It uses published images and has no build steps or local source paths.

Choose a version from [GitHub Releases](https://github.com/happticde/gamehub/releases).
Set `GAMEHUB_VERSION` to its full tag, such as `v0.1.0`.
All three GameHub images use this same version. Select the matching handbook version.
Use a numbered release for a predictable installation. The `latest` tag can change.

## Supply configuration

Supply these variables through your deployment tool or shell environment.
Docker Compose can also read a local `.env` file that you create yourself next to `compose.yaml`.
Keep that file private and out of Git. Never include it in support reports.

| Required variable | Value to supply |
| --- | --- |
| `GAMEHUB_VERSION` | A published tag, such as `v0.1.0` |
| `GAMEHUB_API_KEY` | A random API key, at least 32 characters |
| `GAMEHUB_INGEST_SERVICE_KEY` | A different random key for Core-to-Ingest requests, at least 32 characters |
| `GAMEHUB_JWT_SIGNING_KEY` | A different random signing key, at least 32 characters |
| `GAMEHUB_CREDENTIAL_KEY_1` | Exactly 32 random bytes encoded as Base64 |
| `POSTGRES_PASSWORD` | A strong database password |

Use a password manager to create and store separate secrets. For API and service keys,
use printable ASCII characters without spaces. Do not reuse a secret for another setting.
Keep the vault key with your backups. You need it to read stored platform credentials.

Keep `GAMEHUB_MULTI_USER_ENABLED=false` during setup.
The default browser address is `http://localhost:3000`.
Set `GAMEHUB_PUBLIC_ORIGIN` if you use another address.
For HTTPS, also set `GAMEHUB_AUTH_COOKIE_SECURE=true`.

IGDB metadata needs `IGDB_CLIENT_ID` and `IGDB_CLIENT_SECRET` from a Twitch application.
You can add these later. Connect Steam, GOG, PlayStation, or Epic after signing in.
See [Configuration](configuration.md) for all settings.

## Download the images

Run all commands from your installation folder:

```bash
docker compose config --quiet
docker compose pull
```

The first command checks your configuration. It reports missing required variables.
The second downloads the GameHub images and PostgreSQL.

Public images need no registry login. Private images need a GitHub account with package access.
In that case, run `docker login ghcr.io --username YOUR_GITHUB_USERNAME` before pulling.
At the password prompt, use a personal access token (classic) with `read:packages`.
See [GitHub's registry guide](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-container-registry).
If pulling reports `unauthorized` or `denied`, check the login and your access to all three packages.
Then run `docker compose pull` again.

## Start and check the services

```bash
docker compose up -d --no-build
docker compose ps
```

| Service | Local address | Check |
| --- | --- | --- |
| Frontend | `http://localhost:3000` | Open the login or landing page |
| Core | `http://localhost:5001` | `GET /health`; Swagger at `/swagger` |
| Ingest | `http://localhost:5000` | `GET /health`; Swagger at `/` |
| PostgreSQL | `localhost:5432` | Compose health status |

Ports bind only to `127.0.0.1` by default. Core uses PostgreSQL. Ingest uses SQLite.
The first start creates the databases and applies migrations. Wait until the services are healthy.
Use [Troubleshooting](../DEBUGGING.md) if a service fails its health check.

## Create the initial local account

Open Core Swagger at `http://localhost:5001/swagger`.
Click **Authorize** and enter your `GAMEHUB_API_KEY`.
Call `POST /api/auth/bootstrap` to create the first local account.
Choose a unique password. Then sign in at `http://localhost:3000/login`.

Public servers use a separate login setup through an external provider (OIDC).
Follow [Authentication](../authentication.md) and [Guarded multi-user operations](../guarded-multi-user-enablement.md)
before allowing public access.

## Update an installation

Read the release notes and make a backup first.
Change `GAMEHUB_VERSION` to the new release tag. Keep your existing secrets.
Then run:

```bash
docker compose pull
docker compose up -d --no-build
docker compose ps
```

Use [Deployment and upgrades](deployment.md#upgrade-safely) for the full update procedure.

## Continue

Follow [Your first library](first-library.md) to connect a platform, retrieve its games, and
verify the unified collection. Keep both persistent volumes when restarting; do not use
`docker compose down -v` as a troubleshooting shortcut.

To build GameHub from source, use the separate [development guide](development.md).
