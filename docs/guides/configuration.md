# Configuration reference

The table below describes the operator-facing variables wired by `docker-compose.yml`. Supply
secrets yourself through your deployment system. This handbook contains names and requirements,
never real deployment values. Changing configuration normally requires recreating the affected
containers; restart alone does not update a container's environment.

## Service and database settings

| Variable | Default / purpose |
| --- | --- |
| `CORE_DB_PROVIDER` | `Postgres`; Core supports `Postgres` or `Sqlite` |
| `INGEST_DB_PROVIDER` | `Sqlite`; Ingest database provider |
| `INGEST_DB_CONN` | `Data Source=/app/data/ingest.db` in the baseline |
| `POSTGRES_DB` | `gamehub_core` |
| `POSTGRES_USER` | `postgres` |
| `POSTGRES_PASSWORD` | Replace the development fallback before deployment |
| `IGDB_CLIENT_ID` | Twitch application client ID for IGDB metadata |
| `IGDB_CLIENT_SECRET` | Twitch application secret for IGDB metadata |

Core's Compose SQLite connection is `/app/data/core.db`. Its PostgreSQL connection is built from
the PostgreSQL variables. Frontend `CORE_URL` and `INGEST_URL`, and Core's `Services__IngestUrl`,
use internal Compose hostnames; keep those distinct from public browser URLs.

## Authentication and encrypted credentials

| Variable | Requirement / purpose |
| --- | --- |
| `GAMEHUB_API_KEY` | Compatibility/operator key; empty permits legacy auth-disabled mode |
| `GAMEHUB_TENANT_ID` | Trusted compatibility tenant; defaults to the original default tenant |
| `GAMEHUB_INGEST_SERVICE_KEY` | Independent Core-to-Ingest delegation key |
| `GAMEHUB_CREDENTIAL_KEY_VERSION` | Active vault key version; defaults to `1` |
| `GAMEHUB_CREDENTIAL_KEY_1` | Base64-encoded 32-byte AES vault key |
| `GAMEHUB_JWT_SIGNING_KEY` | At least 32 UTF-8 bytes; enables GameHub JWT issuance/validation |
| `GAMEHUB_JWT_REFRESH_TOKEN_DAYS` | `30`; supported range `1`–`90` |
| `GAMEHUB_FRONTEND_AUTH_ENABLED` | `false`; enable the frontend authentication flow |
| `GAMEHUB_AUTH_COOKIE_SECURE` | `false` locally; requires true for production HTTPS/OIDC |
| `GAMEHUB_MULTI_USER_ENABLED` | `false`; explicit guarded admission switch mirrored across components |

Vault configuration supports a versioned key ring through Ingest's `Credentials__Keys__<version>`
settings. The baseline exposes version 1; extend your operator configuration when rotating keys.
Retain old keys while ciphertext or backups still require them.

## OIDC settings

| Variable | Requirement / purpose |
| --- | --- |
| `GAMEHUB_OIDC_ENABLED` | `false`; independent from guarded admission |
| `GAMEHUB_OIDC_PROVISIONING_MODE` | `Disabled` or explicit `PersonalTenantJit` |
| `GAMEHUB_OIDC_ISSUER` | Exact trusted HTTPS issuer |
| `GAMEHUB_OIDC_CLIENT_ID` | Client ID / expected ID-token audience |
| `GAMEHUB_OIDC_CLIENT_SECRET` | Optional confidential-client secret |
| `GAMEHUB_OIDC_REDIRECT_URI` | Exact external HTTPS URL ending in `/api/auth/oidc/callback` |
| `GAMEHUB_OIDC_SCOPES` | `openid profile email`; must include `openid` |
| `GAMEHUB_OIDC_METADATA_ADDRESS` | Optional exact HTTPS discovery-document address used by Core |
| `GAMEHUB_OIDC_ALLOWED_ALGORITHMS` | `RS256`; explicit asymmetric ID-token allowlist |
| `GAMEHUB_OIDC_COOKIE_KEY` | Independent Base64-encoded 32-byte transaction-cookie key |
| `GAMEHUB_OIDC_BFF_SERVICE_KEY` | Independent BFF-to-Core OIDC key |

Read [Authentication](../authentication.md) for complete prerequisites and session behavior.
The [operator guide](../guarded-multi-user-enablement.md) defines readiness, account linking,
activation, and rollback. Do not infer an account from an email or a provider role/tenant claim.

## Sync and service-native settings

These are ASP.NET configuration keys rather than additional variables already exposed by the
baseline Compose file. Wire them through your deployment if you need to override them:

| Key | Default / behavior |
| --- | --- |
| `Sync:EnableScheduler` | `false` |
| `Sync:IntervalMinutes` | `360` |
| `Sync:ProviderFreshnessMinutes` | `30`; freshness window for provider retrieval |
| `ProductCatalog:Provider` | `None`; no external catalogue is enabled |
| `ProductCatalog:TimeoutMilliseconds` | `1500`; supported range `250`–`5000` |

ASP.NET uses double underscores for environment key separators, for example
`Sync__EnableScheduler`. Scheduler/provider state is separate from credential availability.
Do not enable catalogue integrations or tenant admission merely by copying an example setting.
