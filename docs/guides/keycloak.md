# Use Keycloak for sign-in

This guide connects an existing production Keycloak server to GameHub. Keycloak is a separate
service with its own database, backups, HTTPS address, and administrator access. If you do not
have it installed, follow the [Keycloak server guides](https://www.keycloak.org/guides#server)
first. Keep its management interface private and use production startup, not `start-dev`.

The examples use `https://auth.example.com` for Keycloak and `https://games.example.com` for
GameHub. Replace both consistently with your real addresses.

## 1. Prepare the realm

1. Create a dedicated realm named `gamehub`.
2. Keep user registration disabled for a closed installation.
3. Configure MFA, password policy, and administrative access.
4. Configure SMTP before relying on email verification or password recovery.
5. Create only the intended realm users, or apply an equivalent restricted client admission policy.

Check this public discovery document:

```text
https://auth.example.com/realms/gamehub/.well-known/openid-configuration
```

Its `issuer` must exactly match `https://auth.example.com/realms/gamehub`.
Scheme, hostname, path, and trailing slash must agree with GameHub's setting.
Both the browser and GameHub services must reach the required provider HTTPS endpoints.

## 2. Create the client

Create an OpenID Connect client in that realm:

| Setting | Value |
| --- | --- |
| Client ID | `gamehub` |
| Client authentication | On |
| Standard flow | On |
| PKCE method | `S256` |
| ID-token signature algorithm | `RS256` |
| Implicit flow | Off |
| Direct access grants | Off |
| Service account roles | Off |
| Valid redirect URI | `https://games.example.com/api/auth/oidc/callback` |
| Valid post-logout redirect URI | `https://games.example.com/login` |
| Front channel logout | Off |
| Backchannel logout URL | `https://games.example.com/api/auth/oidc/backchannel-logout` |
| Backchannel logout session required | On |

Do not use wildcard redirect URIs. Under **Credentials**, use **Client ID and Secret** and allow
`client_secret_basic`. Store the generated client secret in your deployment secret system.
The minimal requested scope is `openid`; GameHub does not need `offline_access`.

The back-channel URL is reached by Keycloak's server. Verify its HTTPS connectivity separately
from browser login. Existing clients need this setting added explicitly; an application update
does not change the provider client for you.

## 3. Configure GameHub

Keep the [installation keys](installation.md#3-supply-the-local-configuration) and database settings.
Supply these additional settings through your deployment configuration:

```text
GAMEHUB_PUBLIC_ORIGIN=https://games.example.com
GAMEHUB_FRONTEND_AUTH_ENABLED=true
GAMEHUB_AUTH_COOKIE_SECURE=true
GAMEHUB_MULTI_USER_ENABLED=false
GAMEHUB_OIDC_ENABLED=true
GAMEHUB_OIDC_PROVISIONING_MODE=Disabled
GAMEHUB_OIDC_ISSUER=https://auth.example.com/realms/gamehub
GAMEHUB_OIDC_METADATA_ADDRESS=https://auth.example.com/realms/gamehub/.well-known/openid-configuration
GAMEHUB_OIDC_CLIENT_ID=gamehub
GAMEHUB_OIDC_REDIRECT_URI=https://games.example.com/api/auth/oidc/callback
GAMEHUB_OIDC_SCOPES=openid
GAMEHUB_OIDC_ALLOWED_ALGORITHMS=RS256
```

Also supply `GAMEHUB_OIDC_CLIENT_SECRET`, an independent
`GAMEHUB_OIDC_COOKIE_KEY` generated with `openssl rand -base64 32`, and an independent
`GAMEHUB_OIDC_BFF_SERVICE_KEY` generated with `openssl rand -hex 32`.
Do not reuse the operator, signing, delegation, or vault keys.

Recreate the application with the new configuration. Keep guarded admission disabled while
checking the provider connection and linking existing accounts.

## 4. Choose account provisioning

For existing accounts, keep `GAMEHUB_OIDC_PROVISIONING_MODE=Disabled` and explicitly link each
live account to the provider before activation. Use [Authentication setup](authentication.md)
for the first-link and readiness steps.

For a fresh installation where each intended Keycloak user should receive a personal collection,
set `GAMEHUB_OIDC_PROVISIONING_MODE=PersonalTenantJit`. A fresh database can remain empty;
do not bootstrap an unnecessary password account. Keep the guarded switch false until readiness
passes. This mode admits every validated identity allowed by the configured realm/client policy.
Keycloak organization/group/email claims do not narrow that policy inside GameHub.

## 5. Verify and activate

Follow the [guarded activation checklist](authentication.md#check-and-activate-guarded-admission).
Confirm no readiness blockers, then activate the same switch across all components.

After activation, use **Sign in with identity provider**. Test login, logout, another active
session being revoked, and a provider-side logout propagated to GameHub. For personal-account
provisioning, verify that a new intended user receives an empty personal collection.

If readiness reports an account missing its issuer link, keep admission disabled and complete
linking. If discovery fails, check DNS, HTTPS trust, reachability, and the exact issuer.
A successful identity-provider login does not bypass GameHub account policy.

## Maintain the installation

Back up Keycloak and its database separately. Preserve the realm/client identity and GameHub's
keys during recovery. Changing an issuer can prevent existing accounts from linking to their
collections. Test upgrades and recovery before production use.

For additional upstream identity providers, configure and verify them in Keycloak first.
GameHub still trusts the configured Keycloak issuer and its subject identifiers.
See [Login troubleshooting](troubleshooting.md#login-fails) and [installation recovery](disaster-recovery.md).
