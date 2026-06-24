# GameHub Authentication

GameHub keeps the existing API-key and local-account modes for compatibility and supports
provider-neutral OIDC as the production authentication direction. Tenant identity always comes
from a server-verified GameHub account mapping, never from request bodies or provider tenant claims.

Production multi-user operation is guarded by a separate operator switch that defaults to false.
See [Guarded Multi-User Operations](guarded-multi-user-enablement.md) and
[ADR-012](adr/012-guarded-multi-user-enablement.md). Optional personal-tenant JIT provisioning is
defined separately by [ADR-013](adr/013-personal-tenant-oidc-jit-provisioning.md).

For a source-oriented explanation with sequence diagrams, trust boundaries, data model, failure
paths, and a guided reading order, see
[OIDC Authentication Architecture](oidc-authentication-architecture.md).

For a provider-specific production baseline, realm/client configuration, first-link walkthrough,
backup/restore guidance, and failure matrix, see
[Self-Hosted Keycloak OIDC Setup](keycloak-oidc-setup.md).

## Guarded Multi-User Enablement

`GAMEHUB_MULTI_USER_ENABLED` is the only production multi-user enablement switch. It defaults to
`false`, is read at process startup, and is mapped consistently to Core, Ingest, and Frontend by
Compose. OIDC configuration alone never activates provisioned tenants.

The operational states are distinct:

- **OIDC enabled** means the provider protocol is configured.
- **Readiness satisfied** means Core/BFF/Ingest trust settings are complete, the vault is ready,
  and every live account has an explicit mapping for the configured issuer.
- **Multi-user enabled** means the operator explicitly set the guarded switch to `true`.
- **Multi-user active** requires both readiness and the explicit switch.

Core requires mirrored Frontend auth, Secure-cookie, and switch states; independent strong API,
Ingest delegation, JWT, and BFF keys; valid OIDC; and linked live accounts. Ingest independently
requires the switch, delegation trust, and a configured vault encryption key. Missing, invalid,
reused, or contradictory enabled configuration fails startup. Frontend authentication routes fail
closed with a generic unavailable response.

An operator can inspect secret-free readiness through `GET /api/auth/multi-user/status` using the
configured operator API key. The response contains safe booleans and blocker codes only—never
configured values, secrets, tokens, account/tenant identifiers, counts, claims, credentials, or
library data. No readiness state is exposed to browser JavaScript.

When active, ordinary Core endpoints require GameHub JWTs and ordinary Ingest endpoints require
Core tenant delegation. The legacy API key remains accepted only by Core endpoints that explicitly
name the operator scheme. Public registration, tenant switching, shared memberships, invitations,
and claim-derived roles remain unavailable. Personal-tenant JIT is available only through its
independent explicit provisioning mode.

## Production OIDC

OIDC uses Authorization Code Flow with PKCE through the server-side Next.js BFF. Before enabling
it, create a provider client with the exact callback URI for
`/api/auth/oidc/callback`. Configure these deployment variables without committing their values:

| Variable | Required value |
|---|---|
| `GAMEHUB_OIDC_ENABLED` | `true` only after every value and provider policy below is ready |
| `GAMEHUB_OIDC_PROVISIONING_MODE` | `Disabled` (default) or `PersonalTenantJit`; unknown values fail startup |
| `GAMEHUB_OIDC_ISSUER` | Exact HTTPS issuer from provider discovery metadata |
| `GAMEHUB_OIDC_CLIENT_ID` | Audience/client ID registered for GameHub |
| `GAMEHUB_OIDC_CLIENT_SECRET` | Provider client secret when a confidential client is required; omit for a supported public PKCE client |
| `GAMEHUB_OIDC_REDIRECT_URI` | Exact external HTTPS callback URL registered at the provider |
| `GAMEHUB_OIDC_SCOPES` | Space-separated scopes including `openid`; default `openid profile email` |
| `GAMEHUB_OIDC_COOKIE_KEY` | Base64 encoding of 32 random bytes used only for BFF correlation-cookie encryption |
| `GAMEHUB_OIDC_BFF_SERVICE_KEY` | Separate random server-to-server key authorizing only the BFF to call Core's OIDC exchange/link endpoints |
| `GAMEHUB_OIDC_METADATA_ADDRESS` | Optional explicit HTTPS discovery address; Core otherwise derives it from the issuer |
| `GAMEHUB_OIDC_ALLOWED_ALGORITHMS` | Comma-separated asymmetric ID-token allowlist; default `RS256` |
| `GAMEHUB_AUTH_COOKIE_SECURE` | Must be `true` for OIDC |
| `GAMEHUB_FRONTEND_AUTH_ENABLED` | Must be `true` for OIDC and GameHub session cookies |
| `GAMEHUB_JWT_SIGNING_KEY` | Existing GameHub session-signing key, at least 32 bytes |

Generate the correlation and BFF service keys independently with `openssl rand -base64 32`. Keep
them and any provider client secret outside the repository. Partial, non-HTTPS,
symmetric-algorithm, or insecure-cookie OIDC configuration fails closed.

An existing account must explicitly approve its provider identity while signed in:

```http
GET /api/auth/oidc/start?intent=link&returnTo=/admin
```

Normal login begins at `GET /api/auth/oidc/start?returnTo=/library`; the login page exposes the
same route when OIDC is enabled. State, nonce, exact redirect, correlation expiry, and the S256
PKCE verifier are checked before Core independently validates the ID token against issuer,
audience, signing keys, lifetime, nonce, and allowed algorithms.

Core stores only an explicit `(issuer, subject) -> (account, tenant)` mapping. It never links by
email and never accepts a tenant, account, organization, or role choice from browser parameters or
provider claims. Pending-deletion and deleted accounts cannot authenticate or relink.

### Provisioning choices

`GAMEHUB_OIDC_PROVISIONING_MODE` is independent from both OIDC enablement and the guarded switch:

- `Disabled` keeps operator-only bootstrap/provisioning and the existing explicit first-link flow.
  Unknown subjects receive the same generic authentication failure as before.
- `PersonalTenantJit` permits a fully validated unknown subject from the exact configured issuer
  to receive one server-identified personal tenant while guarded multi-user mode is active.

OIDC alone, JIT with the gate disabled, and unknown configuration values create nothing. JIT
creates the tenant, `ExternalOidc` account, exact mapping, normal refresh session, and one
tenant-owned secret-free activity row in a single transaction. New tenants start platform sync
disabled. Email, name, group, role, organization, and tenant-like provider claims select nothing;
Keycloak realm/client/authentication policy controls who can reach this opt-in flow.

External accounts have no normalized local email and no password hash. Provider email may be
omitted and is never a login or linking key. Local token issuance, password change, and local
recovery/deletion confirmation reject external accounts. Existing local and explicitly linked
accounts remain unchanged.

### Recovery, MFA, and email verification

The configured identity provider owns recovery, MFA, email verification, credential security, and
authentication abuse controls. GameHub does not duplicate those flows and does not treat an email
claim as account ownership. Provider recovery restores access only to the already linked stable
issuer/subject identity; it cannot choose or change a GameHub tenant.

The provider must offer HTTPS discovery/JWKS, nonce-bearing signed ID tokens, and the selected
asymmetric algorithm. This release supports one issuer/client configuration and optional personal
JIT, but no unlink/subject replacement or provider back-channel logout. Provider outages
fail closed for new login/link attempts; already-issued GameHub sessions retain their normal local
expiry and revocation behavior. A reverse proxy must preserve the external HTTPS scheme and host so
the BFF can compare the callback to the registered redirect URI exactly. See ADR-011.

## Enable JWT

Configure separate external, internal, and JWT secrets before bootstrapping the first account:

```dotenv
GAMEHUB_API_KEY=replace-with-a-strong-api-key
GAMEHUB_INGEST_SERVICE_KEY=replace-with-a-different-strong-random-key
GAMEHUB_JWT_SIGNING_KEY=replace-with-a-random-secret-of-at-least-32-bytes
GAMEHUB_FRONTEND_AUTH_ENABLED=true
```

Restart the Compose services after changing authentication configuration. Leaving
`GAMEHUB_JWT_SIGNING_KEY` empty and `GAMEHUB_FRONTEND_AUTH_ENABLED=false` preserves the legacy
local mode without a login screen.

`GAMEHUB_INGEST_SERVICE_KEY` is an internal Core-to-Ingest credential and must not reuse the
external API key. Only requests authenticated with this service key may supply
`X-GameHub-Tenant-Id`; normal API-key requests carrying that header are rejected.

## Configure Platform Credential Encryption

Generate a separate 32-byte key and store only its Base64 representation in the deployment
environment:

```bash
openssl rand -base64 32
```

Assign the result to `GAMEHUB_CREDENTIAL_KEY_1` and keep
`GAMEHUB_CREDENTIAL_KEY_VERSION=1`. Credential writes return HTTP 503 while no key is configured.
For rotation, retain the old version under `Credentials__Keys__<old-version>`, add the new key,
and change `Credentials__ActiveKeyVersion`; old keys cannot be removed until their rows are
re-encrypted or deleted. See ADR-009 for the storage and trust-boundary decision.

## Bootstrap the Tenant Account

Bootstrap is accepted only once per tenant and requires the configured API key:

```http
POST /api/auth/bootstrap
X-Api-Key: replace-with-a-strong-api-key
Content-Type: application/json

{
  "email": "owner@example.com",
  "password": "use-a-long-unique-password",
  "displayName": "Owner"
}
```

Passwords must contain 12–128 characters and are stored only as ASP.NET password hashes.

## Request a Token

```http
POST /api/auth/token
Content-Type: application/json

{
  "email": "owner@example.com",
  "password": "use-a-long-unique-password"
}
```

Use the returned access token as `Authorization: Bearer <token>`. Tokens expire after 60 minutes by default, and token requests are limited to 10 attempts per minute per remote IP.

This local token endpoint is a compatibility boundary, not active-mode production login:

- With guarded multi-user mode disabled, the default tenant retains normal local access/refresh.
- A provisioned tenant cannot obtain a full session while the gate is disabled.
- With guarded multi-user mode disabled, an unlinked provisioned account may receive only a
  ten-minute `oidc_link_only` access token. It has no refresh token and Core accepts it only for
  `POST /api/auth/oidc/link`; this prepares the account before activation.
- A linked account cannot use its local password to obtain a production session.
- A pending-deletion account may receive only a ten-minute `account_deletion_only` token for the
  exact idempotent deletion retry route.

After the first link, the BFF clears the restricted cookie and requires a fresh OIDC login. Existing
GameHub sessions issued before an enable/disable change retain ADR-011's normal local expiry,
database live-account check, rotation, replay detection, and revocation behavior.

The response also contains an opaque `refreshToken` and `refreshTokenExpiresAtUtc`. The access
token is a readable, signed JWT carrying the trusted tenant claim; the refresh token contains
only random bytes and is stored server-side as a SHA-256 hash.

## Refresh a Session

```http
POST /api/auth/refresh
Content-Type: application/json

{
  "refreshToken": "the-current-opaque-refresh-token"
}
```

Every successful refresh returns a new access token and a new refresh token. The submitted
refresh token is consumed and cannot be used again. Reusing it is treated as possible token
theft and revokes the account's remaining active refresh sessions.

Refresh sessions expire after 30 days by default. Configure a value from 1–90 days with
`GAMEHUB_JWT_REFRESH_TOKEN_DAYS`.

## Revoke a Session

```http
POST /api/auth/revoke
Content-Type: application/json

{
  "refreshToken": "the-current-opaque-refresh-token"
}
```

Revocation returns `204 No Content` whether the token was active, expired, or unknown. Clients
should always discard their local access and refresh tokens after this request.

## Change the Password

`PUT /api/auth/password` requires a bearer token and both the current and replacement password.
The current password must verify, the new password must be 12–128 characters and different, and
Core revokes every refresh session in the same transaction as the password hash change.

## Revoke Every Session

`POST /api/auth/revoke-all` requires a bearer token and revokes every refresh session for the
signed account/tenant pair. Password changes and global revocation invalidate refresh tokens
immediately. Existing stateless access JWTs cannot be recalled and remain valid until their short
expiry; clients must discard them. The GameHub BFF clears both HttpOnly cookies after success.

## Delete the Account and Tenant

For local accounts, `DELETE /api/auth/account` requires a bearer token and
`{ "currentPassword": "..." }`. Core first
disables sync, records a pending-deletion state, and revokes refresh sessions. It then asks Ingest
to delete all delegated raw data, audit rows, and encrypted platform credentials. Core deletes its
library, operational history, account, and provisioned tenant only after Ingest succeeds.

An Ingest failure returns HTTP 502; a Core cleanup failure after Ingest returns HTTP 503. Both use
`account_deletion_incomplete`, never claim success, and are safe to retry. Pending tenants cannot
use ordinary JWT-protected APIs. The seeded default tenant row is retained empty and disabled for
single-user upgrade/bootstrap compatibility; all of its account-owned data is still removed.

Local accounts continue to confirm deletion with their current password. An `ExternalOidc`
account instead starts `/api/auth/oidc/start?intent=delete`. The BFF forces `prompt=login` and
`max_age=0`; Core revalidates issuer, subject, audience, signature, nonce, lifetime, and a recent
signed `auth_time`, then requires the exact existing mapping. Core issues only a ten-minute
`account_deletion_only` token, which the BFF immediately consumes on `DELETE /api/auth/account`.
Bearer validation rejects that token on every other method or route. A pending cleanup can be
retried through `intent=delete-retry` after another fresh OIDC authentication.

Deletion stores an exact external-identity tombstone before cascading the live mapping. The same
deleted subject therefore cannot silently JIT-provision a replacement tenant.

## Local Compatibility Recovery Boundary

GameHub currently has no trusted email provider and therefore offers no forgot-password,
email-reset, recovery-code, or administrative password-replacement API. Losing the password and
all active sessions requires trusted operator recovery outside the public API. This must be
revisited separately from provider recovery. See ADR-010 and ADR-011.

In active mode the stored password hash remains only for rollback compatibility, the narrowly
scoped first-link bridge, and current-password confirmation on existing lifecycle operations. It
cannot create a normal production session after the account has an OIDC mapping.

## Frontend Session

The Next.js frontend signs in through `POST /api/auth/login` on its own origin. Its BFF calls
Core, stores the returned access and refresh tokens in HttpOnly, SameSite cookies, and forwards
the access token to Core for `/api/core/*` requests. When the access token expires, the BFF
rotates the refresh token without exposing either secret to React or `localStorage`.

Opening a protected page without a valid session redirects to `/login`. The navigation's sign-
out action revokes the current refresh session and deletes both cookies.

The default Compose deployment uses plain HTTP and therefore sets
`GAMEHUB_AUTH_COOKIE_SECURE=false`. Set it to `true` whenever a reverse proxy serves the frontend
to browsers over HTTPS; browsers intentionally reject Secure cookies over plain HTTP.

OIDC is stricter: enabling it while secure cookies are disabled is rejected. Neither enabling OIDC
nor selecting JIT activates production multi-user operation. After all readiness checks pass,
the operator must still explicitly set `GAMEHUB_MULTI_USER_ENABLED=true` across all components and
restart. Disabling it later preserves all data, blocks new provisioned-tenant login, limits
background jobs to the default tenant, and leaves existing sessions to expire or be revoked under
ADR-011.

GameHub logout does not terminate the Keycloak SSO session. Disabling or deleting a Keycloak user
does not immediately revoke GameHub sessions already issued for that subject. Use GameHub
revoke-all or account deletion when immediate local termination is required. Back-channel logout
and provider-event lifecycle synchronization remain future work.
