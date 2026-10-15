# GameHub handbook

GameHub brings digital libraries from Steam, PlayStation, GOG, and Epic together with your physical
collection. It matches games across platforms, enriches them with IGDB metadata, and lets you track
what you own and play from one interface.

## Choose your starting point

- **New installation:** follow [Installation](guides/installation.md), then [Your first library](guides/first-library.md).
- **Everyday use:** learn about [your collection](guides/library.md), [platform connections](guides/platforms.md),
  and [backup and restore](guides/backup-restore.md).
- **Self-hosting:** start with [Deployment and upgrades](guides/deployment.md) and the
  [configuration reference](guides/configuration.md).
- **Contributing or integrating:** read the [architecture](guides/architecture.md),
  [development guide](guides/development.md), [API overview](guides/api.md), and [CLI reference](guides/cli.md).

## Match the handbook to your installation

Use the version menu to select the version shown by your installed release or `gamehub-cli --version`.
**Next (unreleased)** describes the current development branch and can include features absent from
your installation. A numbered handbook uses documentation from the exact product release commit.
The home page links to the newest documented stable release; before the first documented release,
it links to Next.

## Understand the deployment modes

GameHub supports a local compatibility deployment and a guarded production mode. The production
multi-user switch defaults to off. Configuring OIDC alone does not activate it. Follow
[Guarded multi-user operations](guarded-multi-user-enablement.md) before enabling production access.
Personal-tenant JIT is a separate opt-in feature; shared tenants, invitations, and public
registration are not implied by it.

## About this handbook

The source lives alongside the application. Existing operator runbooks and architecture decisions
are part of this handbook, so fixes have one source of truth. Documentation checks run automatically;
publishing to GitHub Pages currently requires a manual workflow run. See
[Maintaining and publishing documentation](documentation.md).
