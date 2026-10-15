# Backup and restore

## Choose the right backup

| Backup | Includes | Use it for |
| --- | --- | --- |
| GameHub JSON export | Durable library state for the authenticated tenant | Moving or restoring a library |
| CSV export | Spreadsheet-friendly library view | Reporting and analysis |
| Deployment backup | Core and Ingest databases, deployment configuration, independently secured keys | Full recovery and rollback |

The current JSON format is `gamehub-backup` version 3. It includes unified games, metadata,
personal tracking, tags, platform items, manual links, and physical copies with optional
normalized GTIN data. Restore also accepts supported version 1 and 2 documents using their
defined compatibility rules. See [ADR-008](../adr/008-data-portability-format.md).

JSON excludes API/provider credentials, tenant IDs, database IDs, raw Ingest data, activity and
audit history, and sync metadata. Keep deployment backups as well as library exports.

## Export a library

Use the backup controls in Admin to download JSON. Save it outside the container volumes and
restrict access: even without credentials, game ownership and personal tracking are private
data. CSV can be downloaded separately but is not accepted as a full restore format.

## Validate before restoring

Use Admin's validation preview before confirming restore. Validation checks the format,
supported version, relationships, and physical-copy data before writes. Do not edit format
numbers to bypass a rejection.

Restore requires an empty library for the authenticated tenant and runs transactionally.
It does not merge an import into an existing collection. Data in other tenants neither blocks
nor belongs in that restore. Ownership always comes from the authenticated account, not the file.

## Recover an installation

Before upgrades, take a consistent backup of both databases, the deployment's configuration,
and the separately protected vault/JWT/BFF keys. Preserve all encryption-key versions still
needed to read stored credentials. Losing the vault keys makes those credentials unreadable.

Stop writes and background work during a consistent recovery operation. Restore matching
database backups and application versions, then verify health, authentication, credentials,
retrieval, and tenant-scoped library access. Do not assume rolling back an image reverses a
database migration. See [Deployment and upgrades](deployment.md).
