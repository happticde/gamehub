# GameHub Authentication

GameHub keeps the existing API-key mode and optionally enables local account JWTs. Tenant identity always comes from a server-issued authentication claim, never from request bodies.

## Enable JWT

Configure both secrets before bootstrapping the first account:

```dotenv
GAMEHUB_API_KEY=replace-with-a-strong-api-key
GAMEHUB_JWT_SIGNING_KEY=replace-with-a-random-secret-of-at-least-32-bytes
```

Restart Core after changing authentication configuration. Leaving `GAMEHUB_JWT_SIGNING_KEY` empty keeps JWT disabled.

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
