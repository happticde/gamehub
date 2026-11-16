# Managed MCP server keys

The [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) key manager implements
[#113](https://github.com/happticde/gamehub/issues/113) of the
[minimal setup contract](mcp-minimal-setup-contract.md). Core can provision, adopt, check and renew
its durable protocol keys without OpenSSL. This interface does not deploy GameHub, alter Caddy,
enable multi-user mode or expose remote MCP. The single Compose setup and server-side Frontend
configuration loaders belong to [#114](https://github.com/happticde/gamehub/issues/114).

## Storage and privilege

Use two dedicated Linux volumes, in the existing Compose project:

| Default container directory | Contents | Runtime owner |
| --- | --- | --- |
| `/var/lib/gamehub-mcp` | `state.json`, `certificates/`, `ring/` | Existing Core user `1654:1654` |
| `/var/lib/gamehub-mcp-frontend` | Frontend-only `state.json` with paired internal credentials | Existing Frontend user `1001:1001`; runtime mount read-only |

The setup command runs as root only to initialize volume ownership. It creates directories with
mode `0700` and private files with `0600`, then assigns each volume to its service. Runtime Core
and maintenance use Core's non-root identity. Frontend never mounts Core's signing, encryption,
protection certificates or data-protection ring. Existing nonempty stores with unsafe permissions
are rejected; only a fresh empty Docker volume can have its default permissions tightened by setup.
The lower-level store API does not change ownership; image setup always uses the command below.

`state.json` is private configuration, not status output. It contains independent transport and
consent credentials plus version, generation, public origin, certificate hashes and a ring proof.
The Frontend copy contains no certificate material. Both copies must match before deployment.
Never paste them into logs, issues or a merged Compose listing. Configuration loaders and the
cross-service readiness check in #114 must validate the pairing. The private `check` command below
checks Core's store; it does not prove a running Frontend or public proxy is configured correctly.

## Offline command interface

The matching Core image/executable accepts `mcp-keys` before application startup or configuration
loading. In a container with the two dedicated volumes mounted, its application command is:

```bash
dotnet GameHub.Core.dll mcp-keys setup --enable --public-origin https://games.example.com
```

This is the low-level command that #114's shipped Compose addition will wrap. It is available only
in builds/releases containing #113. Do not assume an older published image accepts it. The
command prepares state without starting an application, changing external-provider settings or
contacting a provider. Keep the existing public origin and explicit enablement as the only new
non-secret inputs. Advanced/disposable test layouts may use `--core-directory` and
`--frontend-directory` with dedicated absolute paths.

Repeating the same command reuses the same identities. A different origin, missing material,
generation mismatch, unsafe path, concurrent operation or partial store produces a stable failure
code and does not initialize over it. Secret values never appear in arguments or ordinary output.

Under Core's normal runtime user, run:

```bash
dotnet GameHub.Core.dll mcp-keys check
dotnet GameHub.Core.dll mcp-keys renew
```

`check` emits only `Code` and `DaysRemaining`. Exit `0` means ready; `2` means renewal is due;
`1` means an invalid command or unsafe/unreadable store. Failures emit only a stable `Code`, not
paths, exception details, identities or secrets. `renew` is idempotent outside the renewal window.
For a deliberate maintenance exercise, `renew --force` creates another retained generation even
when current certificates are healthy. Do not use force to recover missing or expired material.

## Renewal and retained keys

New installations use three independent 3072-bit RSA certificates with two-year validity, separate
from HTTPS and all browser/operator/Ingest keys. Managed Core checks on startup and hourly, and
renews within sixty days of the earliest active certificate's expiry. It reloads its protocol
options and data-protection provider without dropping old decryptors. A CLI renewal is picked up
by the next maintenance check or a normal restart. Failed maintenance disables the reloadable
cryptographic provider until a later check validates the complete store again; it never selects
API-key compatibility or ephemeral replacement keys.

Every generation remains on disk and registered for historical decryption. This includes expired
protection certificates needed to decrypt older data-protection keys. Automatic deletion and
rewrapping of old rings are not implemented. Storage grows by three small certificate files per
renewal, bounded to one hundred generations; reaching the bound fails with `retention_limit` and
requires a separately reviewed retention/migration action. No uninterrupted rotation is claimed
after complete key loss, expiry during prolonged downtime, or incompatible backup restoration.

The ring proof prevents an empty or unrelated ring from becoming a healthy new installation.
Every stored descriptor must decrypt before a loaded provider may generate ordinary new
data-protection keys. Certificates are integrity-checked and purpose-separated. This does not
make an inconsistent database/key backup consistent or prove every historical payload was backed
up. Verify a restored deployment before re-enabling clients.

## Adopting a manual installation

Stop Core and Frontend, preserve rollback artifacts, and take a consistent database/key backup.
Mount the old certificates and ring read-only, using dedicated private files. Ensure their
directories/files already have restrictive permissions; adoption does not change the source.
Supply a private JSON descriptor with these fields:

```json
{
  "PublicOrigin": "https://games.example.com",
  "SigningCertificate": "/run/legacy/signing.pfx",
  "EncryptionCertificate": "/run/legacy/encryption.pfx",
  "ProtectionCertificate": "/run/legacy/protection.pfx",
  "RingDirectory": "/run/legacy/ring",
  "ConsentKey": "REPLACE_WITH_EXISTING_PRIVATE_CONSENT_KEY",
  "CertificatePassword": "",
  "RetiredProtectionCertificates": []
}
```

The descriptor itself must be a private `0600` file; keep any passwords and consent credential in
that file rather than arguments. `CertificatePassword` is the fallback password for imported
certificates. Optional `SigningCertificatePassword`, `EncryptionCertificatePassword` and
`ProtectionCertificatePassword` override it for their purpose, including retained protection keys.
List every retained protection certificate needed by the ring. Environment files are never read
or modified. Import uses the existing `GameHub.Mcp.Protocol.v1` application discriminator,
validates all historical descriptors, preserves the existing consent key, and creates a new
independent transport key. The legacy ring must contain encrypted keys; plaintext rings,
incomplete decryptors, wrong origin and shared-purpose certificates are rejected.

```bash
dotnet GameHub.Core.dll mcp-keys setup --enable --public-origin https://games.example.com \
  --adopt-existing /run/legacy/import.json
```

Keep new volumes separate from the legacy source. Adoption is fresh-only; do not import again into
initialized managed storage. Migration-specific read-only mounts and the descriptor are the
contract's explicit one-time exception to the fresh-install input budget. Integration into a
running deployment still waits for #114's configuration/Caddy steps. Keep the old listener,
configuration and private files until rollback and restored grants have been verified.

## Failure and recovery

The exclusive `operation.lock` excludes concurrent processes and threads. Interrupted setup or
renewal may leave this lock or a `pending` marker. Neither is taken over automatically. After
confirming every writer is stopped, preserve the failed state and restore a consistent backup;
do not remove markers to make unknown partial state appear healthy. A never-activated failed
fresh installation may be discarded only after confirming its dedicated volumes contain no
existing grants or required keys. Existing initialized installations must not be reset as fresh.

Back up Core's database, both state volumes and all retained certificate/ring files consistently.
Use encrypted, access-restricted backups: certificate files are permission-protected and stored
without passwords; keeping an encryption password beside them would not add host protection.
Host and Docker administrators remain trusted. A database-only restore cannot recover encrypted
protocol payloads. If key material is unrecoverable, retain the failed installation, plan explicit
grant revocation and fresh authorization, and document the connection loss before recovery.
