# MCP connection data contracts

This implements the persistence package [#68](https://github.com/happticde/gamehub/issues/68)
for [Model Context Protocol (MCP)](https://modelcontextprotocol.io/), following accepted
[Architecture Decision Record (ADR) 016](adr/016-mcp-user-authorization.md).
The merged [OpenIddict proof](../tools/openiddict-proof/README.md) was repeated successfully
before schema adoption. This package adds storage only; it does not enable user-token issuance,
consent, device login, remote transport, or a new authentication scheme.

## Ownership and consent

`McpGrant` records one approved client identifier, exact resource, canonical application scopes,
original 64-bit account session epoch, creation/absolute-expiry times, and terminal revocation.
The scopes are `library:read` or `library:read tracking:write`. Protocol `offline_access` belongs
to OpenIddict's authorization principal, not this application-permission column.
`McpConnectionDto` is the later management API's secret-free response shape; it contains no
owner selectors, provider identifiers, tokens, hashes, or protocol payloads. There is no new route.

Each grant references one independent `AccountSession` and exactly one
`McpProtocolAuthorization`. Unique constraints prohibit sharing either between grants or issuing
multiple protocol authorizations under one family. Composite foreign keys bind authorization,
family, grant and verified provider provenance to the same account and tenant. Multiple grants
may reference the same `OidcProviderSession`; its existing one-to-one browser-family mapping stays
intact. Migration guards prohibit sharing a connection family with browser provider mappings or
`RefreshTokenSession` rows, in both insertion directions. Consent fields, original epoch and
absolute expiry are immutable; only the first revocation marker can be set.

The provider's owner columns remain nullable because unowned logout tombstones must survive.
Entity Framework Core, the database mapper, cannot represent nullable alternate-key columns.
The model therefore describes the navigation through the provider ID and a composite unique
index; both reviewed migrations strengthen that reference to the actual three-column foreign
key. Preserve that manual migration constraint during future scaffolding or SQLite table rebuilds.
Use migrations for production and constraint tests: `EnsureCreated` does not install these guards
or the strengthened provider foreign key. Identity tables deliberately have no tenant query
filter, because authentication starts before a tenant is trusted. Every lookup/mutation must
carry explicit authenticated account and tenant predicates.

## Revocation and deletion

`AccountSessionRevocation` closes owned grants, protocol authorizations and protocol tokens in
the caller's transaction. Local browser-family logout leaves independent connections valid.
Individual, other-family and account-wide invalidation close the affected connection families.
Account-wide callers still advance the account epoch. Verified provider logout closes every
grant referencing that provider row, even when its browser family was already revoked locally.
Other owners and provider rows remain independent.

Pending account deletion uses the existing epoch/family invalidation before cross-service cleanup.
Final cleanup deletes owned protocol tokens, grants and authorizations before clearing provider
ownership into its existing hash-only tombstone and deleting local families/accounts. Shared
client registrations and unapproved, ownerless pending codes are not account-owned data.
Later device/consent implementation must reject approval/redemption for a deleted or pending owner.

These are data mutation contracts, not a completed live-authorization or concurrency guarantee.
[#69](https://github.com/happticde/gamehub/issues/69) must validate account/tenant deletion state,
original epoch, live grant/family, absolute expiry, exact resource/scopes and provider
`ReissueBlockedAtUtc` on every use. [#71](https://github.com/happticde/gamehub/issues/71) must
serialize approval/redemption/refresh against those same markers on both database providers.
The migration triggers enforce durable invariants; they do not replace transaction race tests.
Protocol manager entity caching must be disabled or coherently invalidated before any server is
enabled: these central mutations use database updates and cannot leave a cached authorization valid.

## Protocol secrets and persistent keys

Only [OpenIddict storage](https://documentation.openiddict.com/configuration/token-storage)
owns protocol codes, reference identifiers and rotating integration refresh credentials. Never
write an integration credential to browser `RefreshTokenSession`, consent columns or logs. Let
the supported manager hash reference identifiers/client secrets where verification is sufficient.
An encrypted payload is still sensitive; arbitrary claims and `Properties` are not a secret store.

Before any protocol endpoint is enabled, use the supported
[ASP.NET Core Data Protection token format](https://documentation.openiddict.com/configuration/token-formats)
for database-resident reference/code payloads. Persist a dedicated application-isolated key ring
outside the database, source tree and container image; protect those keys at rest and share the
same protected ring across Core replicas. Keep it independent from browser cookie protection,
the existing browser-token signing key, provider trust, delegation and credential-vault keys.
Use separate persistent signing/encryption certificates as required by OpenIddict's
[key guidance](https://documentation.openiddict.com/configuration/encryption-and-signing-credentials).
Retain retired decryption keys through all outstanding token/grant lifetimes. Missing keys must
fail closed, never fall back to plaintext, ephemeral production keys or the operator API key.

This schema package registers no protocol server, key ring, development certificate or default
client and stores no protocol credentials. Key provisioning, startup validation, encrypted-format
tests across restart and actual rotation/reuse tests are mandatory gates for
[#70](https://github.com/happticde/gamehub/issues/70) and #71. Operators configure secrets outside
repository environment files; this change introduces no required runtime setting.

## Dependencies and verification

On 2026-11-14, [OpenIddict 7.8.0](https://github.com/openiddict/openiddict-core/releases/tag/7.8.0),
released 2026-11-03, remains the latest stable version. Only its database integration is added to
Core; protocol server and validation packages remain downstream. Its Apache-2.0 license and
transitive package review follow the proof's resolved graph. Product builds generate notices from
the resolved production inputs; repeat advisory/license checks before rollout.

`McpGrantContractTests` exercise fresh/upgrade preservation, foreign owner/reference rejection,
immutable consent/lifetime, family separation, individual/provider/account revocation and
deletion ordering. SQLite always runs. To run real PostgreSQL cases, supply
`GAMEHUB_TEST_MCP_POSTGRES_CONNECTION` to the test process for a disposable test server with database
creation rights. Each case creates and removes a unique database; do not point this at production.
Tests store only synthetic metadata, no bearer credentials. Runtime claims/route allowlists and
real-client interoperability remain the later acceptance gates in ADR-016.

The SQLite model was scaffolded with the matching Entity Framework tool in a disposable stable
Docker container, using a temporary metadata-only factory without application authentication
configuration. The PostgreSQL migration follows the same operations with provider-specific types
and guards. No design-time factory or runtime migration-command behavior is changed in production.
Review the repository's shared snapshot and conditional provider migrations carefully; retain only
operations for the new package and preserve the composite provider foreign key and guards on both
engines.
