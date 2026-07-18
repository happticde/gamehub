# ADR-015: OIDC Back-Channel Logout and Immediate Session Invalidation

**Date:** 2026-07-18
**Status:** Accepted
**Deciders:** @jankahnt

## Context

ADR-011 through ADR-013 authenticate one exact external `(issuer, subject)`, issue GameHub access
JWTs, and persist rotating GameHub refresh sessions. RP-Initiated Logout revokes the browser's
current refresh session and ends its provider SSO session, but it cannot receive a provider- or
administrator-initiated logout.

GameHub access JWTs were valid until expiry after signature validation and a live-account check.
Revoking refresh sessions therefore prevented future rotation but did not immediately stop an
already issued access JWT. A Keycloak administrator logout or Account Console logout could end the
Keycloak session while leaving the independent GameHub session usable.

## Decision

### Public and internal request boundaries

The public Back-Channel Logout URI is:

```text
https://<gamehub-host>/api/auth/oidc/backchannel-logout
```

Keycloak sends one `application/x-www-form-urlencoded` POST containing `logout_token`. The Next.js
BFF accepts exactly one bounded string value, returns no response body, uses no browser cookies,
and forwards the token to Core over the existing independent BFF service-key channel. Core remains
the only component that validates provider proof, resolves identity ownership, or changes sessions.

Core accepts only a signed compact Logout Token that satisfies the Back-Channel Logout profile:

- exact configured issuer and GameHub client audience;
- configured asymmetric signing algorithm and discovery/JWKS key;
- a valid `iat` no older than five minutes, with at most one minute of future clock skew;
- bounded `jti` and `sub` claims;
- the `http://schemas.openid.net/event/backchannel-logout` event;
- no `nonce` claim;
- valid `azp` when present or required by multiple audiences; and
- an absent `typ` or exact `typ=logout+jwt`; any other supplied type is rejected.

Invalid protocol messages return `400`; discovery/JWKS or Core availability failures return a
retryable `503`; an accepted message returns `200`. Unknown, deleted, already logged-out, and
repeated identities deliberately share the successful external response.

Encrypted Logout Tokens are not supported. The configured provider must sign a compact JWS using
the same asymmetric trust policy as GameHub ID tokens.

### Account-wide identity boundary

GameHub currently has one owner account per tenant and does not persist a Keycloak `sid` mapping to
individual GameHub refresh sessions. This milestone therefore requires a validated `sub` and treats
an optional `sid` as non-authoritative. A valid exact `(issuer, subject)` logout invalidates every
GameHub session for that account across browsers and devices. It never accepts a tenant, account,
email, group, role, organization, or subject from an unsigned request field.

Session-specific `sid` logout would require a separately designed durable provider-session mapping.
It must not be approximated by accepting `sid` as an account or tenant selector.

### Immediate GameHub access invalidation

`UserAccount.SessionVersion` is a monotonic account session epoch. Every newly issued GameHub
access JWT carries the signed `gamehub:session_version` claim. Normal bearer validation compares
the signed account, tenant, and epoch with live database state.

Each refresh-session row also captures the account epoch at issuance. Rotation requires an exact
match with the current account epoch. This closes the race where a login loaded an account before
logout but attempted to persist or use its refresh session after the logout transaction committed.

Back-channel logout atomically:

1. resolves the exact persisted external identity;
2. increments the account session epoch;
3. revokes all of the account's refresh sessions while the epoch prevents any stale in-flight
   issuance from rotating later;
4. writes a secret-free tenant-owned activity entry; and
5. stores a short-lived replay marker.

This makes existing access JWTs unusable on their next Core request and prevents every refresh
rotation. Browser cookies cannot be deleted through a server-to-server provider call, but they no
longer authorize anything and the normal BFF refresh failure path clears them on the next request.

Upgrade compatibility is explicit: existing accounts start at epoch zero. Access JWTs issued by an
older version have no epoch claim and are accepted only while the durable epoch is still zero. The
first account-wide logout increments it and permanently invalidates those legacy tokens.

### Replay and audit behavior

Core hashes the exact validated issuer and `jti` with SHA-256 and stores only that hash. The unique
database index makes concurrent duplicate delivery idempotent. Markers expire after ten minutes;
the validator's five-minute `iat` window makes the original token unusable before cleanup. Raw
Logout Tokens, subjects, JWT IDs, account IDs, tenant IDs, and provider claims are never logged.

The account epoch update, refresh revocation, tenant activity, and replay marker commit in one
database transaction. SQLite and PostgreSQL enforce the same model.

## Consequences

### Positive

- Keycloak-initiated and administrative logout can terminate GameHub authorization without a
  browser round trip.
- Both access and refresh paths close together instead of relying on the access-token lifetime.
- Exact identity mapping and tenant isolation remain unchanged.
- Duplicate delivery is durable, safe, and non-enumerating.
- No provider token or session material reaches browser JavaScript or `localStorage`.

### Costs and limitations

- Logout is account-wide; one Keycloak session logout signs the account out on every GameHub
  browser/device.
- A valid `sub` is required even though the generic standard also permits `sid`-only messages.
- Disabling/deleting a provider user is not treated as a logout unless Keycloak emits the normal
  Back-Channel Logout request. Provider-event reconciliation remains future work.
- Browser cookies remain physically present until the next request, though their server-side
  authorization is immediately invalid.
- This does not add SCIM, subject replacement, unlinking, invitations, or general SaaS readiness.

## References

- [OpenID Connect Back-Channel Logout 1.0](https://openid.net/specs/openid-connect-backchannel-1_0.html)
- [ADR-011: Production OIDC Authentication](011-production-oidc-authentication.md)
- [ADR-012: Guarded Multi-User Enablement](012-guarded-multi-user-enablement.md)
- [ADR-013: Personal-Tenant OIDC JIT Provisioning](013-personal-tenant-oidc-jit-provisioning.md)
