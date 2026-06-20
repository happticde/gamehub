# ADR-002: API Authentication

**Date:** 2026-03-06
**Status:** Accepted
**Deciders:** @jankahnt

## Context

The GameHub backend currently has **no authentication**. All endpoints are publicly accessible. This is risky because:

- `RetrieveController` accepts raw platform credentials (API keys, cookies) in request bodies
- `POST /api/library/sync` triggers server-to-server traffic
- `POST /api/library/merge` and `/split` modify the unified library
- The Ingest API can write arbitrary data to the database

For a single-user, locally-hosted app, the attack surface is small. But as soon as the service is exposed on a network (even a home LAN), some form of authentication is needed.

## Options Considered

### Option A: Static API Key Middleware

A shared secret (`X-Api-Key` header) checked by ASP.NET middleware before requests reach controllers.

```
X-Api-Key: my-secret-key-here
```

| Aspect | Detail |
|--------|--------|
| **Complexity** | Minimal — ~50 lines of middleware |
| **Config** | Single key in `appsettings.json` or env var |
| **Per-user** | No — single shared key |
| **Rotation** | Manual — change the config value and restart |
| **Granularity** | All-or-nothing (can be extended with role-based keys) |
| **Best for** | Single-user, self-hosted, CLI/API automation |

### Option B: JWT Bearer Tokens

Standard JWT authentication with a token issued by the service or an external identity provider.

| Aspect | Detail |
|--------|--------|
| **Complexity** | Moderate — token generation, validation, refresh |
| **Config** | Signing key/secret, token lifetime, issuer/audience |
| **Per-user** | Yes — tokens carry user identity claims |
| **Rotation** | Automatic — tokens expire, refresh tokens rotate |
| **Granularity** | Fine-grained via claims (admin, read-only, per-platform) |
| **Best for** | Multi-user, frontend integration, OAuth flows |

### Option C: ASP.NET Identity + Cookie Auth

Full user management with registration, login, password hashing, and session cookies.

| Aspect | Detail |
|--------|--------|
| **Complexity** | High — user tables, registration flow, password reset |
| **Config** | Database migrations, email service for recovery |
| **Per-user** | Yes — full user accounts |
| **Rotation** | Automatic — session cookies expire |
| **Granularity** | Role-based (admin, user) |
| **Best for** | Web frontend with login pages, multi-tenant |

### Option D: Reverse Proxy Auth (External)

Delegate auth to a reverse proxy (Traefik, Caddy, nginx) or an auth gateway (Authelia, Authentik).

| Aspect | Detail |
|--------|--------|
| **Complexity** | Zero code changes — proxy config only |
| **Config** | Proxy configuration, external auth provider |
| **Per-user** | Depends on the auth provider |
| **Rotation** | Handled by the proxy/provider |
| **Granularity** | Path-based (e.g. protect `/api/retrieve/*` but allow `/health`) |
| **Best for** | Self-hosted setups already behind a reverse proxy |

## Recommendation

**Start with Option A (API Key Middleware)**, then evolve to Option B (JWT) if/when multi-user or frontend authentication is needed.

Rationale:
- This is currently a **single-user, self-hosted** application
- The CLI, cron jobs, and future UI all work well with a static API key
- API key middleware is ~50 lines, zero dependencies, and can be shipped today
- JWT can be added later as a second auth scheme without removing API key support
- Option D is complementary — users can add a reverse proxy at any time regardless of which option is chosen here

### Proposed Implementation (Option A)

1. **Middleware** checks `X-Api-Key` header on every request
2. **Skip list** for unauthenticated endpoints: `/health`, `/swagger`
3. **Config** via `Authentication:ApiKey` in `appsettings.json` or `GAMEHUB_API_KEY` env var
4. **Behavior when no key configured**: auth is disabled (don't break local dev)
5. Apply to **both services** (Ingest and Core)

### Migration Path to JWT (Option B)

When multi-user support is needed:
1. Add `Microsoft.AspNetCore.Authentication.JwtBearer`
2. Add a `POST /api/auth/token` endpoint that validates credentials and issues JWTs
3. Register JWT as a second authentication scheme alongside API key
4. Controllers can then use `[Authorize(Roles = "Admin")]` for granular access

## Consequences

### Positive
- Prevents unauthorized access to sensitive endpoints
- CLI and automation workflows only need to add one header
- No database changes required
- Backward-compatible — if no key is configured, auth is disabled

### Negative
- API key is a shared secret — if leaked, full access is exposed until rotated
- No per-user permissions (acceptable for single-user)
- Clients must store the key (CLI config, env var, etc.)

## Implementation Update (2026-06-20)

The API-key scheme remains the backward-compatible default. Core additionally supports optional JWT bearer authentication when `Authentication:Jwt:SigningKey` is configured with at least 32 UTF-8 bytes.

- `POST /api/auth/bootstrap` creates the first password-hashed local account for the API key's trusted tenant and requires a configured API key.
- `POST /api/auth/token` verifies local credentials and issues a short-lived JWT containing the trusted tenant claim.
- JWT and API-key authentication coexist through a policy scheme; existing API-key clients continue to work.
- Token requests are rate-limited and return a generic invalid-credentials response.
- Refresh tokens, tenant provisioning, registration UI, password recovery, and OAuth remain future work.
