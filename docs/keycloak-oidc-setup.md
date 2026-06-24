# Self-Hosted Keycloak OIDC Setup

This guide describes a production-oriented way to run a self-hosted Keycloak instance and use it
as GameHub's single OpenID Connect provider. It complements the provider-neutral configuration in
[GameHub Authentication](authentication.md) and the activation procedure in
[Guarded Multi-User Operations](guarded-multi-user-enablement.md).

The examples were validated against Keycloak 26.6.3, the current stable patch release on
2026-06-22. Review the official release and upgrade notes before every deployment rather than
silently following an old image tag.

## Security and product boundary

Keycloak authenticates people. GameHub remains authoritative for accounts, tenants, sessions, and
authorization:

```text
Keycloak user authentication
    -> validated exact Keycloak (issuer, subject)
    -> explicit GameHub external-identity link OR opt-in personal-tenant JIT
    -> server-selected GameHub account and tenant
    -> GameHub access JWT and rotating refresh session
```

Configuring Keycloak does not by itself create a GameHub account or tenant, does not enable public
registration, and does not activate guarded multi-user mode. Operators must explicitly choose one
of two models: operator provisioning plus first-link, or `PersonalTenantJit` together with active
guarded mode. Both remain closed Keycloak-controlled deployments.

GameHub currently supports one exact OIDC issuer/client per deployment. Use one dedicated Keycloak
realm for GameHub and do not use profile, email, group, organization, or role claims to select a
GameHub tenant.

## Recommended topology

Use separate public hostnames and separate databases:

```text
Browser
  | HTTPS
  +-- https://games.example.com  -> reverse proxy -> GameHub Frontend
  +-- https://auth.example.com   -> reverse proxy -> Keycloak :8080
                                                    Keycloak management :9000 (private only)

GameHub databases:  Core and Ingest ownership
Keycloak database:  realms, users, credentials, clients, subjects, and signing keys
```

The production baseline requires:

- a dedicated Keycloak PostgreSQL database and database account;
- TLS at Keycloak or a trusted TLS-terminating reverse proxy;
- a fixed public Keycloak hostname;
- health and metrics endpoints kept on the private management interface;
- a dedicated realm, confidential OIDC client, exact callback URI, and PKCE `S256`;
- a protected permanent Keycloak administrator with MFA;
- tested Keycloak database backup and restore;
- independent GameHub operator, delegation, JWT, BFF, cookie, provider, database, and vault keys.

The container example below is a single-node production-mode baseline, not a high-availability
deployment. Run at least two Keycloak nodes with an external PostgreSQL service and tested load
balancer behavior when the required availability target cannot tolerate one identity node.

## 1. Build and run Keycloak

Never use `start-dev` in production. Keycloak production mode uses `start`, expects a hostname and
TLS boundary, and is secure by default. Keycloak recommends building an optimized container image
so runtime startup does not repeat the augmentation step.

An example deployment-owned `Containerfile` is:

```dockerfile
FROM quay.io/keycloak/keycloak:26.6.3 AS builder

ENV KC_DB=postgres
ENV KC_HEALTH_ENABLED=true
ENV KC_METRICS_ENABLED=true

WORKDIR /opt/keycloak
RUN /opt/keycloak/bin/kc.sh build

FROM quay.io/keycloak/keycloak:26.6.3
COPY --from=builder /opt/keycloak/ /opt/keycloak/
ENTRYPOINT ["/opt/keycloak/bin/kc.sh"]
```

A minimal separate Compose deployment can use the optimized image as follows. The `${...}` values
are placeholders for deployment secret injection; do not commit their values or copy them into the
GameHub repository.

```yaml
services:
  keycloak-db:
    image: postgres:17-alpine
    restart: unless-stopped
    environment:
      POSTGRES_DB: keycloak
      POSTGRES_USER: keycloak
      POSTGRES_PASSWORD: ${KEYCLOAK_DB_PASSWORD:?required}
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U keycloak -d keycloak"]
      interval: 10s
      timeout: 5s
      retries: 5
    volumes:
      - keycloak_postgres_data:/var/lib/postgresql/data

  keycloak:
    build:
      context: .
      dockerfile: Containerfile
    restart: unless-stopped
    command: start --optimized
    depends_on:
      keycloak-db:
        condition: service_healthy
    environment:
      KC_DB: postgres
      KC_DB_URL: jdbc:postgresql://keycloak-db:5432/keycloak
      KC_DB_USERNAME: keycloak
      KC_DB_PASSWORD: ${KEYCLOAK_DB_PASSWORD:?required}
      KC_HOSTNAME: https://auth.example.com
      KC_HTTP_ENABLED: "true"
      KC_PROXY_HEADERS: xforwarded
      KC_PROXY_TRUSTED_ADDRESSES: ${KEYCLOAK_TRUSTED_PROXY_CIDR:?required}
      KC_HEALTH_ENABLED: "true"
      KC_METRICS_ENABLED: "true"
      KC_BOOTSTRAP_ADMIN_USERNAME: ${KEYCLOAK_BOOTSTRAP_ADMIN_USERNAME:?required}
      KC_BOOTSTRAP_ADMIN_PASSWORD: ${KEYCLOAK_BOOTSTRAP_ADMIN_PASSWORD:?required}
    ports:
      - "127.0.0.1:8081:8080"
      - "127.0.0.1:9001:9000"

volumes:
  keycloak_postgres_data:
```

The loopback port mappings assume the reverse proxy and private health monitor run on the Docker
host. When the proxy is another container, attach both to a private network and expose no Keycloak
host port; keep the management interface reachable only from the monitoring network.

The Compose fragment illustrates wiring, not the strongest possible secret delivery. Prefer the
deployment platform's native secret integration or a Keycloak configuration keystore over
long-lived plaintext environment variables when the platform supports it.

This example assumes edge TLS termination at a reverse proxy. `KC_HTTP_ENABLED=true` permits the
private proxy-to-Keycloak HTTP hop; it does not authorize public plaintext HTTP. The proxy must:

- expose only `https://auth.example.com` publicly;
- forward to the private Keycloak HTTP port;
- overwrite `X-Forwarded-For`, `X-Forwarded-Host`, `X-Forwarded-Port`, and
  `X-Forwarded-Proto` rather than trusting values supplied by the browser;
- send `X-Forwarded-Proto: https` and the exact public host;
- be the only network source accepted by Keycloak's application port;
- never expose Keycloak's port `9000` publicly.

Use a publicly trusted certificate or install the private certificate authority into both the
Frontend and Core container trust stores. Never disable certificate validation to make OIDC
discovery or JWKS retrieval work.

Consider a separate `KC_HOSTNAME_ADMIN` plus network access controls for the administration
console. A separate admin hostname does not itself enforce access control; the proxy/firewall must
restrict it.

If TLS passes through directly to Keycloak, configure Keycloak certificates and do not combine TLS
passthrough with `KC_HTTP_ENABLED=true` or proxy-header parsing.

Keycloak exposes private readiness at `/health/ready` and metrics at `/metrics` on the management
port when the corresponding options are enabled. Route user traffic only after readiness succeeds.

The bootstrap administrator is temporary. At the first start:

1. Sign in at `https://auth.example.com/admin/`.
2. Create a separate permanent administrator with the minimum required administration roles.
3. Require MFA for the permanent administrator and verify recovery access.
4. Delete the temporary bootstrap administrator.
5. Remove the bootstrap username/password from subsequent container configuration.

## 2. Create the GameHub realm

Create a realm named `gamehub`. Realm names become part of the issuer and should be treated as
stable after identities are linked.

For a closed operator-provisioned GameHub deployment:

- keep **User registration** disabled;
- configure SMTP before enabling **Forgot password** or email verification;
- require verified email if email is shown as profile metadata;
- enable brute-force detection and choose lockout thresholds appropriate to the deployment;
- require OTP or WebAuthn through the browser authentication flow;
- restrict administrative access separately from ordinary realm users;
- review token/session lifetimes, password policy, recovery, and audit-event retention.

GameHub never treats Keycloak email verification, roles, or groups as tenant authorization. Those
provider controls improve the authentication ceremony but cannot replace the explicit GameHub
issuer/subject mapping.

Verify the public discovery document after the realm exists:

```bash
curl --fail --silent \
  https://auth.example.com/realms/gamehub/.well-known/openid-configuration
```

Its `issuer` value must be exactly:

```text
https://auth.example.com/realms/gamehub
```

Scheme, host, path, case, and trailing slash must agree exactly with GameHub configuration.

## 3. Create the GameHub OIDC client

In the `gamehub` realm, create a client with these settings:

| Keycloak setting | Value |
|---|---|
| Client type | `OpenID Connect` |
| Client ID | `gamehub` or another stable deployment-specific ID |
| Client authentication | On; confidential server-side client |
| Standard flow | On |
| PKCE method | `S256` |
| ID-token signature algorithm | `RS256` |
| Implicit flow | Off |
| Direct access grants | Off |
| Service account roles | Off |
| Authorization Services | Off unless independently required and reviewed |
| Consent required | Off unless the deployment intentionally requires a consent screen |
| Valid redirect URI | `https://games.example.com/api/auth/oidc/callback` exactly |
| Web origins | No wildcard; normally unnecessary for the server-side BFF flow |

Under **Credentials**, use **Client ID and Secret** and retain
`client_secret_basic` as an allowed authentication method. GameHub's BFF authenticates the token
exchange with the client ID and secret in the HTTP Basic authorization header. Copy the generated
client secret directly into the deployment's secret manager.

Keep `profile` and `email` client scopes only when their display metadata is useful. GameHub
requires `openid`; it does not need `offline_access` because it issues and rotates its own refresh
sessions after the one-time Keycloak login.

Do not configure wildcard redirect URIs. GameHub verifies the exact callback independently, and a
broader Keycloak redirect pattern would weaken the provider boundary without adding functionality.

## 4. Configure GameHub while the gate is disabled

Inject the following values through the deployment configuration and secret system. Do not commit
them and do not enable production multi-user mode yet.

```text
GAMEHUB_MULTI_USER_ENABLED=false
GAMEHUB_FRONTEND_AUTH_ENABLED=true
GAMEHUB_AUTH_COOKIE_SECURE=true

GAMEHUB_OIDC_ENABLED=true
GAMEHUB_OIDC_PROVISIONING_MODE=Disabled
GAMEHUB_OIDC_ISSUER=https://auth.example.com/realms/gamehub
GAMEHUB_OIDC_METADATA_ADDRESS=https://auth.example.com/realms/gamehub/.well-known/openid-configuration
GAMEHUB_OIDC_CLIENT_ID=gamehub
GAMEHUB_OIDC_CLIENT_SECRET=<Keycloak client secret>
GAMEHUB_OIDC_REDIRECT_URI=https://games.example.com/api/auth/oidc/callback
GAMEHUB_OIDC_SCOPES=openid profile email
GAMEHUB_OIDC_ALLOWED_ALGORITHMS=RS256

GAMEHUB_OIDC_COOKIE_KEY=<independent Base64 encoding of 32 random bytes>
GAMEHUB_OIDC_BFF_SERVICE_KEY=<independent strong BFF-to-Core key>
GAMEHUB_JWT_SIGNING_KEY=<independent GameHub signing key of at least 32 bytes>
GAMEHUB_API_KEY=<independent strong operator API key>
GAMEHUB_INGEST_SERVICE_KEY=<independent strong Core-to-Ingest key>
GAMEHUB_CREDENTIAL_KEY_VERSION=1
GAMEHUB_CREDENTIAL_KEY_1=<independent Base64 encoding of 32 random bytes>
```

Generate every trust secret independently. Reusing the operator, delegation, GameHub JWT, or BFF
service key blocks guarded startup. Keep the Keycloak client secret, Keycloak database password,
Keycloak bootstrap/recovery credentials, GameHub keys, and vault key ring in the external secret
inventory and backup policy.

The reverse proxy in front of GameHub must preserve the external HTTPS scheme and host. Browsers
will reject the required Secure cookies if they reach GameHub over plaintext HTTP.

Start Keycloak first, then Ingest, Core, and Frontend. Keep the gate false while validating either
provisioning model.

## 5A. Option 1: operator provisioning and explicit first-link

In this operator option, GameHub does not create accounts from Keycloak users. Bootstrap the
default owner once when it does not already exist:

```http
POST /api/auth/bootstrap
X-Api-Key: <operator key>
Content-Type: application/json

{
  "email": "owner@example.com",
  "password": "a long unique bootstrap password",
  "displayName": "Owner"
}
```

Provision each additional tenant through the operator endpoint while the gate is false:

```http
POST /api/auth/tenants
X-Api-Key: <operator key>
Content-Type: application/json

{
  "tenantName": "Example Household",
  "email": "member@example.com",
  "password": "another long unique bootstrap password",
  "displayName": "Member"
}
```

For every account:

1. Open `https://games.example.com/login` while the gate remains disabled.
2. Submit the GameHub bootstrap email/password once.
3. For an unlinked provisioned account, Core issues only a ten-minute, non-refreshable
   `oidc_link_only` session.
4. The BFF immediately starts Keycloak Authorization Code Flow with PKCE.
5. Authenticate as the intended Keycloak user and complete MFA.
6. Core validates both the BFF trust channel and Keycloak ID token, then stores only the exact
   Keycloak issuer/subject mapping to the signed GameHub account/tenant.
7. GameHub clears the restricted cookie and requires a fresh Keycloak login.

Do not link an administrator merely because its email matches a GameHub account. The person
performing the flow chooses the durable Keycloak subject that will own that GameHub account.

While the gate is false, the default tenant can exercise a complete OIDC login. A provisioned
tenant remains blocked after linking until the operator deliberately activates guarded mode.

## 5B. Option 2: opt-in personal-tenant JIT

Use this option only when every Keycloak subject admitted by the dedicated realm/client is allowed
to own a separate personal GameHub tenant. Set:

```text
GAMEHUB_OIDC_PROVISIONING_MODE=PersonalTenantJit
GAMEHUB_MULTI_USER_ENABLED=false
```

Keep Keycloak self-registration disabled unless the operator has made a separate, explicit access
policy decision. Restrict access through Keycloak realm membership, the GameHub client, browser
authentication flows, MFA, and administrative policy. GameHub intentionally does not interpret
email, verified-email, name, group, organization, role, or tenant claims as a JIT allowlist.

A new Core database may contain zero accounts. With every technical prerequisite satisfied, the
operator readiness response may still be satisfied in this mode. After the operator activates
guarded mode consistently, the ordinary **Sign in with identity provider** button performs the
normal flow. For an unknown validated subject, Core atomically creates:

- one server-identified personal tenant with platform sync disabled;
- one `ExternalOidc` owner without a local password;
- one exact issuer/subject mapping;
- one GameHub refresh session; and
- one secret-free tenant activity row.

Concurrent first logins cannot create duplicate or orphan tenants. A provider email claim is not
required, equal email claims remain separate subjects, and provider claims cannot choose IDs or an
existing tenant. This option is not anonymous signup, shared tenancy, invitations, roles, or a
claim of general SaaS readiness.

## 6. Check readiness and activate

Call the operator-only readiness endpoint without exposing the API key to browser JavaScript:

```http
GET /api/auth/multi-user/status
X-Api-Key: <operator key>
Cache-Control: no-store
```

Before activation, expect:

```json
{
  "oidcEnabled": true,
  "oidcProvisioningMode": "disabled",
  "readinessSatisfied": true,
  "multiUserEnabled": false,
  "multiUserActive": false,
  "localPasswordMode": "compatibility_login",
  "blockers": []
}
```

Readiness proves local configuration, explicit identity links, Core-to-Ingest trust, mode
agreement, and vault-key availability. It does not prove current Keycloak uptime, MFA policy,
backup quality, or operational ownership.

Before activation:

- test the default-tenant Keycloak login, refresh, logout, and GameHub revocation paths;
- test wrong issuer, wrong callback, disabled Keycloak user, and Keycloak outage behavior;
- configure at least one supported vault credential for every tenant that should synchronize;
- exercise tenant-scoped library, backup, restore, credential, sync, and deletion operations in
  staging;
- back up and restore the Keycloak, Core, and Ingest databases plus every external key.

Then set `GAMEHUB_MULTI_USER_ENABLED=true` in Frontend, Ingest, and Core as one controlled change.
Restart Ingest first so its readiness endpoint is available, then Core, then Frontend. Confirm:

```json
{
  "oidcEnabled": true,
  "oidcProvisioningMode": "disabled",
  "readinessSatisfied": true,
  "multiUserEnabled": true,
  "multiUserActive": true,
  "localPasswordMode": "oidc_link_only",
  "blockers": []
}
```

Use `personal_tenant_jit` instead of `disabled` in those responses when Option 2 is selected.
Finally, test each linked or JIT-created account's login, access-token expiry, GameHub refresh
rotation, logout, account-wide revocation, external reauthenticated deletion, and tenant isolation.
External accounts have no local password; local passwords cannot create normal active-mode
sessions.

## 7. Backup, restore, rotation, and upgrades

The Keycloak PostgreSQL database contains realm configuration, user identifiers, credentials,
client configuration, and signing-key state. A realm export is useful for reviewing configuration
but is not a substitute for a tested database backup.

Preserve the Keycloak database when recovering an existing GameHub deployment. Recreating users in
a new realm can assign different Keycloak subjects and leave the existing explicit GameHub links
unusable even when the same email addresses are recreated.

A complete recovery set includes:

- Keycloak PostgreSQL backup;
- Core database backup;
- Ingest database backup;
- Keycloak database credential and bootstrap/recovery procedure;
- Keycloak client secret;
- GameHub operator, delegation, JWT, BFF, correlation-cookie, and vault keys, including old vault
  versions still referenced by ciphertext;
- reverse-proxy certificates and configuration.

Test restore in an isolated environment with `GAMEHUB_MULTI_USER_ENABLED=false`. Restore Keycloak
and verify its exact issuer and JWKS first, then Ingest, Core, and Frontend. Recheck every explicit
identity mapping before enabling admission.

For Keycloak upgrades:

1. Read the release and migration notes for the target patch.
2. Back up the Keycloak database and external secrets.
3. Test the upgrade against a restored copy.
4. Exercise discovery, JWKS, login, MFA, callback, and GameHub refresh behavior.
5. Roll out using the deployment's availability procedure.

Keycloak signing-key rotation is expected to publish new JWKS. Core performs one bounded metadata
refresh after an unknown signing key; test the rotation rather than assuming it. Coordinate
Keycloak client-secret rotation with the BFF secret update and restart, using Keycloak's client
secret rotation support when an overlap window is required.

## 8. Outage, disable, and identity lifecycle behavior

During a Keycloak discovery, JWKS, token, or database outage:

- new GameHub login and linking fail closed;
- no email, password, or claim fallback activates a provisioned tenant;
- already-issued GameHub access and refresh sessions continue under their local expiry,
  live-account, replay, and revocation checks;
- readiness can remain satisfied because it validates local configuration/data invariants rather
  than live provider availability.

Disabling or deleting a Keycloak user prevents future provider login but does not immediately
revoke GameHub sessions already issued to that user. GameHub does not currently consume Keycloak
back-channel logout or provider lifecycle events. For immediate termination, also call GameHub's
account-wide revocation or account deletion path.

GameHub logout revokes the local refresh session and clears GameHub cookies; it does not terminate
the Keycloak browser SSO session. A later login can therefore authenticate silently at Keycloak.
Use Keycloak's administrative session controls when the provider session must also end.

Deleting an `ExternalOidc` GameHub account is different: the BFF starts a dedicated transaction
with `prompt=login` and `max_age=0`. Core requires the exact linked subject and a recent signed
`auth_time`, then issues only a short-lived deletion-purpose token for the existing idempotent
deletion route. If cross-service cleanup remains pending, use the documented deletion-retry intent
and complete another fresh Keycloak authentication. A completed deletion tombstones the subject,
so the same identity cannot silently create a replacement tenant through JIT.

To stop new provisioned-tenant admission, set `GAMEHUB_MULTI_USER_ENABLED=false` consistently and
restart all GameHub components. This preserves tenants, links, sessions, Keycloak state,
credentials, raw records, and libraries. Existing GameHub sessions continue until normal expiry or
explicit revocation; the switch is not a global kill switch.

## Troubleshooting matrix

| Symptom | Likely cause | Safe check or action |
|---|---|---|
| Keycloak returns an invalid redirect error | Callback differs by scheme, host, port, path, case, or slash | Compare the exact client redirect URI with `GAMEHUB_OIDC_REDIRECT_URI` |
| Keycloak or Core reports issuer mismatch | Public hostname/proxy headers are wrong, or issuer has a slash mismatch | Compare discovery `issuer` exactly with `GAMEHUB_OIDC_ISSUER` |
| Keycloak returns `invalid_client` | Wrong client ID/secret or Basic authentication disallowed | Verify confidential client credentials and allow `client_secret_basic` |
| Keycloak returns 403 behind the proxy | Proxy headers are not parsed or origin is wrong | Verify `KC_PROXY_HEADERS`, trusted proxy addresses, and overwritten forwarded headers |
| GameHub OIDC route returns 503 | Incomplete OIDC, Secure-cookie, frontend-auth, or BFF configuration | Check server-side configuration and safe startup blocker labels |
| OIDC login is valid but provisioned tenant is rejected | Guarded mode is still disabled | Complete linking/readiness, then enable all GameHub components together |
| Readiness reports `active_account_missing_oidc_link` | At least one live account lacks this realm issuer | Complete the first-link flow for every live account |
| Readiness reports `ingest_credential_vault_not_ready` | No valid active vault encryption key | Restore/configure the external versioned vault key ring |
| Readiness reports `ingest_unavailable_or_trust_mismatch` | Ingest outage or delegation-key mismatch | Restore Ingest and verify the same independent service key on both sides |
| Existing GameHub session survives Keycloak logout/disable | Provider back-channel revocation is not implemented | Revoke all GameHub sessions for the account when immediate termination is required |

Never log or paste Keycloak client secrets, passwords, authorization codes, ID tokens, GameHub
access/refresh tokens, BFF/cookie keys, vault keys, or decrypted platform credentials while
troubleshooting.

## Official Keycloak references

- [Keycloak 26.6.3 release](https://www.keycloak.org/2026/06/keycloak-2663-released)
- [Running Keycloak in a container](https://www.keycloak.org/server/containers)
- [Configuring Keycloak for production](https://www.keycloak.org/server/configuration-production)
- [Configuring a PostgreSQL database](https://www.keycloak.org/server/db)
- [Configuring the hostname](https://www.keycloak.org/server/hostname)
- [Configuring a reverse proxy](https://www.keycloak.org/server/reverseproxy)
- [Management, health, and metrics interface](https://www.keycloak.org/server/management-interface)
- [Keycloak Server Administration Guide](https://www.keycloak.org/docs/latest/server_admin/)
- [Keycloak upgrading guide](https://www.keycloak.org/docs/26.6.3/upgrading/)
