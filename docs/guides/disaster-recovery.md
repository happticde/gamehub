# Back up and recover an installation

A [library JSON export](backup-restore.md) restores collection data. Full recovery also needs
the databases, deployment configuration, and keys used by the installation.

## What to preserve

| Item | Baseline location or purpose |
| --- | --- |
| Core PostgreSQL | Compose volume `postgres_data`; use PostgreSQL-aware backup and restore tools |
| Ingest SQLite | `/app/data/gamehub.db` in volume `gamehub_data` for the release template; inspect the configured path for other deployments |
| Core SQLite, when selected | `/app/data/core.db` in volume `gamehub_data` |
| Deployment configuration | Database provider, service addresses, public origin, and selected release |
| Secrets, stored separately | Vault key ring, JWT key, service keys, and OIDC cookie/client keys where used |

Losing vault encryption keys makes stored platform credentials unreadable.
Keep all key versions required by live data or older backups. Back up your identity provider
separately when using OIDC.

## Make a consistent backup

1. Record the running release and database providers.
2. Schedule downtime or otherwise prevent browser writes, imports, and background jobs.
3. Stop application services while leaving PostgreSQL available for its native backup tools:
   ```bash
   docker compose stop frontend core ingest
   ```
4. Back up Core with tools appropriate to its configured database. For SQLite, copy the stopped
   database and any associated journal/WAL files consistently. For PostgreSQL, create a logical
   dump or a supported consistent physical backup; do not copy a running data directory as a dump.
5. Back up Ingest with the same consistency requirement.
6. Preserve the matching deployment configuration and separately protected keys.
7. Start the application again:
   ```bash
   docker compose up -d
   ```
8. Verify health and login.

If you use custom Compose file options, include the same `-f` options in every command.
Keep backup copies outside Docker volumes and test recovery on a separate installation.

## Restore after an incident

1. Keep the affected application stopped to avoid new writes.
2. Select a database backup and the matching application release/configuration.
3. Restore both databases using the corresponding database tools.
4. Restore required keys through your secret/configuration system.
5. Start the services and check health and startup logs.
6. Verify login, known games, tags, physical copies, and connection status.
7. Run one controlled platform refresh and confirm the collection remains correct.

Changing only an image does not undo a schema migration. Recover with a compatible application
and database pair. Do not delete volumes or regenerate vault keys to repair a failed startup.
