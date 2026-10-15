# API overview

This page maps the main API areas. Use the running service's Swagger/OpenAPI documentation for
the exact request/response schema of your installed version. Core exposes Swagger at `/swagger`;
Ingest exposes its UI at `/` in the local baseline.

## Authentication boundaries

Browser requests use the same-origin Next.js BFF at `/api/core/*`. GameHub access and refresh
tokens stay in HttpOnly cookies. Core resolves account and tenant ownership from trusted
authentication. In active guarded mode, ordinary Core endpoints require GameHub JWTs and ordinary
Ingest operations require Core's internal tenant delegation.

Compatibility/operator API keys are accepted only where the installed mode and endpoint policy
allow them. An arbitrary `X-GameHub-Tenant-Id` header is never tenant authority for a normal client.
Internal credential, lifecycle, and readiness routes must not be exposed as public user APIs.

## Core resources

Paths below are relative to Core. `{id}` is a unified game identifier owned by the active tenant.

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/api/library` | Search/filter/page the unified library |
| GET | `/api/library/{id}` | Game details and platform ownership |
| GET | `/api/library/random` | Random selection from matching library entries |
| GET | `/api/library/stats` | Collection statistics |
| PATCH | `/api/library/{id}/personal` | Personal tracking and ignored state |
| GET / PUT | `/api/library/tags`, `/api/library/{id}/tags` | Tag definitions / game assignments |
| GET | `/api/library/tag-suggestions` | Tenant-local suggestions |
| DELETE | `/api/library/tags/{tagId}` | Delete a custom tag definition |
| POST | `/api/library/merge`, `/api/library/split` | Durable ownership corrections |
| GET / POST | `/api/library/{id}/physical-copies` | List/add supported physical copies |
| POST | `/api/library/physical-copies/capture` | Confirm an IGDB-backed physical capture |
| DELETE | `/api/library/{id}/physical-copies/{copyId}` | Remove one physical copy |
| GET | `/api/activity` | Active tenant's activity |

## Retrieval, sync, and enrichment

| Method | Path | Purpose |
| --- | --- | --- |
| POST | `/api/library/sync` | Synchronize cached raw platform data |
| POST | `/api/library/sync/refresh` | Refresh stale providers, then synchronize |
| POST | `/api/library/sync/refresh/force` | Deliberate forced refresh, still subject to cooldowns |
| GET | `/api/library/sync/status`, `/api/library/sync/providers` | Operation/provider state |
| POST | `/api/library/enrich`, `/api/library/enrich/stale` | Metadata enrichment |
| POST | `/api/library/enrich/{id}` | Enrich one game |
| GET | `/api/library/enrichment/search` | Search IGDB candidates |
| POST | `/api/library/{id}/enrichment/override` | Confirm a metadata replacement |
| GET | `/api/library/sync-history`, `/api/library/enrichment-history` | Sanitized audit history |
| GET | `/api/platform-credentials` | Secret-free connection status |
| PUT / DELETE | `/api/platform-credentials/{platform}` | Save/delete encrypted platform credentials |
| POST | `/api/platform-credentials/{platform}/retrieve` | Retrieve using stored credentials |
| GET | `/api/platform-credentials/retrieval-history` | Sanitized provider outcomes |

## Backup and account operations

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/api/backup`, `/api/backup/csv` | JSON backup / spreadsheet export |
| POST | `/api/backup/validate`, `/api/backup/restore` | Validate / restore into an empty tenant library |
| GET | `/api/auth/sessions` | Current account's local session families |
| DELETE | `/api/auth/sessions/{sessionId}`, `/api/auth/sessions/others` | Revoke one owned family / other families |
| PUT | `/api/auth/password` | Local compatibility password change |
| POST | `/api/auth/revoke-all` | Revoke account-wide sessions |
| DELETE | `/api/auth/account` | Guarded account deletion lifecycle |

Bootstrap, operator provisioning, token issuance, refresh, OIDC linking, and logout have distinct
policies. Follow [Authentication](../authentication.md) instead of treating all `/api/auth/*`
routes as interchangeable login endpoints.

## Ingest and integration clients

Ingest owns platform-specific raw data under `/api/ingest/{platform}` and export under
`/api/export/{platform}`. Compatibility retrieval is under `/api/retrieve/*`; authenticated
production users should use Core's credential and retrieval APIs. The CLI targets Ingest;
the [MCP server](../mcp.md) targets Core.

## Handle failures

Treat `401` as an authentication/session problem and `403` as a policy denial. Do not retry either
with a different tenant header. A `404` may intentionally conceal a resource outside the active
tenant. Respect `429` and provider cooldowns. A network error or `5xx` is not evidence that a
credential was deleted or an operation succeeded; inspect status/history before retrying writes.
