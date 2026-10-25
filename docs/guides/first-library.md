# Your first library

Start after [installation and first login](installation.md), or sign in to the instance your
host provides. This walkthrough imports one platform and checks that its games appear.

## 1. Connect an account

1. Open **Admin** (`/admin`), then the **Accounts** tab.
2. Pick [Steam](steam.md), [GOG](gog.md), [PlayStation](playstation.md), or [Epic Games](epic.md).
3. Complete that platform's instructions and wait for confirmation that the connection is saved.

Steam uses an API key and Steam ID. GOG, PlayStation, and Epic offer a local browser helper.
Check the [supported setup](connection-helper.md) before installing it.

**Saved** means GameHub stored your connection. You still need a successful import.

## 2. Import the games

1. Open **Admin → Sync**.
2. Select **Refresh Platforms + Sync**.
3. Wait for the operation to finish, then inspect the provider status and sync history.
4. Open **Library** (`/library`) and search for a game you know you own.

**Refresh Platforms + Sync** asks connected platforms for fresh data when needed, then updates
your collection. **Sync Cached Library** rebuilds it from data already downloaded.
**Force Refresh** is for deliberate retries; provider cooldowns still apply.

If no games appear, check [missing games](troubleshooting.md#games-are-missing) before retrying.

## 3. Add covers and descriptions

If your host configured IGDB, open **Admin → Enrichment** and run the normal enrichment action.
Return to the game's details and check its cover, title, and description.
Import establishes ownership; enrichment supplies metadata. One can succeed while the other fails.

Use [Correct a match](correct-matches.md) for the wrong cover or description and
[Merge and split](merge-split.md) for duplicate or incorrectly grouped ownership.

## 4. Organize your collection

Open a game's details to set play status and a personal rating.
Use [tags and tracking](tracking.md) to organize your backlog, or [add physical games](physical-games.md).
Connect another platform and repeat the import check when the first works.

## 5. Save a backup

Open **Admin → Backup → Download JSON**. Keep the file outside your GameHub installation.
Read [Backup and restore](backup-restore.md) for what is included and how to restore it.
