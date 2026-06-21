# GameHub OIDC Authentication Architecture

This document is a learning-oriented walkthrough of GameHub's production authentication path.
It explains the protocol, component boundaries, data model, source files, validation order,
failure behavior, and tests in enough detail to follow one login from the browser to a
tenant-authorized Core request.

The normative architectural decision is [ADR-011](adr/011-production-oidc-authentication.md).
Operational configuration is documented in [authentication.md](authentication.md). This guide
focuses on how and why the implementation works.

> **Important scope:** OIDC is an opt-in production authentication path. It does not enable public
> registration or production multi-user operation. Guarded multi-user enablement remains a
> separate Roadmap #8 step.

## 1. The mental model

GameHub uses two related standards with different jobs:

- **OAuth 2.0 Authorization Code Flow** transports a short-lived authorization code from the
  identity provider to GameHub's callback and exchanges it server-to-server.
- **OpenID Connect (OIDC)** adds authenticated identity to OAuth. The provider returns a signed
  **ID token** containing an issuer (`iss`) and provider-stable subject (`sub`).

OAuth by itself does not answer “who signed in?” An OAuth access token primarily authorizes calls
to a provider-owned API. GameHub therefore requires the `openid` scope and validates the OIDC ID
token. It does not use the provider access token to authorize GameHub library APIs.

After validating the external identity, GameHub issues its own tokens:

| Artifact | Issuer | Consumer | Purpose |
|---|---|---|---|
| Authorization code | Identity provider | Next.js BFF | One-time input to the provider token endpoint |
| Provider ID token | Identity provider | Core, via the BFF | Cryptographic proof of `(issuer, subject)` |
| Provider access token | Identity provider | Not used by GameHub | Would authorize provider APIs, not GameHub APIs |
| GameHub access JWT | Core | Core APIs, forwarded by the BFF | Short-lived account and tenant authorization |
| GameHub refresh token | Core | Core refresh endpoint, called by the BFF | Rotating, revocable browser session continuation |

The central rule is:

```text
validated provider (issuer, subject)
    -> persisted ExternalIdentity mapping
    -> existing GameHub UserAccount
    -> exactly one Tenant
    -> GameHub JWT containing the server-selected tenant_id
```

Email, display name, provider roles, organization claims, and tenant-like claims are never part of
that authorization chain.

## 2. Actors and responsibilities

| Actor | Owns | Must not own |
|---|---|---|
| Browser | Top-level navigation, provider UI, opaque cookies | Provider secrets, PKCE verifier, ID token validation, tenant choice |
| Next.js BFF | OIDC redirect protocol, state/nonce/PKCE, encrypted correlation cookie, code exchange, GameHub session cookies | Durable account/tenant mapping or final trust in ID-token claims |
| Identity provider | Primary authentication, credential recovery, MFA, email-verification policy, signed ID token | GameHub tenant selection or GameHub roles |
| Core | ID-token validation, external identity mapping, account/tenant state, GameHub JWT and refresh sessions | Provider password/MFA/recovery workflow |
| Core database | Unique identity ownership, composite account/tenant relationship, refresh-session state | Provider tokens or provider credentials |

## 3. Trust boundaries

```mermaid
flowchart LR
    Browser["Browser<br/>untrusted protocol carrier"]
    BFF["Next.js BFF<br/>redirect and cookie boundary"]
    IdP["Trusted OIDC Provider<br/>authentication authority"]
    Core["Core Service<br/>identity and tenant authority"]
    DB[("Core Database<br/>durable mapping and sessions")]

    Browser -->|"Top-level navigation; opaque cookies"| BFF
    BFF -->|"Authorization request with state, nonce, PKCE challenge"| IdP
    IdP -->|"Code and state through browser redirect"| BFF
    BFF -->|"Code plus private PKCE verifier"| IdP
    IdP -->|"Signed ID token"| BFF
    BFF -->|"ID token, expected nonce, BFF service key"| Core
    Core -->|"Discovery and JWKS over HTTPS"| IdP
    Core -->|"Exact issuer/subject lookup"| DB
    Core -->|"GameHub access and refresh tokens"| BFF
    BFF -->|"HttpOnly session cookies"| Browser
```

There are three distinct proof transitions:

1. **Browser callback to BFF:** state, exact redirect, encrypted transaction, age, and PKCE
   correlation prove that the callback belongs to the BFF-created browser transaction.
2. **Provider ID token to Core:** signature, issuer, audience, lifetime, algorithm, nonce, and
   authorized-party checks prove the provider identity.
3. **External identity to tenant:** the persisted composite mapping proves which local account and
   tenant the identity may access.

Skipping any one transition would create a different vulnerability. For example, a valid ID token
does not prove that the browser callback belongs to the initiating session, and a correct callback
does not prove which GameHub tenant the provider identity owns.

## 4. Source-code map

Read the implementation in this order:

1. [`frontend/lib/oidc-security.ts`](../frontend/lib/oidc-security.ts)
   - Generates state, nonce, verifier, and challenge.
   - Encrypts/authenticates the correlation transaction.
   - Performs callback correlation checks.
2. [`frontend/lib/oidc.ts`](../frontend/lib/oidc.ts)
   - Parses server-only BFF configuration.
   - Fetches discovery metadata and pins the configured issuer.
3. [`frontend/app/api/auth/oidc/start/route.ts`](../frontend/app/api/auth/oidc/start/route.ts)
   - Creates the transaction and redirects to the provider.
4. [`frontend/app/api/auth/oidc/callback/route.ts`](../frontend/app/api/auth/oidc/callback/route.ts)
   - Opens the transaction, checks the callback, exchanges the code, and delegates to Core.
5. [`services/core/Services/OidcTokenValidator.cs`](../services/core/Services/OidcTokenValidator.cs)
   - Parses fail-closed Core configuration.
   - Retrieves discovery/JWKS metadata.
   - Validates the provider ID token.
6. [`services/core/Services/ExternalIdentityService.cs`](../services/core/Services/ExternalIdentityService.cs)
   - Explicitly links and exactly resolves `(issuer, subject)` mappings.
7. [`services/core/Controllers/AuthController.cs`](../services/core/Controllers/AuthController.cs)
   - Enforces the BFF-only boundary.
   - Orchestrates link/login and issues GameHub sessions.
8. [`services/core/Data/ExternalIdentity.cs`](../services/core/Data/ExternalIdentity.cs) and
   [`services/core/Data/CoreContext.cs`](../services/core/Data/CoreContext.cs)
   - Define database ownership and uniqueness constraints.
9. [`frontend/lib/auth-session.ts`](../frontend/lib/auth-session.ts) and
   [`frontend/app/api/core/[...path]/route.ts`](../frontend/app/api/core/[...path]/route.ts)
   - Store, refresh, and forward the resulting GameHub session.

## 5. Configuration flows through two servers

The same trust anchors are deliberately configured in both Next.js and Core, but the secret
material and responsibilities differ.

| Deployment variable | Next.js/BFF use | Core use |
|---|---|---|
| `GAMEHUB_OIDC_ENABLED` | Exposes the OIDC flow | Enables ID-token validation |
| `GAMEHUB_OIDC_ISSUER` | Pins discovery issuer and constructs discovery URL | Pins discovery issuer and token `iss` |
| `GAMEHUB_OIDC_CLIENT_ID` | Sends OAuth `client_id` | Requires ID-token audience |
| `GAMEHUB_OIDC_CLIENT_SECRET` | Optional confidential-client token exchange | Never receives it |
| `GAMEHUB_OIDC_REDIRECT_URI` | Builds and verifies the callback | Never receives it |
| `GAMEHUB_OIDC_SCOPES` | Builds authorization request; must contain `openid` | Never receives it |
| `GAMEHUB_OIDC_COOKIE_KEY` | Encrypts correlation cookie | Never receives it |
| `GAMEHUB_OIDC_BFF_SERVICE_KEY` | Authenticates BFF calls to Core | Verifies BFF calls in fixed time |
| `GAMEHUB_OIDC_METADATA_ADDRESS` | Uses standard issuer discovery path | Optional explicit metadata location |
| `GAMEHUB_OIDC_ALLOWED_ALGORITHMS` | Not used for trust decisions | Closed asymmetric ID-token allowlist |
| `GAMEHUB_JWT_SIGNING_KEY` | Never receives it | Signs GameHub access JWTs |

OIDC startup is fail-closed:

- Core parses `OidcSettings` during process startup. If OIDC is enabled but JWT signing, issuer,
  audience, HTTPS metadata, allowed algorithms, or BFF service key is invalid, Core does not start.
- The BFF parses its full configuration at both start and callback. Missing authentication,
  Secure-cookie mode, client, redirect, cookie key, or BFF key returns a generic unavailable result.
- OIDC never falls back to email matching or an unsigned local session.

The cookie key, BFF service key, provider client secret, and GameHub JWT signing key have different
purposes and must be generated independently. Key reuse would merge trust boundaries that are
intentionally separate.

## 6. The correlation transaction

`OidcTransaction` is the BFF's memory across the provider redirect:

| Field | Visible to provider? | Security role |
|---|---:|---|
| `state` | Yes | Correlates callback to browser transaction; prevents login CSRF |
| `nonce` | Yes, then signed in ID token | Correlates ID token to this authorization request |
| `codeVerifier` | No; token endpoint only | Secret PKCE proof held by BFF |
| `codeChallenge` | Yes | SHA-256 commitment to verifier bound to authorization code |
| `redirectUri` | Yes | Exact registered callback binding |
| `returnTo` | No | Sanitized local destination after success |
| `intent` | No | Server-owned choice between login and link |
| `createdAt` | No | Ten-minute replay/expiry boundary |

The transaction is serialized as JSON and sealed with AES-256-GCM:

```text
v1.<base64url 12-byte IV>.<base64url ciphertext>.<base64url 16-byte tag>
```

Why encryption as well as cookie flags?

- `HttpOnly` prevents normal browser JavaScript from reading the cookie.
- `Secure` restricts browser transport to HTTPS.
- `SameSite=Lax` permits the top-level cross-site provider callback while blocking most
  cross-site subrequests.
- AES-GCM protects against a browser/user changing `intent`, redirect, return path, nonce, or PKCE
  verifier and also hides those values from the browser storage layer.
- Associated data (`gamehub-oidc-transaction-v1`) binds the ciphertext to this protocol purpose.

## 7. First-time identity linking flow

OIDC login cannot succeed until an external identity is explicitly linked to an existing GameHub
account. GameHub does not perform just-in-time provisioning and never searches by email.

The user begins while already signed in through the local compatibility path:

```http
GET /api/auth/oidc/start?intent=link&returnTo=/admin
```

```mermaid
sequenceDiagram
    autonumber
    participant U as Browser
    participant B as Next.js BFF
    participant P as OIDC Provider
    participant C as Core
    participant D as Core DB

    U->>B: GET /api/auth/oidc/start?intent=link
    B->>B: Require existing GameHub access cookie
    B->>B: Generate state, nonce, verifier, challenge
    B->>B: Seal correlation transaction
    B-->>U: Secure HttpOnly transaction cookie + 302
    U->>P: Authorization request
    P->>P: Authenticate user / MFA / provider policy
    P-->>U: Redirect with code + state
    U->>B: GET /api/auth/oidc/callback
    B->>B: Open cookie; validate state, redirect, age, PKCE pair
    B->>P: POST code + verifier to token endpoint
    P-->>B: Signed ID token
    B->>C: POST /api/auth/oidc/link + BFF key + GameHub JWT + ID token + nonce
    C->>C: Validate BFF key and existing GameHub JWT
    C->>P: Fetch/cache discovery and JWKS
    C->>C: Validate ID token and nonce
    C->>D: Link exact (issuer, subject) to signed account/tenant
    D-->>C: Mapping stored or conflict
    C-->>B: 204 or generic/conflict error
    B-->>U: Redirect to local return path; delete transaction cookie
```

The destination account and tenant come only from the existing GameHub JWT. The linking request
contains no account ID or tenant ID, and provider claims cannot override them.

The operation is idempotent for the same exact mapping. It rejects:

- an issuer/subject already owned by another account;
- a different subject from the same issuer already linked to this account;
- an unavailable or pending-deletion account;
- a cross-tenant combination rejected by the composite database foreign key.

## 8. Normal OIDC login flow

After linking, the login page navigates to:

```http
GET /api/auth/oidc/start?returnTo=/library
```

```mermaid
sequenceDiagram
    autonumber
    participant U as Browser
    participant B as Next.js BFF
    participant P as OIDC Provider
    participant C as Core
    participant D as Core DB

    U->>B: Start OIDC login
    B->>P: Authorization request (code, state, nonce, S256 challenge)
    P->>P: Primary authentication, MFA, recovery policy
    P-->>U: Browser redirect with one-time code + state
    U->>B: Callback with code + state and sealed cookie
    B->>B: Validate transaction correlation
    B->>P: Exchange code + private verifier
    P-->>B: Signed ID token
    B->>C: POST /api/auth/oidc/token + BFF key + ID token + expected nonce
    C->>P: Obtain cached/refreshed discovery and JWKS
    C->>C: Validate signature, issuer, audience, time, algorithm, nonce, azp
    C->>D: Find exact ExternalIdentity issuer/subject
    D-->>C: Live UserAccount and Tenant
    C->>D: Create rotating refresh session; update login timestamps
    C-->>B: GameHub access JWT + opaque refresh token
    B-->>U: HttpOnly session cookies + redirect; delete transaction cookie
```

### Detailed validation order in Core

`OidcTokenValidator.ValidateAsync` applies these gates:

1. OIDC must be enabled through validated startup configuration.
2. ID-token and nonce sizes are bounded before parsing or cryptography.
3. Discovery/JWKS metadata must be obtainable over HTTPS within the timeout.
4. Discovery issuer must exactly match configured issuer and contain signing keys.
5. JWT signature must validate with a configured asymmetric algorithm.
6. Token issuer must exactly match the configured issuer.
7. Audience must include the configured GameHub client ID.
8. `azp` must identify GameHub whenever required by OIDC multiple-audience rules.
9. `exp` is required; lifetime and clock skew are enforced.
10. `sub` must be present and bounded.
11. Signed token `nonce` must exactly match the BFF transaction nonce.
12. The validator projects only issuer, subject, and optional display email.

If the token references a signing key not present in the current cache, Core requests exactly one
metadata refresh. This supports normal provider key rotation without creating unbounded network
retries. Every other validation failure remains closed.

## 9. Why state, nonce, and PKCE are all required

These values are related but not interchangeable:

| Mechanism | Compared by | Primary attack it addresses |
|---|---|---|
| `state` | BFF callback vs sealed transaction | Login CSRF and callback injection |
| Exact redirect URI | BFF and provider | Code delivery to the wrong origin/path |
| PKCE verifier/challenge | BFF locally and provider token endpoint | Stolen/intercepted authorization code |
| `nonce` | Core vs signed ID-token claim | ID-token substitution/replay across authorization transactions |

Example: an attacker may possess a valid ID token for their own provider account. That does not
give them the sealed transaction cookie, matching state, private verifier, or another subject's
database mapping. Conversely, a stolen authorization code cannot be exchanged without the private
verifier, even if the attacker knows the public challenge.

## 10. Core identity and tenant data model

```mermaid
erDiagram
    Tenant ||--o| UserAccount : "has one owner in current model"
    UserAccount ||--o{ ExternalIdentity : "explicitly links"
    UserAccount ||--o{ RefreshTokenSession : "owns"

    Tenant {
        uuid Id PK
        datetime DeletionRequestedAtUtc
    }
    UserAccount {
        uuid Id PK
        uuid TenantId FK
        string NormalizedEmail UK
        string PasswordHash
    }
    ExternalIdentity {
        uuid Id PK
        string Issuer
        string Subject
        uuid TenantId FK
        uuid UserAccountId FK
        datetime CreatedAtUtc
        datetime LastLoginAtUtc
    }
    RefreshTokenSession {
        uuid Id PK
        uuid UserAccountId FK
        string TokenHash UK
        datetime ExpiresAtUtc
        datetime ConsumedAtUtc
        datetime RevokedAtUtc
    }
```

Relevant database invariants:

- Unique `(Issuer, Subject)` means one provider identity has one GameHub owner.
- Unique `(UserAccountId, Issuer)` prevents implicit subject replacement at a provider.
- Composite FK `(TenantId, UserAccountId) -> UserAccount(TenantId, Id)` prevents constructing a row
  with Account A and Tenant B.
- Cascade delete removes external mappings and refresh sessions when the account is deleted.
- No email or provider profile data participates in the relationship.

`ExternalIdentity` is deliberately not behind a tenant query filter because login starts before a
tenant is trusted. That makes the exact global issuer/subject lookup security-sensitive; trust is
established only after the mapping and its account/tenant relationship are loaded.

## 11. GameHub session after OIDC

OIDC ends when Core creates a normal GameHub session. From that point onward, API authorization is
identical to local-account login:

```mermaid
sequenceDiagram
    participant U as Browser
    participant B as Next.js BFF
    participant C as Core
    participant D as Core DB

    U->>B: Request page or /api/core/* with HttpOnly cookies
    B->>B: Read access cookie server-side
    B->>C: Authorization: Bearer GameHub access JWT
    C->>C: Validate GameHub signature, issuer, audience, lifetime
    C->>D: Verify account/tenant still live and not pending deletion
    C-->>B: Tenant-scoped API response
    B-->>U: Same-origin response

    Note over B,C: If access token is absent/expired and refresh exists
    B->>C: POST /api/auth/refresh with opaque refresh token
    C->>D: Hash lookup, expiry/replay/revocation checks, rotate session
    C-->>B: New GameHub access + refresh pair
    B-->>U: Replace HttpOnly cookies
```

The browser cannot read either GameHub token because both cookies are HttpOnly. The BFF converts
the access cookie into an `Authorization: Bearer` header for Core. A refresh token is random opaque
data; Core stores only its SHA-256 hash and rotates it on every successful refresh.

## 12. Cookie and secret inventory

| Value | Stored where | Browser-readable? | Lifetime/use |
|---|---|---:|---|
| OIDC transaction cookie | Browser, encrypted/authenticated | No (HttpOnly) | Maximum 10 minutes; deleted after callback |
| OIDC cookie key | Next.js server configuration | No | Encrypts only correlation transactions |
| Provider client secret | Next.js server configuration | No | Optional confidential-client authentication |
| BFF service key | Next.js and Core server configuration | No | Authenticates internal OIDC exchange/link calls |
| Provider ID token | BFF memory, then Core request | No | One callback; never stored in GameHub DB/cookies |
| GameHub access JWT | Browser cookie | No (HttpOnly) | Short-lived Core authorization |
| GameHub refresh token | Browser cookie | No (HttpOnly) | Rotating session secret; hashed in Core DB |
| GameHub JWT signing key | Core server configuration | No | Signs/verifies GameHub access JWTs |

`Secure=true` is mandatory for OIDC. `SameSite=Lax` is used because the provider returns through a
top-level cross-site navigation; `Strict` would commonly suppress the callback cookie. The
correlation cookie uses path `/api/auth/oidc`, while GameHub session cookies use `/` because the BFF
needs them for page and API requests.

## 13. Failure behavior

| Failure | Detection point | External behavior | Security reason |
|---|---|---|---|
| Missing/invalid OIDC config | Core startup or BFF route | Startup failure or 503 | Never run partially configured auth |
| Provider discovery unavailable | BFF/Core | Generic unavailable/failed login | No cached/unsigned claim fallback |
| Provider token endpoint unavailable | BFF | Generic failed login | No session without completed code exchange |
| Wrong state | BFF callback | Generic failed login | Reject callback not initiated by browser transaction |
| Wrong callback origin/path | BFF callback | Generic failed login | Enforce registered redirect boundary |
| Wrong/altered verifier | BFF and provider | Generic failed login | Reject intercepted code or altered transaction |
| Wrong nonce | Core | Generic 401 through callback | Reject token from another authorization transaction |
| Wrong issuer/audience/algorithm/signature | Core | Generic 401 | Reject untrusted token authority or client |
| Unknown issuer/subject mapping | Core mapping | Same generic 401 | Avoid identity/account enumeration |
| Email collision | Core mapping | No fallback; generic 401 | Prevent automatic account takeover |
| Pending-deletion account | Core mapping/JWT validation | Generic 401 | Lifecycle state is immediately fail-closed |
| Deleted account | DB cascade and mapping lookup | Generic 401 | Identity cannot recreate deleted ownership |
| Wrong/missing BFF service key | AuthController | Generic 401 | Prevent direct bypass of BFF protocol checks |

The callback always expires the transaction cookie, whether authentication succeeds or fails.
Public errors intentionally do not reveal which check failed or whether an identity is linked.

## 14. Provider outage and signing-key rotation

The BFF applies a ten-second timeout to discovery and token calls. Core's metadata HTTP client also
uses a ten-second timeout. New login/link attempts fail closed when the provider is unavailable.

Already-issued GameHub sessions are independent of provider availability:

- Existing access JWTs remain valid until their normal short expiry, subject to Core's live
  account/pending-deletion database check.
- Existing GameHub refresh sessions can rotate without contacting the provider.
- Provider recovery/MFA changes do not retroactively revoke GameHub refresh sessions because
  back-channel logout and provider event synchronization are not implemented.

For provider signing-key rotation, `ConfigurationManager` caches discovery/JWKS data. A
`SecurityTokenSignatureKeyNotFoundException` triggers one forced refresh and one revalidation.
Other failures do not trigger refresh loops.

## 15. Account lifecycle interaction

### Pending deletion

Pending-deletion accounts cannot:

- authenticate through an external mapping;
- link a new identity;
- use ordinary GameHub access JWTs;
- rotate refresh sessions.

Only the signed deletion retry path remains available under ADR-010.

### Completed deletion

Deleting `UserAccount` cascades to `ExternalIdentity` and `RefreshTokenSession`. A later provider
login therefore finds no mapping and returns the same generic authentication failure as any unknown
identity. OIDC does not reprovision the account or tenant.

### Provider recovery

Password recovery, MFA recovery, and email verification belong to the trusted provider. Successful
provider recovery restores the ability to prove the same `(issuer, subject)`; it does not change
the GameHub mapping. The local compatibility password has no public recovery flow.

## 16. Why GameHub never links by email

Email is unsuitable as the stable external identity key because:

- a provider may allow address changes;
- different providers can assert the same address;
- provider tenants/directories may reuse addresses;
- `email_verified` semantics and policies vary;
- an address may be recycled to another person;
- accepting a new issuer/subject because its email matches an existing account converts provider
  policy mistakes into GameHub account takeover.

OIDC defines `sub` as the locally unique, never-reassigned identifier for the end user at an
issuer. GameHub therefore stores the exact `(iss, sub)` pair and treats email as optional display
metadata only.

## 17. Test coverage mapped to threats

### Core tests

[`tests/GameHub.Tests/OidcAuthenticationTests.cs`](../tests/GameHub.Tests/OidcAuthenticationTests.cs)
covers:

- valid signature/issuer/audience/nonce identity projection;
- wrong issuer, audience, nonce, and signing algorithm;
- provider metadata outage;
- incomplete or unsafe OIDC configuration;
- same email under different subjects/issuers without auto-linking;
- Tenant A being unable to claim Tenant B's identity;
- manipulated cross-tenant database relationships;
- pending-deletion and deleted account rejection.

[`tests/GameHub.Tests/AuthControllerTests.cs`](../tests/GameHub.Tests/AuthControllerTests.cs) proves
that direct Core callers without the BFF key cannot exchange an ID token.

SQLite and real PostgreSQL upgrade preservation are covered in:

- [`tests/GameHub.Tests/TenantIsolationTests.cs`](../tests/GameHub.Tests/TenantIsolationTests.cs)
- [`tests/GameHub.Tests/PostgresTagRelationshipMigrationTests.cs`](../tests/GameHub.Tests/PostgresTagRelationshipMigrationTests.cs)

They verify that existing local accounts, refresh sessions, default tenant, unified games, and
library items survive the external-identity migration.

### BFF tests

[`frontend/tests/oidc-security.test.mjs`](../frontend/tests/oidc-security.test.mjs) directly tests:

- encrypted transaction round-trip;
- wrong state;
- wrong callback redirect;
- wrong PKCE verifier;
- tampered authenticated cookie;
- expired transaction.

## 18. Safe debugging checklist

Never print provider client secrets, BFF service keys, cookie keys, authorization codes, ID tokens,
GameHub access tokens, refresh tokens, or decrypted correlation transactions.

When diagnosing a failed login, check non-secret facts in this order:

1. Is OIDC explicitly enabled in both frontend and Core configuration?
2. Is frontend authentication enabled and Secure-cookie mode true?
3. Does the configured issuer exactly equal the discovery document's issuer, including path and
   trailing slash?
4. Does the provider registration contain the exact external HTTPS callback URL?
5. Does the reverse proxy preserve external scheme and host for `request.url`?
6. Does the requested scope include `openid`?
7. Is the GameHub client ID the ID-token audience and, when applicable, authorized party?
8. Is the provider's actual signing algorithm in Core's explicit allowlist?
9. Are browser time, Core time, and provider time reasonably synchronized?
10. Was the external identity explicitly linked before normal OIDC login?
11. Is the linked GameHub account live rather than pending deletion?

Useful safe signals include HTTP status, exception category, configured issuer hostname, key ID
(`kid`), algorithm name, and whether a mapping exists. Token bodies and secrets are not safe logs.

## 19. Provider integration checklist

Before enabling a provider:

- Register an OIDC client with Authorization Code Flow.
- Require PKCE with `S256` if the provider exposes such a policy switch.
- Register only the exact GameHub HTTPS callback URI.
- Select a trusted issuer and verify its discovery metadata/JWKS behavior.
- Choose the minimum scopes; `openid` is mandatory, while `profile`/`email` are optional for display.
- Choose and configure an asymmetric ID-token algorithm supported by both provider and Core.
- Configure provider MFA, recovery, email verification, abuse controls, and administrative access.
- Generate independent cookie, BFF service, and GameHub JWT keys outside the repository.
- Verify reverse-proxy forwarded host/protocol behavior.
- Bootstrap the existing GameHub owner and explicitly link the provider identity.
- Run login, logout, refresh, pending-deletion, provider-outage, and key-rotation exercises.
- Keep guarded multi-user enablement disabled.

## 20. Known boundaries and future work

- One issuer/client configuration is supported per deployment.
- There is no OIDC just-in-time tenant/account provisioning.
- There is no unlink or subject-replacement flow.
- There is no provider back-channel logout or provider-event-driven GameHub session revocation.
- Provider access tokens are not retained or used for provider APIs.
- Local account deletion still follows ADR-010's current-password confirmation path.
- Production multi-user enablement remains deliberately separate and disabled.

These boundaries are intentional. Expanding them safely requires new decisions about
reauthentication, recovery, administrative authority, tenant creation, provider event trust, and
cross-tenant rollout controls—not merely additional buttons in the frontend.
