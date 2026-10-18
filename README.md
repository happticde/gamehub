# GameHub

![CI](https://github.com/jfk1987/gamehub/actions/workflows/ci.yml/badge.svg)

GameHub brings Steam, PlayStation, GOG, Epic, and physical games into one collection. It combines
cross-platform ownership, IGDB metadata, personal tracking, custom tags, statistics, and portable
library backups.

Start with the [GameHub handbook](docs/index.md). The handbook source is available here before
GitHub Pages is activated. Pages publication is deliberately manual; the website is not
automatically published by a merge or product release.

## Get started

1. Follow [Installation](docs/guides/installation.md) and supply your deployment configuration.
2. Start the services with Docker Compose.
3. Complete [Your first library](docs/guides/first-library.md).

```bash
docker compose up -d --build
```

The baseline frontend is at `http://localhost:3000`, Core Swagger at
`http://localhost:5001/swagger`, and Ingest Swagger at `http://localhost:5000/`.
Both APIs expose `/health`. Use an actual release tag for a versioned deployment.
All baseline ports bind to `127.0.0.1`. Compose requires independent API/JWT keys and a PostgreSQL
password; frontend authentication defaults on. Set `GAMEHUB_PUBLIC_ORIGIN` to the browser-visible
origin when using a hostname or HTTPS proxy (the local default is `http://localhost:3000`).
Existing installations should review the [security hardening transition](docs/security-hardening-2026-10-15.md)
before updating the deployment and installed connection helpers.

## Documentation

| Task | Guide |
| --- | --- |
| Connect accounts and import games | [Platform connections](docs/guides/platforms.md) |
| Find games, correct matches, track progress, add physical copies | [Manage your collection](docs/guides/library.md) |
| Export, validate, and restore a library | [Backup and restore](docs/guides/backup-restore.md) |
| Self-host, upgrade, or recover | [Deployment](docs/guides/deployment.md), [configuration](docs/guides/configuration.md), [troubleshooting](docs/DEBUGGING.md) |
| Configure production authentication | [Authentication](docs/authentication.md), [guarded activation](docs/guarded-multi-user-enablement.md), [Keycloak](docs/keycloak-oidc-setup.md) |
| Integrate clients | [API overview](docs/guides/api.md), [CLI](docs/guides/cli.md), [MCP](docs/mcp.md) |
| Understand or contribute to the code | [Architecture](docs/guides/architecture.md), [development](docs/guides/development.md), [roadmap](docs/ROADMAP.md) |
| Publish a product release | [CI and release runbook](docs/ci-and-releases.md) |
| Preview or manually publish the handbook | [Documentation runbook](docs/documentation.md) |

## Components

| Component | Responsibility |
| --- | --- |
| `services/core/` | ASP.NET Core unified library, accounts, matching, enrichment, and orchestration |
| `services/ingest/` | ASP.NET Core raw platform storage, retrieval, and encrypted credentials |
| `frontend/` | Next.js interface and authenticated BFF |
| `services/cli/` | Go ingestion client and local connection helper |
| `services/mcp/` | .NET stdio MCP server backed by Core |
| `services/shared/` | Shared contracts and trust boundaries |
| `docs/site/` | Independent Docusaurus handbook and version tooling |

## Deployment boundaries

The local compatibility deployment and guarded production mode have different authentication
policies. Production multi-user admission defaults to disabled and must be explicitly enabled
after readiness checks. OIDC configuration alone does not activate it. Personal-tenant JIT is
a separate opt-in; public registration and shared tenants are not implied.

Supply strong independent secrets through your deployment system and keep operator-owned
environment files out of Git. Browser tokens stay in HttpOnly cookies; provider credentials
remain encrypted in Ingest. Tenant ownership comes from trusted authentication, never client
tenant headers or backup files.

## Releases

Core, Ingest, frontend, and CLI share one release version. Product publication is started through
**Actions → Release → Run workflow** on **main** and produces verified GHCR images, CLI/helper
archives, checksums, and SBOMs. Merging a PR does not publish a release.

Documentation-only changes run documentation verification and the existing secret/aggregate
checks without application or container builds. Documentation-only product release attempts
are rejected. Publishing the handbook uses the separate manual Pages workflow.
