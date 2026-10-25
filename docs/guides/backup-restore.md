# Back up and restore your library

## Choose a format

| Format | Includes | Use |
| --- | --- | --- |
| JSON | Games, metadata, tracking, tags, platform items, manual links, and physical copies | Move or restore your collection |
| CSV | Spreadsheet-friendly library view | Analyze or report on the collection |
| Installation backup | Databases, configuration, and separately protected keys | [Recover the complete server](disaster-recovery.md) |

JSON does not include provider credentials, raw platform downloads, activity/audit history,
or sync metadata. It does not replace an installation backup.

## Download a backup

1. Open **Admin → Backup**.
2. Under **Library exports**, select **Download JSON**.
3. Save the downloaded file outside the GameHub server and its volumes.
4. Keep a dated copy before upgrades or major manual cleanup.

Use **Download CSV** when you need a spreadsheet export. CSV cannot be restored as a library.
Library exports contain private information about ownership and tracking; restrict access.

## Validate and restore

Restore requires an **empty destination library**. It does not merge a backup into an existing
collection. Do not delete your current collection just to test an export; use a separate empty
installation or an appropriately prepared account.

1. Sign in to the intended destination and open **Admin → Backup**.
2. Select **Choose JSON backup** and choose your JSON file.
3. Wait for validation. Check **Backup valid**, the format version, and the record counts.
4. Select **Restore backup**.
5. Review **Restore this backup?** and confirm only if the destination and counts are correct.
6. Wait for completion, then check known games, personal ratings, tags, and physical copies.

Validation runs before writes. A failed restore does not justify changing the file's format
number to bypass validation. The current `gamehub-backup` format is version 3; supported version
1 and 2 backups are also accepted according to their compatibility rules.
Version 3 can include normalized GTIN data, even though barcode entry is not an active UI flow.

After moving to another installation, reconnect platforms separately.
See [Restore troubleshooting](troubleshooting.md#backup-validation-or-restore-fails).
