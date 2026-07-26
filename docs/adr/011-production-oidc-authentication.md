# ADR-011: Production Authentication with OIDC

**Date:** 2026-06-21
**Status:** Accepted
**Deciders:** @jankahnt

## Context

Roadmap item #8 has tenant isolation, portability, encrypted platform credentials, rotating
GameHub sessions, and an initial local-account lifecycle. The local email/password flow has no
trusted recovery, MFA, or email-verification authority and is therefore not a production
multi-user authentication system.

GameHub needs a provider-neutral production trust boundary without allowing provider claims,
browser parameters, or email similarity to select a tenant.

## Decision

### Protocol and component boundary

GameHub uses OpenID Connect on OAuth 2.0 Authorization Code Flow with PKCE (`S256`).

- The server-side Next.js BFF creates cryptographically random state, nonce, and PKCE verifier
  values. It stores them for at most ten minutes in an AES-256-GCM authenticated HttpOnly,
  Secure, SameSite=Lax correlation cookie.
- The BFF verifies the exact state, callback URI, correlation lifetime, and PKCE challenge before
  it exchanges the code. Authorization and token endpoints come from HTTPS discovery metadata
  whose issuer must exactly match configuration.
- Core independently validates the returned ID token against HTTPS discovery/JWKS using the exact
  configured issuer, audience, lifetime, signature, nonce, and an explicit asymmetric algorithm
  allowlist. A BFF result alone is never treated as proof of identity.
- Core's OIDC exchange and linking endpoints additionally require a separate fixed-time-compared
  BFF service key. Direct callers therefore cannot bypass the BFF's state, redirect, and PKCE
  correlation checks merely by presenting an ID token.
- After validation, Core issues the existing short-lived GameHub JWT plus rotating opaque refresh
  session. Browser code receives neither token; the BFF stores both in HttpOnly, SameSite=Lax
  cookies, and OIDC configuration requires Secure cookies.

### Stable identity and tenant mapping

An external identity is the case-sensitive pair `(Issuer, Subject)`. Email is display metadata,
never an identity key.

- An external identity can be linked only while an existing GameHub account JWT is valid. The
  signed GameHub account and tenant choose the destination; the linking request contains no
  account or tenant selector.
- The database uniquely binds `(Issuer, Subject)` to one account and enforces a composite
  `(TenantId, UserAccountId)` foreign key. One account may have only one subject for a configured
  issuer unless a future explicit replacement flow is designed.
- Login performs an exact issuer/subject lookup. It never falls back to email, `email_verified`,
  tenant, organization, group, or role claims. Equal emails under different subjects or issuers
  remain separate and unlinked.
- Under this ADR's default operator mode, OIDC does not provision accounts or tenants; existing
  local provisioning and authenticated explicit link are required. ADR-013 later adds a separate
  default-off personal-tenant JIT mode without turning OIDC itself into public registration.
- Pending-deletion accounts cannot link or authenticate. Account deletion cascades to external
  mappings, so a deleted identity cannot recreate or regain access to a tenant.

### Identity-provider responsibility

The trusted provider owns primary authentication, account recovery, MFA enrollment/challenges,
email ownership verification, abuse controls, and its credential lifecycle. GameHub does not
implement provider password reset, recovery codes, or MFA and must not infer those assurances
from an unvalidated email claim. Operators must configure provider policies appropriate for their
deployment before treating OIDC as production authentication.

Provider recovery restores access only to an already linked issuer/subject identity. It cannot
change the GameHub tenant mapping.

### Failure behavior

- Enabling OIDC with a missing GameHub signing key, issuer, audience, HTTPS endpoint, cookie key,
  BFF service key, Secure-cookie setting, or algorithm allowlist fails closed (Core startup failure
  or BFF 503).
- Discovery/JWKS or token-endpoint outages return an unavailable/failed login and never fall back
  to unvalidated claims or email matching. Core refresh sessions that were already issued continue
  under their normal lifetime and revocation rules.
- Issuer, audience, algorithm, state, nonce, callback URI, PKCE, signature, and expiry mismatches
  are rejected. Error responses do not reveal whether an external identity is linked.
- Signing-key rotation permits one forced metadata refresh after a key miss; all other validation
  failures remain closed.

### Local compatibility and multi-user status

The API-key bootstrap, local password login, refresh-session, and default-tenant paths remain for
upgrade compatibility and single-user/self-hosted operation. They are explicitly not a
production-ready account/recovery system. OIDC is separately opt-in and does not enable public
registration or multi-user mode.

This ADR closes the production authentication/recovery design and implementation gate only.
ADR-012 subsequently adds the separate default-off guarded multi-user mechanism and rollout review.
OIDC still never activates the mode by itself; an operator must satisfy readiness and explicitly
enable it.

### Logout boundary

User-triggered sign-out implements standards-based OIDC RP-Initiated Logout as a top-level browser
navigation. The BFF first attempts to revoke the GameHub refresh session and expires every GameHub
authentication cookie. When the request carried a refresh-session cookie, the BFF may then use the
exact configured issuer's discovery document and its validated HTTPS `end_session_endpoint`, with
the server-configured client ID and `/login` on the configured callback origin. Browser input
cannot choose either redirect. Missing/invalid metadata, provider outages, or disabled OIDC fall
back to local `/login` without preventing local cleanup or normal OIDC login.

GameHub does not retain provider tokens for browser logout and sends no `id_token_hint`. ADR-015
later adds independently validated Back-Channel Logout: standard `sid` targets one durable local
session family, while `sub` without `sid` advances the account epoch and invalidates every family.
Broader provider event propagation and administrative lifecycle synchronization remain unsupported.

## Consequences

### Positive

- GameHub relies on a standards-based provider for credentials, recovery, MFA, and verified-email
  policy while retaining tenant authorization locally.
- Compromised or misleading email, role, organization, and tenant-like claims cannot redirect an
  identity to another library.
- Provider outages and partial configuration have a defined fail-closed result.
- Existing accounts, default-tenant data, and libraries survive SQLite and PostgreSQL upgrades.
  ADR-015's later session-family upgrade deliberately revokes legacy sessions that lack a stable
  family reference.

### Limitations

- One OIDC issuer/client configuration is supported per deployment.
- Only explicitly configured asymmetric signing algorithms are accepted.
- Account creation remains operator controlled by default. ADR-013 adds optional personal-tenant
  JIT only through an independent mode and active guarded gate.
- Linking requires a current GameHub session. There is no unlink or subject-replacement endpoint;
  those operations need a separate reauthentication and recovery design.
- RP-Initiated browser logout is implemented without provider-token storage. ADR-015 later adds
  provider-session and subject-wide Back-Channel Logout without changing this login trust model.
  Other token revocation events and administrative lifecycle synchronization remain unimplemented.
- The provider must be reachable over HTTPS and supply conforming discovery metadata and signed
  ID tokens with nonce support.

## References

- [GameHub OIDC Authentication Architecture](../oidc-authentication-architecture.md)
- [ADR-013: Opt-in Personal-Tenant OIDC JIT Provisioning](013-personal-tenant-oidc-jit-provisioning.md)
- [ADR-015: OIDC Back-Channel Logout](015-oidc-back-channel-logout.md)
- [OpenID Connect Core 1.0](https://openid.net/specs/openid-connect-core-1_0.html)
- [OAuth 2.0 Authorization Code Grant](https://www.rfc-editor.org/rfc/rfc6749#section-4.1)
- [Proof Key for Code Exchange (RFC 7636)](https://www.rfc-editor.org/rfc/rfc7636)
