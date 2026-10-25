# Update GameHub

Update the frontend, Core, and Ingest to the same release. Check that release's notes and select
its handbook version before changing the installation.

## Before updating

1. Record the current release and database providers.
2. Create a [library export](backup-restore.md) and a consistent [installation backup](disaster-recovery.md).
3. Preserve the vault key versions required by stored credentials.
4. Arrange a maintenance window and stop writes and background work.

## Source-build installation

From the repository root, with your deployment configuration available:

```bash
docker compose stop frontend core ingest
git fetch --tags origin
git checkout vX.Y.Z
docker compose up -d --build
docker compose ps
```

Replace `vX.Y.Z` with the published target release. Resolve local source changes before checking
out another tag; do not overwrite operator changes.

## Release-image installation

Set `GAMEHUB_VERSION` to the target published tag in your deployment configuration.
Run from the folder containing the release `compose.yaml`:

```bash
docker compose stop frontend core ingest
docker compose pull
docker compose up -d --no-build
docker compose ps
```

Keep existing secrets and volumes. See [Release-image setup](deployment.md#use-published-release-images).

## Verify the update

- Check service health and startup/migration logs.
- Sign in and inspect a known game, a personal rating, tags, and physical copies.
- Check connection status and run one controlled **Refresh Platforms + Sync**.
- Update the locally installed helper when the release notes require it. Download the package
  from the updated GameHub **Accounts** assistant.

## If the update fails

Stop writes and retain logs without secrets. Do not delete volumes.
Reverting application images is safe only when the database schema remains compatible.
If migrations changed it, follow [installation recovery](disaster-recovery.md) with a matching
backup and application release.
