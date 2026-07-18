# Self-Hosted Keycloak OIDC Setup

This guide describes a production-oriented way to run a self-hosted Keycloak instance and use it
as GameHub's single OpenID Connect provider. It complements the provider-neutral configuration in
[GameHub Authentication](authentication.md) and the activation procedure in
[Guarded Multi-User Operations](guarded-multi-user-enablement.md). It also provides complete
broker setup procedures for internal Google Workspace and Microsoft Entra ID login.

The examples were validated against Keycloak 26.7.0, the current stable release on 2026-07-17.
Keycloak published 26.7.0 on 2026-07-09. Review the official release and upgrade notes before every
deployment rather than silently following an old image tag.

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
FROM quay.io/keycloak/keycloak:26.7.0 AS builder

ENV KC_DB=postgres
ENV KC_HEALTH_ENABLED=true
ENV KC_METRICS_ENABLED=true

WORKDIR /opt/keycloak
RUN /opt/keycloak/bin/kc.sh build

FROM quay.io/keycloak/keycloak:26.7.0
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

### Roadmap #18 preparation: native Organizations and invitations

Keycloak Organizations can be enabled for this realm under **Realm settings → Organizations**.
This enables Keycloak's native organization membership and invitation lifecycle; it does not add
an invitation mode, invitation endpoint, registration UI, or tenant membership model to GameHub.
GameHub does not currently implement an `InvitationOnly` provisioning mode.

For a future closed personal-tenant admission policy, keep global **User registration** disabled.
Use native organization invitations instead of enabling the realm-wide public registration link:

- an invitation for an existing realm user asks that user to confirm joining the organization;
- an invitation for an unknown email enters Keycloak's registration flow and fixes the registration
  email to the invited address;
- the invitation-scoped new-user flow in Keycloak 26.7.0 was locally validated to complete while
  the realm-wide **User registration** switch remained disabled; and
- SMTP must be configured for real delivery. Do not enable global registration as an invitation
  workaround if delivery or another external dependency is unavailable.

Treat this as provider-side admission only. If Roadmap #18 later adds an explicit GameHub policy,
validated membership in one configured Keycloak organization may decide only whether a previously
unknown exact `(issuer, subject)` is admitted to `PersonalTenantJit`. It must never supply, select,
name, reuse, or switch a GameHub tenant. Core must still generate a new personal tenant and owner,
and every returning login must still resolve solely through the stored case-sensitive
`(issuer, subject)` mapping. Email, organization alias/ID, organization attributes, roles, groups,
domains, and all other provider claims remain non-authoritative for tenant selection.

#### Invitation administration and lifecycle

Keycloak 26.7.0 adds the dedicated `realm-management` client role
`manage-organizations`. Grant it to a narrowly scoped administrator or service account that must
create organizations, manage their membership, and send or manage invitations. The broader
`manage-realm` role continues to imply organization management, but should not be granted merely
for invitation automation. `view-organizations` and `query-organizations` provide narrower read
and search capabilities. Operations that expose realm user records or enumerate member details
also require suitable `view-users` access or equivalent Fine-Grained Admin Permissions.

The relevant Admin REST operations are:

```text
POST   /admin/realms/{realm}/organizations/{organization-id}/members/invite-user
POST   /admin/realms/{realm}/organizations/{organization-id}/members/invite-existing-user
GET    /admin/realms/{realm}/organizations/{organization-id}/invitations
GET    /admin/realms/{realm}/organizations/{organization-id}/invitations/{invitation-id}
POST   /admin/realms/{realm}/organizations/{organization-id}/invitations/{invitation-id}/resend
DELETE /admin/realms/{realm}/organizations/{organization-id}/invitations/{invitation-id}
```

The `invite-user` operation sends either a join link for an existing user or a registration link
for an unknown email; `invite-existing-user` accepts an existing realm user ID. Invitation records
are `PENDING` or `EXPIRED`. Resending a pending invitation sends a new action token with a fresh
expiration time. Deleting the invitation is the revocation operation and permanently removes its
record. A successfully accepted invitation is also removed automatically, so the accepted action
link is not a reusable durable membership credential.

Admin REST invitation representations can contain `inviteLink`. Treat that field like a short-lived
bearer credential: do not log it, include it in metrics or traces, return it through GameHub APIs,
store it in GameHub, paste it into tickets, or expose it to browser JavaScript. Restrict the Admin
REST token and role, keep responses out of general-purpose logs, and let Keycloak deliver the link
through its configured email path.

#### Organization OIDC scope and claim boundary

Keycloak supplies a built-in optional `organization` client scope with an `Organization Membership`
mapper. It can be requested as `organization`, `organization:<alias>`, or `organization:*`.
The resulting `organization` claim is keyed by organization alias; organization IDs and attributes
are omitted by default unless enabled on the mapper. The unqualified scope selects the sole
membership or prompts a member of multiple organizations to choose. An alias-qualified scope is
rejected when the alias does not exist or the user is not a member; the wildcard maps every
membership. Do not mix these scope formats in one request.

Do not add an organization scope to the GameHub client merely by enabling Organizations. Current
GameHub deliberately projects only the validated issuer, subject, optional display email, and
fresh-authentication time; it does not consume organization claims. Any future Roadmap #18 change
must introduce a separately reviewed admission check while preserving the exact `(issuer, subject)`
identity authority and every ADR-011/012/013 tenant boundary.

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
| Valid Post Logout Redirect URIs | `https://<gamehub-host>/login` exactly |
| Web origins | No wildcard; normally unnecessary for the server-side BFF flow |
| Requested scope baseline | `openid` |
| Organization scope | Do not request until a GameHub admission policy is implemented and reviewed |
| Front channel logout | Off |
| Backchannel logout URL | `https://games.example.com/api/auth/oidc/backchannel-logout` exactly |
| Backchannel logout session required | Off; GameHub deliberately performs account-wide `sub` logout |

Under **Credentials**, use **Client ID and Secret** and retain
`client_secret_basic` as an allowed authentication method. GameHub's BFF authenticates the token
exchange with the client ID and secret in the HTTP Basic authorization header. Copy the generated
client secret directly into the deployment's secret manager.

Use `openid` as the minimum and sufficient GameHub scope baseline. Add `profile` and/or `email` only
when their optional display metadata is useful. Core does not require either scope, does not use an
email claim for lookup or authorization, and permits an external account without provider email.
The current Compose/BFF fallback is `openid profile email` for compatibility, so set
`GAMEHUB_OIDC_SCOPES=openid` explicitly when using the minimal baseline. GameHub does not need
`offline_access` because it issues and rotates its own refresh sessions after the one-time Keycloak
login.

The Backchannel Logout URL is a provider-to-BFF endpoint, not a browser callback and not a Core
container URL. Keycloak posts a signed Logout Token there when a relevant user, Account Console,
or administrator logout is propagated. GameHub validates that token independently in Core, revokes
every GameHub refresh session for the exact linked `(issuer, subject)`, and advances the account's
session epoch so already issued GameHub access JWTs fail on their next request. Repeated messages,
unknown identities, and identities that are already logged out return the same successful provider
response and reveal no account state.

GameHub does not map Keycloak `sid` values to individual browsers. Keep **Backchannel logout session
required** off to make the account-wide behavior explicit; even if a `sid` is present, it is never
used as an account or tenant selector. Logout Tokens must be signed compact JWTs. Encrypted Logout
Tokens and `sid`-only messages are unsupported. See
[ADR-015](adr/015-oidc-back-channel-logout.md) for the complete validation and replay boundary.

Do not configure wildcard redirect URIs. GameHub verifies the exact callback independently, and a
broader Keycloak redirect pattern would weaken the provider boundary without adding functionality.

After saving the client, validate Back-Channel Logout in staging:

1. Sign in to GameHub from two browser sessions with the same Keycloak account.
2. Trigger a Keycloak Account Console or administrator logout that terminates the user session.
3. Verify that both browser sessions lose GameHub API access without waiting for the normal access
   JWT lifetime and cannot rotate their refresh sessions.
4. Verify a secret-free `OidcBackChannelLogout` entry exists only in that tenant's activity feed.
5. Verify a different tenant remains signed in and operational.

If Keycloak reports a non-success response, check HTTPS reachability from Keycloak to the public
GameHub BFF, exact issuer/client configuration, system clock skew, discovery/JWKS availability, and
the configured asymmetric signing algorithm. Never log or paste the Logout Token while diagnosing.

## Optional upstream login with Google Workspace and Microsoft Entra ID

GameHub continues to trust exactly one issuer when Keycloak brokers an upstream provider:

```text
GameHub -> exact Keycloak realm issuer -> Google Workspace or Microsoft Entra ID
```

Register Keycloak's broker callback with Google or Microsoft, not GameHub's
`/api/auth/oidc/callback`. GameHub receives only a Keycloak ID token and continues to identify the
person by the exact Keycloak `(issuer, subject)`. Do not store upstream provider tokens merely to
support login or logout: leave **Store tokens** and **Stored tokens readable** disabled on both
Keycloak identity providers.

### Private Keycloak connectivity

Google and Microsoft do not need direct inbound network access to Keycloak for the authorization
callback. They redirect the top-level browser to Keycloak. A Keycloak deployment that is not
internet-reachable can therefore broker both providers when all of the following are true:

- Keycloak has a stable HTTPS hostname under a public DNS suffix, such as `auth.example.com`, and
  presents a certificate trusted by every participating browser;
- internal or split-horizon DNS resolves that hostname to the private Keycloak proxy;
- the browser remains on the LAN or VPN when the provider returns it to Keycloak; and
- Keycloak has outbound HTTPS access to the provider's authorization, token, JWKS, UserInfo, and
  Microsoft Graph endpoints.

Do not replace the configured Keycloak issuer with a temporary public hostname. Changing the realm
issuer changes GameHub's exact external-identity key. Raw private IP addresses, plaintext callback
URLs, and private-only suffixes such as `.lan` are also unsuitable provider redirect URIs.

### Choose the upstream admission policy first

Keycloak's realm-wide **User registration** switch and its identity-broker first-login behavior are
separate controls. The default First Broker Login flow can create a Keycloak user for a previously
unknown upstream identity even while realm self-registration is disabled.

Choose one admission model before enabling the provider:

1. **Existing users only:** have each existing Keycloak user sign in to
   `https://auth.example.com/realms/gamehub/account/` and link the upstream provider from the
   account-security or linked-accounts page. Alternatively, duplicate the built-in
   `first broker login` flow, disable **Create User If Unique** and
   **Confirm Link Existing Account**, retain verified reauthentication of an existing account,
   and select that copy as the provider's **First Login Flow**.
2. **New personal GameHub tenants:** keep Keycloak's normal first-broker user creation only when
   every upstream identity admitted by the provider policy may receive its own GameHub tenant.
   GameHub still requires active guarded multi-user mode and the explicit
   `PersonalTenantJit` provisioning mode described in Option 2 below.

Do not automatically link accounts merely because upstream and Keycloak email addresses match.
Pre-link through the authenticated Keycloak account console or require reauthentication of the
existing account. Email similarity never changes GameHub's issuer/subject mapping.

### Google Workspace setup

Use a Google Cloud project owned by the same Google Workspace or Cloud Identity organization as
the intended users:

1. In the GameHub realm, select **Identity Providers -> Add provider -> Google** and copy the exact
   **Redirect URI** displayed by Keycloak. It normally resembles:

   ```text
   https://auth.example.com/realms/gamehub/broker/google/endpoint
   ```

2. In Google Cloud, configure **Google Auth Platform -> Audience** as **Internal** for an
   organization-only deployment. Confirm under **IAM & Admin -> Settings** that the Cloud project
   belongs to that organization. If an external app is deliberately kept in **Testing**, add every
   intended account as a test user instead.
3. Configure the OAuth consent screen with the GameHub name, support contact, and only the basic
   `openid`, `profile`, and `email` scopes.
4. Under **Google Auth Platform -> Clients**, create a **Web application** OAuth client and add the
   exact Keycloak broker callback as an **Authorized redirect URI**. Scheme, host, port, path,
   case, and trailing slash must match exactly.
5. Copy that client's ID and secret into the Keycloak Google provider. Configure:

   ```text
   Default scopes: openid profile email
   Prompt: select_account
   Sync mode: Import
   Show in account console: Always
   Store tokens: Off
   Stored tokens readable: Off
   ```

6. Leave **Trust email** disabled unless the deployment has separately reviewed Google's verified
   email assertion and the Keycloak profile policy. **Hosted Domain** is optional; when used, enter
   the primary Workspace domain. Audience and Workspace policy remain the actual admission
   controls. A mismatched alias or secondary domain can make account selection confusing, so clear
   this field while diagnosing domain-selection problems.
7. If Workspace API access is restricted, open **Google Admin Console -> Security -> Access and
   data control -> API controls -> Manage App Access**, configure the exact OAuth client ID as
   **Trusted**, and apply it to the top-level organization or every intended organizational unit.
   A child organizational-unit policy can override the parent setting.
8. Save and enable the Keycloak provider, then test one pre-linked user before widening admission.

`prompt=select_account` retains the Google browser session but shows the account chooser on every
new Keycloak login. It is preferable to `prompt=consent`, which repeatedly asks for authorization
without solving account selection.

When one Google project owner can sign in but another directory user cannot, check the failing
Google error code before changing Keycloak:

- `org_internal`: the user is outside the Cloud project's owning organization;
- `access_denied`: the app is in testing without that test user, the account type is ineligible,
  or a Workspace policy denied access;
- `admin_policy_enforced`: the user's organizational-unit policy blocks the OAuth client; and
- `redirect_uri_mismatch`: the actual request callback differs from the URI registered on the
  exact OAuth client used by Keycloak.

Also verify that the account can sign in directly at `https://accounts.google.com/`, that it is an
active managed user rather than an alias or directory contact, and that the trusted OAuth client ID
exactly matches Keycloak. Google Workspace **User log events** and **OAuth log events** provide the
provider-side failure reason.

### Microsoft Entra ID setup

For a private organization deployment, prefer a single-tenant Entra application:

1. In the GameHub realm, select **Identity Providers -> Add provider -> Microsoft** and copy the
   exact **Redirect URI** displayed by Keycloak. It normally resembles:

   ```text
   https://auth.example.com/realms/gamehub/broker/microsoft/endpoint
   ```

2. In **Microsoft Entra ID -> App registrations**, create an application such as
   `GameHub Keycloak Broker`. Select **Accounts in this organizational directory only** unless the
   deployment deliberately admits other organizations or personal Microsoft accounts.
3. Under **Authentication**, add the Keycloak broker callback as a **Web** redirect URI. Do not use
   the SPA platform and do not enable implicit or hybrid grants.
4. Under **Certificates & secrets**, create a client secret with a controlled lifetime. Copy the
   secret **Value**, not its secret ID, and record its rotation date.
5. Copy the **Application (client) ID**, **Directory (tenant) ID**, and secret value into the
   Keycloak Microsoft provider. Set the provider tenant to the exact directory tenant ID for a
   single-tenant deployment.
6. Under **API permissions**, add only the Microsoft Graph **Delegated** permission `User.Read` and
   grant tenant-wide admin consent when the organization's consent policy requires it. Do not use
   the substantially broader `User.Read.All` application permission.
7. Configure the built-in Keycloak Microsoft provider as follows:

   ```text
   Default scopes: User.Read
   Prompt: select_account
   Sync mode: Import
   Show in account console: Always
   Store tokens: Off
   Stored tokens readable: Off
   ```

   Additional `openid profile email` scopes are optional, but they do not replace `User.Read` for
   this provider. Keycloak's built-in Microsoft provider uses the delegated access token to call
   `https://graph.microsoft.com/v1.0/me/` and extract the user profile.
8. Leave **Trust email** disabled unless it has been separately reviewed, select the intended
   **First Login Flow**, save the provider, and test a pre-linked account.

`prompt=select_account` displays Microsoft's account chooser while preserving the broader
Microsoft 365 browser session. Use `prompt=login` only when forced credential reauthentication is
required; it is more disruptive and is not necessary for ordinary account switching.

If Keycloak reports `Could not obtain user profile from Microsoft Graph`, inspect the Keycloak
server log and the Entra sign-in record. The error
`Authorization_RequestDenied: Insufficient privileges to complete the operation` means the access
token lacks usable delegated `User.Read`: verify both the Entra API permission/admin consent and
Keycloak's requested default scope. Other common codes include `AADSTS7000215` for a wrong client
secret, `AADSTS7000222` for an expired secret, `AADSTS50011` for a redirect mismatch, and
`AADSTS50020` for an account outside the selected tenant or supported account types.

Use **Entra ID -> Monitoring & health -> Sign-in logs** to capture the error code, failure reason,
conditional-access result, and correlation ID. A successful Entra sign-in followed by a Keycloak
error normally points to Keycloak's token exchange, Graph profile request, or First Broker Login
rather than the user's primary Microsoft authentication.

### Test the complete broker flow

Introduce Google and Microsoft one at a time. For each provider:

1. Link or admit one controlled identity according to the chosen First Login Flow.
2. Confirm that the browser can return to the private Keycloak hostname over LAN or VPN.
3. Confirm that the same upstream identity resolves to the same Keycloak user and GameHub tenant
   on repeated login.
4. Confirm that an unapproved account is rejected at the provider or Keycloak admission boundary.
5. If `PersonalTenantJit` is active, confirm that one newly admitted identity receives exactly one
   tenant and that a repeated or concurrent login creates no duplicate.
6. Sign out through GameHub and verify local GameHub session revocation plus Keycloak browser
   logout.
7. Sign in again and verify that `select_account` offers account switching.

GameHub logout terminates the current GameHub and Keycloak browser sessions, not the global Google
or Microsoft browser session. Logging out of those upstream sessions would also affect unrelated
services such as Gmail or Microsoft 365. The provider-specific `select_account` prompt supplies
explicit account choice without broad upstream logout.

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
GAMEHUB_OIDC_SCOPES=openid
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

Enabling a Keycloak Organization does not change this current behavior. Roadmap #18 may later add
an explicit invitation-only admission check, but organization membership would be allowed to answer
only whether a validated unknown subject may enter this personal-tenant JIT path. It would never
select a GameHub tenant. Until that policy is implemented and tested, GameHub ignores organization
claims and every valid subject admitted by the configured realm/client remains subject to the
existing ADR-013 policy.

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

1. Read the release and migration notes for the target release.
2. Back up the Keycloak database and external secrets.
3. Test the upgrade against a restored copy.
4. Exercise discovery, JWKS, login, MFA, callback, and GameHub refresh behavior.
5. Roll out using the deployment's availability procedure.

Moving from 26.6.x to 26.7.0 is a minor-version upgrade, not a same-stream patch update. Keycloak's
rolling-update support introduced in 26.6 applies to supported patch updates within one minor
stream; plan a coordinated shutdown for this minor upgrade unless the official rolling-update
guide explicitly validates the deployment topology. In addition to the general migration review,
26.7.0 changes organization member queries to return brief user representations by default and
makes the invitation `email`, `firstName`, and `lastName` filters case-insensitive exact matches
(`search` retains substring matching). Re-test delegated organization roles, invitation automation,
custom themes, database migration, and client-secret storage limits against a restored copy before
production rollout.

Keycloak signing-key rotation is expected to publish new JWKS. Core performs one bounded metadata
refresh after an unknown signing key; test the rotation rather than assuming it. Coordinate
Keycloak client-secret rotation with the BFF secret update and restart, using Keycloak's client
secret rotation support when an overlap window is required.

## 8. Outage, disable, and identity lifecycle behavior

During a Keycloak discovery, JWKS, token, or database outage:

- new GameHub login and linking fail closed;
- no email, password, or claim fallback activates a provisioned tenant;
- already-issued GameHub access and refresh sessions continue under their local expiry,
  live-account, session-epoch, replay, and revocation checks unless a valid Back-Channel Logout is
  delivered;
- readiness can remain satisfied because it validates local configuration/data invariants rather
  than live provider availability.

Disabling or deleting a Keycloak user prevents future provider login. It terminates existing
GameHub authorization immediately only when Keycloak also delivers the configured Back-Channel
Logout request. GameHub does not consume separate disabled-user or deletion events, so operators
must still verify session termination or use GameHub's account-wide revocation/deletion path.

GameHub implements OIDC RP-Initiated Logout for user-triggered browser sign-out. The browser submits
a same-origin GameHub POST; GameHub first attempts to revoke the local refresh session and expires
all GameHub authentication cookies. Only when that request carried a GameHub refresh-session cookie
does GameHub discover the exact configured issuer's HTTPS `end_session_endpoint` and return a 303
top-level navigation with the server-configured `client_id` and a server-derived
`post_logout_redirect_uri`. OIDC disablement, provider outage, missing metadata, or invalid logout
metadata falls back to local `/login` after the same local cleanup.

Register this exact value under the client's **Valid Post Logout Redirect URIs**:

```text
https://<gamehub-host>/login
```

The scheme, host, optional port, and `/login` path must match the origin of the exact configured
`GAMEHUB_OIDC_REDIRECT_URI`. GameHub does not retain a Keycloak ID token and therefore sends
`client_id` rather than `id_token_hint`. Keycloak may show the user a logout confirmation page as a
result; this is expected and avoids storing or exposing provider tokens solely to suppress it.

RP-Initiated browser logout is distinct from Keycloak-initiated Back-Channel Logout. The former
clears the current browser and navigates through Keycloak; the latter is a signed server-to-server
POST to the configured GameHub BFF URL. GameHub processes Back-Channel Logout account-wide and
invalidates both access and refresh paths. Account disablement, deletion, organization removal, or
other provider lifecycle changes still require a normal Keycloak logout delivery or separate
GameHub revocation because no general provider-event synchronization exists.

Deleting an `ExternalOidc` GameHub account is different: the BFF starts a dedicated transaction
with `prompt=login` and `max_age=0`. Core requires the exact linked subject and a recent signed
`auth_time`, then issues only a short-lived deletion-purpose token for the existing idempotent
deletion route. If cross-service cleanup remains pending, use the documented deletion-retry intent
and complete another fresh Keycloak authentication. A completed deletion tombstones the subject,
so the same identity cannot silently create a replacement tenant through JIT.

To stop new provisioned-tenant admission, set `GAMEHUB_MULTI_USER_ENABLED=false` consistently and
restart all GameHub components. This preserves tenants, links, sessions, Keycloak state,
credentials, raw records, and libraries. Existing GameHub sessions continue until normal expiry or
explicit local/provider revocation; the switch is not a global kill switch.

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
| Existing GameHub session survives Keycloak-initiated or administrative logout/disable | Back-channel/provider-event revocation is not implemented | Revoke all GameHub sessions for the account when immediate termination is required |
| Google returns `org_internal` | The selected account is outside the Cloud project's owning organization | Verify the project's organization and the user's managed primary account |
| Only the Google project owner can sign in | External testing, wrong trusted client ID, or an organizational-unit override blocks other users | Verify Internal audience, exact OAuth client ID, and access for the user's OU |
| Google returns `redirect_uri_mismatch` | Keycloak's broker callback differs from the URI registered on the OAuth web client | Compare the actual request and Keycloak-displayed callback exactly |
| Google or Microsoft silently reuses the previous account | The upstream browser session remains active and no account chooser was requested | Set the provider's **Prompt** to `select_account`; do not broaden logout token storage |
| Keycloak cannot obtain a Microsoft Graph user profile | The delegated access token lacks usable `User.Read` | Add delegated Graph `User.Read`, grant required consent, and request `User.Read` in Keycloak |
| Entra returns `AADSTS7000215` or `AADSTS7000222` | Client secret value is wrong or expired | Store the secret **Value**, rotate it, and update Keycloak without logging it |
| Entra returns `AADSTS50020` | Selected account is not admitted by the configured tenant/account type | Verify the provider tenant ID, app account type, and selected account |
| Invitation creation fails | SMTP, delegated role, organization state, or email policy is incomplete | Keep global registration disabled; verify Keycloak SMTP and least-privilege organization administration |
| Invitation link appears in logs or diagnostics | Admin REST response bodies or email URLs are being captured | Treat `inviteLink` as a bearer credential and remove it from logs, traces, tickets, and GameHub storage |

Never log or paste Keycloak client secrets, passwords, authorization codes, ID tokens, GameHub
access/refresh tokens, BFF/cookie keys, vault keys, or decrypted platform credentials while
troubleshooting.

## Controlled Keycloak 26.7.0 invitation validation

On 2026-07-17, a temporary local Keycloak 26.7.0 container was exercised with an ephemeral realm,
Organizations enabled, global **User registration** disabled, a synthetic `.invalid` address, a
local SMTP protocol sink, and a service account holding `manage-organizations`:

- the delegated account created the organization and invitation and used the invitation lifecycle
  endpoints without `manage-realm`;
- a previously unknown synthetic user completed the invitation registration flow while global
  **User registration** remained disabled and became an organization member;
- successful acceptance removed the invitation and replaying its original action link did not
  create another user or membership;
- deleting a second invitation invalidated its action link; and
- an invitation created with a deliberately shortened action-token lifespan became `EXPIRED` and
  its link did not create a user.

No real address, token, credential, or invitation link was printed or retained, and the temporary
container and validation scripts were removed. The SMTP sink proved only that Keycloak completed
the SMTP protocol exchange. External delivery, spam filtering, mailbox-client link handling,
public TLS/hostname/proxy behavior, clustering, production database upgrades, and a GameHub
end-to-end admission flow were not tested. No GameHub invitation-only behavior exists as a result
of this validation.

## Official Keycloak references

- [Keycloak 26.7.0 release](https://www.keycloak.org/2026/07/keycloak-2670-released)
- [Keycloak downloads](https://www.keycloak.org/downloads)
- [Running Keycloak in a container](https://www.keycloak.org/server/containers)
- [Configuring Keycloak for production](https://www.keycloak.org/server/configuration-production)
- [Configuring a PostgreSQL database](https://www.keycloak.org/server/db)
- [Configuring the hostname](https://www.keycloak.org/server/hostname)
- [Configuring a reverse proxy](https://www.keycloak.org/server/reverseproxy)
- [Management, health, and metrics interface](https://www.keycloak.org/server/management-interface)
- [Keycloak 26.7.0 Server Administration Guide](https://www.keycloak.org/docs/26.7.0/server_admin/)
- [Keycloak OpenID Connect endpoints](https://www.keycloak.org/securing-apps/oidc-layers)
- [Keycloak 26.7.0 Admin REST API](https://www.keycloak.org/docs-api/latest/rest-api/index.html)
- [Keycloak 26.7.0 upgrading guide](https://www.keycloak.org/docs/latest/upgrading/)
- [Google OpenID Connect](https://developers.google.com/identity/openid-connect/openid-connect)
- [Google OAuth 2.0 for web server applications](https://developers.google.com/identity/protocols/oauth2/web-server)
- [Google OAuth app audience](https://support.google.com/cloud/answer/15549945)
- [Google Workspace app access control](https://support.google.com/a/answer/7281227)
- [Microsoft Entra application registration](https://learn.microsoft.com/en-us/entra/identity-platform/quickstart-register-app)
- [Microsoft identity platform authorization code flow](https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-auth-code-flow)
- [Microsoft Graph authentication basics](https://learn.microsoft.com/en-us/graph/auth/auth-concepts)
- [Microsoft Entra sign-in troubleshooting](https://learn.microsoft.com/en-us/entra/identity/monitoring-health/howto-troubleshoot-sign-in-errors)
