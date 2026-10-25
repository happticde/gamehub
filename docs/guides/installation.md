# Install GameHub

This guide gets a local installation running and takes you through the first login.
For a server reached through a domain, complete [HTTPS deployment](deployment.md) before using it
remotely. Keep production account admission disabled until you finish [authentication setup](authentication.md).

## 1. Check the prerequisites

Install Docker with the Docker Compose v2 plugin. Docker must be running.
You do not need Git, a source checkout, or host installations of .NET, Go, or Node.

```bash
docker version
docker compose version
```

Reserve persistent storage for the databases and downloaded metadata. The baseline uses
PostgreSQL for Core and SQLite for Ingest. Ports 3000, 5000, 5001, and 5432 must be available
on the Docker host. The guided platform helper is installed later on the Mac where you browse
GameHub; it does not belong on the server.

## 2. Create your installation folder

```bash
mkdir gamehub
cd gamehub
```

Open the [release Compose template](../../docker-compose.images.yml) on GitHub.
Choose **Download raw file** and save it as `compose.yaml` in this folder.
This complete file runs the frontend, Core, Ingest, and PostgreSQL using published images.

Set `GAMEHUB_VERSION=v0.2.0` for GameHub 0.2.0. For another published release, use its full tag
and matching handbook version. All three application images use the same version.
The images support Linux AMD64 and ARM64.

## 3. Supply the local configuration

Provide the following names through the shell environment or your deployment configuration.
Replace every placeholder with a separately generated value. Keep your values private and out of Git.

| Variable | What to supply |
| --- | --- |
| `GAMEHUB_VERSION` | `v0.2.0`, or the full tag of your chosen published release |
| `GAMEHUB_API_KEY` | Independent random operator key; at least 32 printable, non-space ASCII characters |
| `GAMEHUB_JWT_SIGNING_KEY` | Different random signing secret; at least 32 bytes |
| `POSTGRES_PASSWORD` | Strong unique database password |
| `GAMEHUB_INGEST_SERVICE_KEY` | Different random internal service key, needed for platform connections |
| `GAMEHUB_CREDENTIAL_KEY_1` | Base64-encoded 32-byte encryption key, needed to store platform connections |

Generate each ordinary key separately with `openssl rand -hex 32`. Generate the vault key
with `openssl rand -base64 32`. Do not reuse keys. Keep a protected copy of the vault key for recovery.

For a local installation, use these non-secret settings:

```text
GAMEHUB_FRONTEND_AUTH_ENABLED=true
GAMEHUB_PUBLIC_ORIGIN=http://localhost:3000
GAMEHUB_AUTH_COOKIE_SECURE=false
GAMEHUB_MULTI_USER_ENABLED=false
GAMEHUB_OIDC_ENABLED=false
GAMEHUB_CREDENTIAL_KEY_VERSION=1
```

For covers and descriptions, also supply `IGDB_CLIENT_ID` and `IGDB_CLIENT_SECRET` from your
Twitch application. Those are optional for startup and digital imports, but required for IGDB
enrichment and physical-game search. See the [configuration reference](configuration.md)
for defaults and optional settings.

## 4. Start the application

Run from your installation folder, with your configuration available to Compose:

```bash
docker compose config --quiet
docker compose pull
docker compose up -d --no-build
docker compose ps
```

The first command validates configuration; the second downloads the images. Wait for health
checks to pass after startup, then open [GameHub](http://localhost:3000) on the Docker host.

If a package is private and pulling reports `unauthorized` or `denied`, authenticate with a GitHub
account that has access to all three images using `docker login ghcr.io --username YOUR_GITHUB_USERNAME`.
Enter a token permitted to read those packages at the password prompt, then retry the pull.
Do not put the token into the command or a support report.

| Check | Expected result |
| --- | --- |
| `docker compose ps` | Services running and health checks passing |
| `http://localhost:5001/health` | Successful Core health response |
| `http://localhost:5000/health` | Successful Ingest health response |
| `http://localhost:3000/login` | Login page |

The ports bind to the Docker host's loopback interface. A different computer cannot reach that
`localhost`. For a remote server, use the [HTTPS deployment guide](deployment.md).
If startup fails, use [installation troubleshooting](troubleshooting.md#installation-does-not-start).

## 5. Create your first account

The local installation has no public registration page. Create the initial owner once:

1. Open Core Swagger at `http://localhost:5001/swagger`.
2. Select **Authorize**, enter your `GAMEHUB_API_KEY` in the API-key authorization field, and authorize.
3. Open **POST /api/auth/bootstrap**, select **Try it out**, and enter:
   ```json
   {
     "email": "owner@example.com",
     "password": "replace-with-a-unique-long-password",
     "displayName": "Owner"
   }
   ```
4. Replace the example email, name, and password. The password must contain 12–128 characters.
5. Select **Execute**. A successful response creates the owner; bootstrap is accepted only once.
6. Open `http://localhost:3000/login` and sign in with that email and password.

If an account already exists, sign in with it instead of rerunning bootstrap or deleting data.
The operator key is not your login password.

## 6. Import your games

Continue with [Your first library](first-library.md). The goal is to see one known game in your
collection before adding more platforms.

Keep persistent volumes when restarting. `docker compose down` stops the installation;
`docker compose down -v` also deletes its volumes and is not a repair step.
