# GameHub

![CI](https://github.com/happticde/gamehub/actions/workflows/ci.yml/badge.svg)

GameHub brings your Steam, PlayStation, GOG, and Epic games into one collection.
You can also add physical games for PS3, PS4, and PS5.

**Read the [GameHub handbook on GitHub Pages](https://happticde.github.io/gamehub/).**
It covers setup, daily use, and server maintenance. Use the version menu to match your
installed release. **Next (unreleased)** covers development changes on `main`.
You can also read the [handbook source](docs/index.md) in this repository.

## Features

- See which platforms you own each game on.
- Add covers and game details from the IGDB game database.
- Track play status, personal ratings, and custom tags.
- Search your collection and view statistics.
- Fix metadata matches and merge or split game entries.
- Export your library and restore it from a JSON backup.

The guided connection helper is available for macOS Apple Silicon and Intel.
Other systems can use the manual connection steps. Epic support is an early integration.
See [Platform connections](docs/guides/platforms.md) for details.

## Get started

You need Docker with Docker Compose. GameHub provides ready-made images.
You do not need Git or local build tools.

1. Save the [release Compose template](docker-compose.images.yml) as `compose.yaml` in a new folder.
2. Choose a release and supply the required settings. Follow [Installation](docs/guides/installation.md).
3. Run these commands from that folder:

```bash
docker compose pull
docker compose up -d --no-build
```

Then create the first local account as described in the installation guide.
Open the frontend and follow [Your first library](docs/guides/first-library.md).

| Service | Default local address |
| --- | --- |
| Frontend | `http://localhost:3000` |
| Core API and Swagger | `http://localhost:5001/swagger` |
| Ingest API and Swagger | `http://localhost:5000/` |

Both APIs expose `/health`. Ports bind to `127.0.0.1` by default.
Core uses PostgreSQL by default. Ingest uses SQLite.

Set `GAMEHUB_VERSION` to a published tag, such as `v0.1.0`.
Compose also requires an API key, an internal service key, a signing key,
a credential encryption key, and a PostgreSQL password.
The installation guide lists their variable names. Use strong, separate secrets.
Frontend login is enabled by default.
IGDB metadata needs Twitch application credentials. The configuration guide lists these settings.

Set `GAMEHUB_PUBLIC_ORIGIN` to the exact browser origin when using a hostname or HTTPS proxy.
The default is `http://localhost:3000`.
For an existing installation, read the [security upgrade guide](docs/security-hardening-2026-10-15.md)
before updating GameHub and the installed connection helper.

For source builds, use the [development guide](docs/guides/development.md).

## Documentation

| Task | Guide |
| --- | --- |
| Connect accounts and import games | [Platform connections](docs/guides/platforms.md) |
| Search, fix matches, track progress, or add physical games | [Manage your collection](docs/guides/library.md) |
| Export, validate, and restore a library | [Backup and restore](docs/guides/backup-restore.md) |
| Run your own server, upgrade, or recover | [Deployment](docs/guides/deployment.md), [configuration](docs/guides/configuration.md), [troubleshooting](docs/DEBUGGING.md) |
| Set up login for a public server | [Authentication](docs/authentication.md), [guarded activation](docs/guarded-multi-user-enablement.md), [Keycloak](docs/keycloak-oidc-setup.md) |
| Connect other tools | [API overview](docs/guides/api.md), [CLI](docs/guides/cli.md), [MCP](docs/mcp.md) |
| Understand or contribute to the code | [Architecture](docs/guides/architecture.md), [development](docs/guides/development.md), [roadmap](docs/ROADMAP.md) |
| Report a security issue or keep dependencies current | [Security policy](SECURITY.md), [Dependabot setup](docs/dependabot.md) |
| Publish a product release | [CI and release runbook](docs/ci-and-releases.md) |
| Preview or manually publish the handbook | [Documentation runbook](docs/documentation.md) |

## Components

| Component | Responsibility |
| --- | --- |
| `services/core/` | ASP.NET Core API for the library, accounts, game matching, and metadata |
| `services/ingest/` | ASP.NET Core API for platform imports and encrypted credentials |
| `frontend/` | Next.js web interface and server-side API proxy (BFF) |
| `services/cli/` | Go command-line client and local connection helper |
| `services/mcp/` | .NET MCP server that connects AI tools to Core |
| `services/shared/` | Shared service contracts and security checks |
| `docs/site/` | Docusaurus website with versioned handbooks |

## Local use and public servers

Start with the local setup. Before exposing GameHub to the internet, follow the deployment
and authentication guides. Use HTTPS and keep the backend APIs and databases private.

Production access for multiple users is off by default. An operator must check the setup
and enable it. Login can use an external provider through OpenID Connect (OIDC).
Configuring that provider alone does not enable this mode.
Automatic creation of a personal library on first OIDC login is a separate option.
Shared libraries, invitations, and public registration are not included.

Keep secrets and local environment files out of Git. GameHub stores browser login tokens
in HttpOnly cookies. It encrypts stored platform credentials in Ingest.
The signed-in account determines library ownership. Client headers and backup files cannot
select another account's library.

## Releases

Find published versions and downloads on [GitHub Releases](https://github.com/happticde/gamehub/releases).
Core, Ingest, frontend, and CLI share one release version.

Maintainers publish through **Actions → Release → Run workflow** on **main**.
Each release includes checked container images in GitHub Container Registry (GHCR).
It also includes CLI and helper archives, checksums, and software component lists (SBOMs).
Merging a PR does not publish a release.

Handbook publication uses the separate **Publish documentation (manual)** workflow.
A merge or product release does not update the live handbook.
Documentation-only changes do not create a product release.

## License

GameHub application code and handbook content use **AGPL-3.0-only**.
The website tooling in `docs/site` uses **MIT**. Third-party components keep their own licenses.
See [LICENSE](LICENSE), [COPYRIGHT](COPYRIGHT), [licensing and source availability](licensing/README.md),
and [contribution terms](CONTRIBUTING.md).
