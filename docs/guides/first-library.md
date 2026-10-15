# Your first library

This walkthrough starts after [installation](installation.md). Sign in and open `/admin`.
The account controls and platform assistant depend on your configured deployment mode.

## Connect one platform

Start with Steam if you have a Steam Web API key and Steam ID, or use the account connection
assistant for GOG, PlayStation, or Epic. Open the platform/account controls, follow the platform
instructions, and save the credentials to GameHub. See [Connect platforms](platforms.md) for
provider-specific details and helper requirements.

Saving credentials confirms storage. It does not prove the provider has returned a complete
library. A newly provisioned tenant starts with platform sync disabled; Core enables it only
after fresh status verification finds supported credentials.

## Retrieve, then synchronize

GameHub performs two distinct operations:

1. **Retrieval** contacts the platform and stores its raw library in Ingest.
2. **Synchronization** merges stored platform records into the unified Core library.

Use the Admin sync controls to refresh stale provider data and synchronize it. Cached sync can
be repeated without contacting providers. Force refresh is for deliberate recovery and still
respects provider cooldowns. Check the provider status and retrieval history before repeatedly
retrying an empty or failed import.

## Enrich and inspect

If IGDB is configured, run enrichment from Admin. Enrichment adds covers, descriptions, genres,
and other metadata; it does not establish ownership of a game.

Open `/library`, search for a known title, and open its details. Check its platform ownership,
metadata, and any duplicate editions. Use the match correction or merge/split tools only after
checking the records. [Manage your collection](library.md) explains the differences.

## Add the remaining platforms

Connect the next platform and repeat the retrieval/sync checks. Cross-platform matching uses
normalized title slugs, so games with different editions or names may require manual review.
Manual links survive later syncs.

## Finish with a backup

Export a JSON backup from Admin and store it securely outside the deployment. A library backup
does not contain provider credentials or the complete Ingest database. Read
[Backup and restore](backup-restore.md) before treating it as disaster recovery.
