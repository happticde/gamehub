# ADR-015: OIDC Back-Channel Logout and Session-Family Invalidation

**Date:** 2026-07-18
**Updated:** 2026-07-25
**Status:** Accepted
**Deciders:** @jankahnt

## Context

ADR-011 through ADR-013 authenticate one exact external `(issuer, subject)`, issue short-lived
GameHub access JWTs, and persist rotating opaque refresh tokens. The first Back-Channel Logout
implementation accepted only `sub` and invalidated every GameHub session for the account. That was
safe, but it did not implement the final OpenID Connect Back-Channel Logout 1.0 session semantics.

The final specification permits `sub`, `sid`, or both. A `sid` represents one provider session and
is unique only within its issuer. If `sid` is absent, `iss` plus `sub` means all RP sessions for
that subject. GameHub therefore needs a durable local session family that survives refresh-token
rotation and a provider-neutral mapping from a validated OIDC session to that local family.

## Decision

### Protocol and trust boundary

The public provider endpoint remains:

```text
https://<gamehub-host>/api/auth/oidc/backchannel-logout
```

The Next.js BFF accepts exactly one bounded form-encoded `logout_token`, returns no response body,
uses no browser cookies, and forwards the token through the independent BFF service-key boundary.
Core is the only component that validates provider proof or changes session state.

Core accepts only a signed compact Logout Token after checking:

- exact configured issuer and GameHub client audience;
- configured asymmetric signing algorithm and discovery/JWKS signing key;
- `iat` no older than five minutes and no more than one minute in the future;
- bounded `jti`, and bounded string values for any `sub` and `sid`;
- `sub`, `sid`, or both, as required by the final specification;
- the `http://schemas.openid.net/event/backchannel-logout` event with an object value;
- no `nonce`;
- valid `azp` when supplied or required by multiple audiences; and
- absent `typ` or exact `typ=logout+jwt`.

No `sub` or `sid` is used until all checks above complete. Invalid protocol messages return `400`;
discovery/JWKS or Core availability failures return retryable `503`; accepted unknown, already
revoked, deleted, and repeated targets return the same bodyless `200`.

Encrypted Logout Tokens remain unsupported. The provider must send a compact signed JWS under the
same asymmetric discovery/JWKS trust policy as ID tokens.

### Durable local session family

`AccountSession` is the stable, server-generated identity for one GameHub browser/device login.
Every normal GameHub access JWT contains its signed `gamehub:account_session_id`. Every rotating
`RefreshTokenSession` row references the same family. Refresh rotation consumes and replaces only
the opaque refresh row; it does not replace or revoke the family and therefore does not
accidentally invalidate existing access tokens.

Bearer validation checks the signed account, tenant, account epoch, and local session family
against live database state. Revoking one family immediately invalidates both its access and
refresh credentials. Account-wide password change, revoke-all, `sub`-only provider logout, and
account deletion still advance the account epoch and revoke every family.

### Provider-neutral `sid` mapping

After Core fully validates an OIDC ID token, normal OIDC session issuance requires its `sid`.
GameHub hashes the exact issuer and opaque `sid` with SHA-256 and stores only that issuer-bound
hash. The row maps the exact validated `ExternalIdentity` and its tenant/account pair to one
`AccountSession`. Raw provider session IDs, subjects, Logout Tokens, JWT IDs, account IDs, and
tenant IDs are not logged.

The `sid` is never an account or tenant selector by itself. It can reach a local family only
through this previously persisted, database-constrained mapping created during validated login.
Local non-OIDC sessions have no provider-session mapping and remain valid when a different mapped
OIDC family is logged out.

No provider SDK, administrator API, plugin, service account, provider-specific model, or long-lived
provider credential is introduced. Keycloak is only one operator setup example.

### Logout targeting

- With `sid` (with or without `sub`), GameHub revokes only the mapped `AccountSession` and all of
  its refresh rows.
- With `sub` and no `sid`, GameHub resolves only the exact `(issuer, subject)` mapping, advances
  that account's epoch, and revokes all local families and refresh rows.
- With both claims, a known session mapping must reference the same exact external identity,
  account, and tenant as `sub`. A disagreement returns `400` and changes neither side.
- Unknown or already revoked targets are accepted no-ops and do not reveal whether a mapping,
  account, tenant, or session exists.

### Replay, login/logout races, and deletion

Core stores the SHA-256 hash of exact validated issuer plus `jti` as the existing short-lived replay
marker. The provider-session table has a unique issuer-bound session hash. A valid logout for an
unknown `sid` creates a revoked hash-only tombstone.

That unique row orders parallel first login and logout writers:

- if login commits first, logout retries against the committed mapping and revokes it;
- if logout commits first, login observes the tombstone and cannot reopen the provider session;
- repeated login for the same still-active provider session reuses the same local family; and
- refresh rotation remains inside that family.

Account deletion removes tenant/account/external-identity references from provider-session rows but
retains their hash-only revoked tombstones. The separate deleted-subject tombstone continues to
block JIT recreation.

### Upgrade behavior

The SQLite and PostgreSQL migrations are additive. Existing refresh rows have no trustworthy
stable family reference, so the migration marks them revoked. Existing normal access JWTs have no
`gamehub:account_session_id`, so bearer validation rejects them after deployment. This deliberately
fails closed and requires a fresh login.

Restricted link/deletion-purpose JWTs retain their exact route boundary and do not become normal
library sessions. A new OIDC login succeeds only after the deployed provider supplies `sid`; the
operator must enable session-bearing Back-Channel Logout and then sign in again to create the
persisted mapping.

## Consequences

### Positive

- One provider session can be terminated without signing the same account out on other devices.
- `sub`-only logout retains the standard account-wide behavior.
- Access and refresh credentials share one immediate local revocation boundary.
- Refresh rotation does not invalidate access tokens in the same family.
- Issuer/subject tenant authority and cross-tenant isolation remain unchanged.
- Unknown, deleted, repeated, and already revoked targets stay non-enumerating.
- The implementation is provider-neutral and trusts only standard claims plus discovery/JWKS.

### Costs and limitations

- Deployments must configure a provider that supports session-bearing Back-Channel Logout and must
  create fresh GameHub sessions after upgrade.
- Browser cookies cannot be physically deleted through a server-to-server call; their credentials
  are unusable immediately and normal BFF failure handling clears them on the next request.
- Disabling/deleting a provider user is not a logout unless the provider sends the standard Logout
  Token. General provider lifecycle reconciliation remains separate work.
- This does not add SCIM, subject replacement, unlinking, invitations, or general SaaS readiness.

## References

- [Final OpenID Connect Back-Channel Logout 1.0](https://openid.net/specs/openid-connect-backchannel-1_0-final.html)
- [ADR-011: Production OIDC Authentication](011-production-oidc-authentication.md)
- [ADR-012: Guarded Multi-User Enablement](012-guarded-multi-user-enablement.md)
- [ADR-013: Personal-Tenant OIDC JIT Provisioning](013-personal-tenant-oidc-jit-provisioning.md)
