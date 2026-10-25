# GameHub

GameHub brings Steam, PlayStation, GOG, Epic, and physical games into one collection.
Browse ownership across platforms, add IGDB covers and descriptions, track progress and ratings,
organize games with tags, and export portable library backups.

![CI](https://github.com/happticde/gamehub/actions/workflows/ci.yml/badge.svg)

## Install and start using GameHub

Start with the [GameHub handbook](https://happticde.github.io/gamehub/).
The [handbook source](docs/index.md) is also available in this repository.

1. Follow [Installation](docs/guides/installation.md) through configuration and your first login.
2. [Connect a platform](docs/guides/platforms.md).
3. [Import your first library](docs/guides/first-library.md).

Docker with Compose runs the published Linux AMD64/ARM64 images; Git, a source checkout, and
host installations of .NET, Go, and Node are not required.
Supply your own deployment secrets before starting.

The local baseline serves GameHub at `http://localhost:3000` and binds service ports to loopback.
For a domain or remote server, complete [HTTPS deployment](docs/guides/deployment.md).
Production account admission defaults to disabled and needs the explicit
[authentication setup](docs/guides/authentication.md).

## Guides

| Task | Instructions |
| --- | --- |
| Connect accounts | [Steam](docs/guides/steam.md), [GOG](docs/guides/gog.md), [PlayStation](docs/guides/playstation.md), [Epic](docs/guides/epic.md) |
| Install the local macOS connection helper | [Connection helper](docs/guides/connection-helper.md) |
| Find and organize games | [Collection](docs/guides/library.md), [tracking and tags](docs/guides/tracking.md) |
| Correct ownership or metadata | [Fix a match](docs/guides/correct-matches.md), [merge and split](docs/guides/merge-split.md) |
| Record physical copies | [Physical games](docs/guides/physical-games.md) |
| Back up, restore, or update | [Library backups](docs/guides/backup-restore.md), [server recovery](docs/guides/disaster-recovery.md), [updates](docs/guides/upgrades.md) |
| Resolve a problem | [Troubleshooting](docs/guides/troubleshooting.md) |
| Integrate another client | [CLI](docs/guides/cli.md), [API](docs/guides/api.md), [MCP](docs/guides/mcp.md) |

## Releases

Download matching images and CLI/helper packages from
[GitHub Releases](https://github.com/happticde/gamehub/releases). Core, Ingest, frontend, and CLI
share a release version. Use the corresponding handbook version when installing or updating.
Documentation corrections can be published independently of application releases.

## Contribute and report issues

See [Contributing](CONTRIBUTING.md) and the repository's [development guide](docs/guides/development.md).
Architecture decisions, audits, release workflows, and agent context stay in the repository;
the published handbook focuses on installation, use, and self-hosting.
Report vulnerabilities according to the [security policy](SECURITY.md).

## License

GameHub application code and handbook content are **AGPL-3.0-only**.
The independent website tooling in `docs/site` is **MIT**.
See [LICENSE](LICENSE), [COPYRIGHT](COPYRIGHT), and [licensing and source availability](licensing/README.md).
Third-party components retain their own terms.
