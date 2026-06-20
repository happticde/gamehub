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
