# GameHub handbook

Install GameHub, connect your game accounts, and manage your digital and physical collection.
GameHub brings Steam, PlayStation, GOG, and Epic libraries together with covers and descriptions
from IGDB, personal ratings, play status, tags, and backups.

## Start here

- **Installing for the first time?** Follow [Install GameHub](guides/installation.md) through your
  first login, then [import your first library](guides/first-library.md).
- **Already signed in?** [Connect a platform](guides/platforms.md), [manage your collection](guides/library.md),
  or [add a physical game](guides/physical-games.md).
- **Running your own server?** Set up [HTTPS and deployment](guides/deployment.md),
  [authentication](guides/authentication.md), [updates](guides/upgrades.md), and
  [installation backups](guides/disaster-recovery.md).
- **Something is wrong?** Start with [Troubleshooting](guides/troubleshooting.md).

## Choose the matching version

Use the version menu to select your installed GameHub release. The CLI also reports its version
with `gamehub-cli --version`. **Next (unreleased)** may describe features that are not in your
installation. Documentation can receive corrections independently of the application release.

## What you need

For a new installation, you need Docker with Compose and configuration values you supply yourself.
You do not need to install .NET, Go, or Node to run the application with Compose. Platform accounts
are connected after setup. The guided connection helper for GOG, PlayStation, and Epic currently
supports macOS with Google Chrome; Steam does not need that helper.

If someone else hosts GameHub for you, ask them for the site address and sign-in method, then go
straight to [Your first library](guides/first-library.md).
