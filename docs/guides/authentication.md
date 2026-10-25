# Set up sign-in

Choose the sign-in method before exposing a new installation to users.
Authentication setup is separate from connecting Steam, GOG, PlayStation, or Epic accounts.

## Local owner login

For a local installation, use the password account created during [installation](installation.md).
Keep `GAMEHUB_MULTI_USER_ENABLED=false`. The frontend requires login by default.

The operator API key creates the initial owner and performs permitted operator operations;
it is not the password used on the login page.
Do not disable authentication to repair a failed login.

## HTTPS and identity-provider login

For an internet-facing installation, use HTTPS and configure an OpenID Connect (OIDC) provider.
The [Keycloak guide](keycloak.md) walks through the required client settings.

You need:

- The provider's exact HTTPS issuer and client ID.
- An exact GameHub callback URL ending in `/api/auth/oidc/callback`.
- A client secret when using a confidential provider client.
- Independent signing, service, encryption, and OIDC cookie keys.
- `GAMEHUB_PUBLIC_ORIGIN` set to the browser-visible HTTPS origin.
- `GAMEHUB_FRONTEND_AUTH_ENABLED=true` and `GAMEHUB_AUTH_COOKIE_SECURE=true`.

See [Configuration](configuration.md#oidc-settings) for variable names.
Keep `GAMEHUB_MULTI_USER_ENABLED=false` while preparing the deployment.
Configuring OIDC alone does not admit additional accounts.

## Decide who may sign in

Two supported preparation paths have different consequences:

| Mode | Access |
| --- | --- |
| `Disabled` provisioning | Existing accounts must be explicitly linked; unknown provider identities cannot create accounts |
| `PersonalTenantJit` | After guarded activation, each admitted new provider identity receives a separate personal collection |

Personal-account provisioning is for a deliberately restricted provider realm/client.
Every identity admitted by that provider policy can enter this path. Email addresses, roles,
groups, and organizations are not a GameHub admission allowlist and do not select a collection.
Shared collections, invitations, and public registration are not implied.

For an existing password account, link it while guarded admission is disabled. Provisioned
accounts use the local login form as a first-link bridge; after provider confirmation, sign in
again through OIDC. Do not create another provider account assuming an equal email will link it.

## Check and activate guarded admission

This is an operator procedure, not an end-user setting.

1. Prepare HTTPS, the provider client, independent keys, and matching component configuration.
2. In explicit-link mode, link every live account to the configured issuer. In a fresh
   personal-account deployment, an empty database is permitted.
3. Test login, logout, session revocation, provider outage, and backup/recovery in staging.
4. Through Core Swagger, authorize with the operator API key and call
   **GET /api/auth/multi-user/status**.
5. Require `readinessSatisfied=true`, `multiUserEnabled=false`, `multiUserActive=false`,
   and an empty `blockers` list. Resolve every blocker before continuing.
6. Back up the databases and keys.
7. Set `GAMEHUB_MULTI_USER_ENABLED=true` consistently across the deployment.
8. Recreate Ingest first, then Core, then frontend:
   ```bash
   docker compose up -d --no-deps --force-recreate ingest
   docker compose up -d --no-deps --force-recreate core
   docker compose up -d --no-deps --force-recreate frontend
   ```
9. Check the same status endpoint for `multiUserActive=true`, then verify intended account access.

Include any custom Compose file options in these commands. A failed readiness check is a deployment
blocker; bypassing it is not a supported setup step.

## Sessions, recovery, and disabling admission

Users manage their active GameHub sessions in **Admin → Security**. Revoking another session
does not sign out the current one. Provider password recovery and MFA belong to the identity provider;
local owner mode does not supply an email-based password recovery service.

Configure the provider's Back-Channel Logout URL as
`https://games.example.com/api/auth/oidc/backchannel-logout`, replacing the hostname.
For Keycloak, use the exact settings in [Keycloak setup](keycloak.md).

To disable guarded admission, set `GAMEHUB_MULTI_USER_ENABLED=false` consistently and recreate
the services. This retains data and stops new provisioned-account logins; it does not immediately
revoke all existing sessions. Use account session controls when revocation is required.

Back up the provider separately alongside [GameHub installation backups](disaster-recovery.md).
Never repair access by changing an issuer/subject mapping based only on matching email addresses.
