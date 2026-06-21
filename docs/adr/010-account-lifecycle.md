# ADR-010: Initial Account Lifecycle

**Date:** 2026-06-21
**Status:** Accepted
**Deciders:** @jankahnt

## Context

Roadmap item #8 has a verified tenant-isolation foundation, but isolation alone does not define
how an owner account is created, secured, recovered, or removed. Account deletion also crosses
the Core and Ingest databases without a distributed transaction. Reporting success after only
one database was cleaned would retain user-owned data or encrypted platform credentials.

GameHub still has no trusted transactional email provider. A password-reset link or email-based
ownership proof would therefore create a recovery promise the deployment cannot safely keep.
This decision covers the initial local-account lifecycle only; it does not enable production
multi-user operation.

## Decision

### Provisioning and registration

- GameHub has no public self-registration endpoint.
- `POST /api/auth/bootstrap` creates the default tenant's first local owner only after the
  configured server API key authenticates the request.
- `POST /api/auth/tenants` lets the same API-key trust boundary provision an additional tenant
  and its single owner. The server generates the tenant ID and starts platform sync disabled.
- These are operator-controlled provisioning paths, not a production multi-user signup flow.

### Password changes and session revocation

- `PUT /api/auth/password` accepts no account or tenant ID. It uses the signed JWT subject and
  tenant claims, verifies the current password, applies the normal 12–128 character rule to the
  new password, and rejects reuse of the current password.
- The password hash change and revocation of every refresh session commit in one Core database
  transaction.
- `POST /api/auth/revoke-all` explicitly revokes every refresh session belonging to the signed
  account/tenant pair.
- Refresh tokens stop working immediately. Access JWTs remain valid until their configured short
  expiry because they are stateless; clients must discard both tokens. The Next.js BFF clears its
  HttpOnly cookies after successful password change, global revocation, or account deletion.

### Recovery boundary

There is no forgot-password, email-reset, recovery-code, or API-key-based password-replacement
endpoint in this lifecycle. Without a trusted email/identity provider, GameHub cannot safely prove
that an anonymous requester owns an existing account. If the password and all live sessions are
lost, recovery is an operator responsibility outside the public API, such as restoring a trusted
database backup or deliberately reprovisioning the deployment. Production authentication must
define a stronger recovery mechanism before guarded multi-user enablement.

### Account and tenant deletion

`DELETE /api/auth/account` requires a signed account JWT and the current password. Request bodies
cannot select an account or tenant. Deletion has three ordered phases:

1. Core atomically disables platform sync, records `DeletionRequestedAtUtc`, and revokes all
   refresh sessions. This is the durable, fail-closed retry state.
2. Core delegates the trusted tenant to Ingest. Ingest transactionally and idempotently deletes
   Steam, GOG, PSN, and Epic raw rows, Ingest audit history, and encrypted platform credentials
   including ciphertext, nonce, and authentication tag.
3. Only after Ingest succeeds, Core transactionally deletes tag assignments, library items,
   manual links, enrichment/sync/activity history, sync cursors, tags, unified games, refresh
   sessions, the account, and the tenant.

The seeded default compatibility tenant is a deliberate physical exception: its account and all
user-owned data are deleted, but the empty tenant row is retained with sync disabled so an upgraded
single-user installation can bootstrap a new owner. Provisioned tenant rows are removed entirely.

### Partial failures and retries

- If Ingest is unavailable or rejects cleanup, Core returns `502 account_deletion_incomplete`.
  Core data and the account remain, but the tenant stays pending deletion, sync-disabled, and with
  its existing refresh sessions revoked. The response never claims completion.
- If Ingest succeeds but Core cleanup fails, Core returns `503 account_deletion_incomplete`.
  The local Core transaction rolls back and the same request can safely repeat the already-empty
  Ingest cleanup before retrying Core.
- Normal bearer requests for a pending or deleted account fail validation. A cryptographically
  valid, unexpired JWT is accepted specifically on the deletion endpoint so the operation can be
  retried. A pending account can authenticate with its password to obtain another token, but that
  token remains restricted to completing deletion.
- Repeating a completed deletion is a safe no-op. A mismatched live account ID or tenant ID is
  rejected before Ingest is called, preventing Tenant A from selecting Tenant B.

## Consequences

### Positive

- Password and destructive operations follow signed identity rather than caller-selected IDs.
- Refresh-session invalidation is immediate, durable, and tenant-isolated.
- Cross-service deletion is observable, fail-closed, and retryable without a distributed
  transaction.
- Encrypted credential material is included in the same tenant cleanup boundary as raw data.
- The additive nullable migration preserves existing SQLite and PostgreSQL data and leaves every
  upgraded tenant active (`DeletionRequestedAtUtc = null`).

### Limitations

- Access JWTs are not individually revocable and retain validity until expiry after password
  change or global refresh-session revocation.
- There is no supported self-service account recovery yet.
- Provisioning still relies on the deployment API key and one owner per tenant.
- OAuth/external identity, production abuse controls, verified email ownership, account recovery,
  and an explicit guarded multi-user rollout remain future Roadmap #8 work.

## Multi-user status

This ADR completes the initial account-lifecycle gate only. It does not activate multi-user mode,
make local JWT authentication production-ready, or authorize advertising GameHub as a multi-user
service.
