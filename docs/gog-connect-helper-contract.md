# GOG Connect Helper Contract

Roadmap #17A introduces the first guided-account-connect slice for GOG without adding a hidden
server-side provider login flow. The local helper is intentionally user-visible: it opens a normal
GOG browser session, lets the user complete password, MFA, captcha, or provider prompts directly
with GOG, and captures only the `gog-al` cookie value plus the GOG username needed by the existing
Ingest fetcher.

## Browser-to-GameHub Boundary

The Admin credential UI accepts a one-time JSON result from the helper. The UI immediately converts
that result into the existing authenticated Core credential upsert:

```http
PUT /api/core/platform-credentials/Gog
Content-Type: application/json

{
  "schemaVersion": 1,
  "credentials": {
    "user": "gog-user",
    "cookie": "gog-al-cookie-value"
  }
}
```

`/api/core/*` is the Next.js BFF route. Browser JavaScript never receives Core bearer tokens; the
BFF forwards the request with HttpOnly GameHub session cookies. Core then delegates to Ingest's
service-key-only tenant credential vault. Status APIs continue to return only configuration state
and timestamps.

## Helper Result JSON

The first UI slice accepts this helper output:

```json
{
  "provider": "gog",
  "user": "gog-user",
  "cookies": {
    "gog-al": "gog-al-cookie-value"
  },
  "capturedAtUtc": "2026-07-03T12:00:00Z",
  "expiresAtUtc": "2026-07-04T12:00:00Z"
}
```

Accepted aliases during the POC:

- `provider` or `platform`, when present, must be `gog`.
- `user` or `username` contains the GOG username.
- `cookies["gog-al"]`, `cookie`, `gogAl`, or `gogAlCookie` contains the session cookie value.
- `status: "expired"` or `status: "session_expired"` marks the capture as unusable.
- `expiresAtUtc` or `expiresAt` marks the capture as unusable when it is already in the past.

## Security Invariants

- GameHub must not ask for the user's GOG password.
- GameHub must not run a hidden server-side headless GOG password login.
- The helper must not put the `gog-al` cookie in `localStorage`, query strings, logs, or status APIs.
- Plaintext session material may exist only in the helper capture, transient React state, the
  BFF/Core upsert request body, and Ingest's immediate encryption path.
- Core and Ingest must keep the existing authenticated, tenant-scoped credential-management and
  vault boundaries.
- A stale or expired capture should lead the UI to ask the user to reconnect GOG rather than trying
  to repair the provider session inside GameHub.

PSN should reuse this shape only after the GOG helper contract is stable. PSN will likely need a
provider-specific cookie map and separate validation because its browser-store session state is more
brittle than GOG's single `gog-al` cookie.
