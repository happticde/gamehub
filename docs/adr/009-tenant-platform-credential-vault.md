# ADR-009: Tenant Platform Credential Vault

**Date:** 2026-06-20
**Status:** Accepted
**Deciders:** @jankahnt

## Context

Platform retrieval currently accepts Steam, GOG, and PSN credentials in request bodies. This is
usable for a local single-user deployment, but it cannot support scheduled per-tenant retrieval:
background work needs durable credentials, and plaintext secrets must not be stored in the
database, logs, backups, or API responses.

Ingest owns platform retrieval and raw platform data. Core authenticates users and delegates a
trusted tenant to Ingest with the separate internal service key established by the multi-tenancy
foundation.

## Decision

Ingest will own a tenant-scoped encrypted credential vault.

- One `PlatformCredential` row may exist per `(TenantId, Platform)`.
- Credential payloads are non-empty, versioned JSON objects with a 16 KiB UTF-8 limit.
- Payloads are encrypted with AES-256-GCM using a fresh random 96-bit nonce and a 128-bit
  authentication tag for every write.
- Tenant ID, platform, credential schema version, encryption format version, and key version are
  authenticated as associated data. Moving ciphertext between tenants or platforms therefore
  fails authentication.
- The database stores ciphertext, nonce, authentication tag, schema version, encryption format,
  and key version. It never stores the master key or plaintext payload.
- Keys are supplied as Base64-encoded 32-byte secrets under `Credentials:Keys:{version}`.
  `Credentials:ActiveKeyVersion` selects the key for new writes. Previous keys must remain
  configured until all rows using them have been re-encrypted or deleted.
- Empty key configuration leaves the service operational but makes credential writes fail closed
  with HTTP 503. Invalid or incomplete key-ring configuration fails service startup.

The internal API supports only secret-free status, encrypted upsert, and deletion. It requires the
`TenantDelegation` authentication scheme; normal API keys cannot call it, and tenant identity is
never accepted in the route or body. No endpoint returns decrypted credentials.

## Scope Boundary

This decision establishes encrypted storage only. Fetchers do not consume the vault yet, and
newly provisioned tenants remain `PlatformSyncEnabled = false`. A later change must add typed
platform validation, fetcher integration, controlled in-memory decryption, and the Core/frontend
management flow before multi-user platform sync can be enabled.

Credential rows and keys remain excluded from GameHub backups under ADR-008.

## Consequences

### Positive

- Database disclosure does not reveal platform secrets without the separately managed key ring.
- Authenticated encryption detects ciphertext, metadata, tenant, or platform tampering.
- Key versions allow staged rotation without a database schema change.
- Tenant filtering and a composite unique index preserve cross-tenant isolation.

### Negative

- Losing a key permanently makes credentials encrypted with it unrecoverable.
- Operators must provision and retain external key material separately from database backups.
- Generic JSON payloads require typed validation before individual fetchers may consume them.
- This foundation alone does not enable platform synchronization for provisioned tenants.

## References

- [.NET `AesGcm` API](https://learn.microsoft.com/dotnet/api/system.security.cryptography.aesgcm)
- [NIST SP 800-38D: GCM and GMAC](https://csrc.nist.gov/pubs/sp/800/38/d/final)
