# GOG Connect Helper Contract

Roadmap #17A introduces the first guided-account-connect slice for GOG without adding a hidden
server-side provider login flow. The local helper is intentionally user-visible: it opens a normal
GOG browser session, lets the user complete password, MFA, captcha, or provider prompts directly
with GOG, and captures only the `gog-al` cookie value plus the GOG username needed by the existing
Ingest fetcher.

## Implemented Local Helper

The local helper is the existing Go CLI. The portable recommended flow is the loopback handoff
mode:

```bash
cd services/cli
go run . connect gog --handoff-server
```

For repeated use, build the CLI and run the same command from the binary:

```bash
go build -o gamehub-cli .
./gamehub-cli connect gog --handoff-server
```

The helper prints a local handoff URL to stdout before opening GOG:

```text
http://127.0.0.1:49152/connect/gog/result?nonce=one-time-random-value
```

Paste that URL into the Admin credential UI and leave the helper running. The UI polls the local
URL while the user signs in directly with GOG in the visible browser. After capture, the helper
serves the credential payload once from memory and then clears it.

The JSON stdout fallback is still available:

```bash
cd services/cli
go run . connect gog
```

For repeated use, build the CLI and run the same command from the binary:

```bash
go build -o gamehub-cli .
./gamehub-cli connect gog
```

Both modes open a temporary, user-visible Chrome/Chromium browser profile at GOG. The user signs in
directly with GOG in that browser window, including MFA or captcha prompts. The helper polls the
controlled browser context until it can read the `gog-al` cookie and the GOG username, then removes
the temporary browser profile when the command exits.

Supported flags:

- `--timeout 10m`: maximum wait time for login and capture.
- `--user gog-user`: optional username override if GOG's `userData.json` endpoint cannot report the
  username even though the cookie was captured.
- `--handoff-server`: starts the local one-shot loopback result server instead of printing the
  cookie JSON to stdout.
- `--origin http://localhost:3005,http://127.0.0.1:3005`: comma-separated exact browser Origins
  allowed to read the loopback result through CORS. Use the frontend origin shown in the browser if
  it differs.
- `--handoff-result-ttl 10m`: maximum time to keep a captured result available after login before
  discarding it.

The helper must run on the user's host machine, not inside the devcontainer, because it needs to
open a visible local browser. It requires a locally installed Chrome or Chromium browser discoverable
by `chromedp`.

## Loopback Handoff Protocol

Loopback handoff mode starts an HTTP server bound only to `127.0.0.1` on a random port. It creates a
32-byte random nonce and embeds that nonce in the local result URL. The `gog-al` cookie is never
placed in the URL, query string, browser history, logs, or status APIs.

The Admin UI sends:

```http
GET http://127.0.0.1:{random-port}/connect/gog/result?nonce={random-nonce}
Origin: http://localhost:3005
```

The default allowed origins also include `http://127.0.0.1:3005`. If the Admin UI is running on a
different host or port, start the helper with that exact browser origin, for example:

```bash
go run . connect gog --handoff-server --origin http://localhost:3000
```

Responses:

- `202 Accepted` with `{"status":"pending"}` while the visible browser login is still in progress.
- `200 OK` with the helper result JSON exactly once after capture.
- `410 Gone` with `{"status":"consumed"}` after the result has already been read.
- `404 Not Found` with `{"status":"invalid_nonce"}` for the wrong nonce.
- `403 Forbidden` when a browser Origin is present and does not exactly match `--origin`.

The server also sets `Cache-Control: no-store`, grants CORS only to configured origins, and responds
to browser Private Network Access preflight checks for those same allowed origins. The nonce is not a
credential by itself, but it gates access to the one-time plaintext result while the helper is
running, so it should be treated as a short-lived handoff secret. The result is stored only in helper
memory and is removed when the first successful read returns it, when the TTL expires, or when the
helper exits.

## Browser-to-GameHub Boundary

Future Admin UI or installer work may introduce this custom URI:

```text
gamehub-helper://connect/gog
```

The repository does not install or register that URI scheme. The current Admin UI therefore does not
claim that `gamehub-helper://connect/gog` is available. Until a platform-specific installer exists,
the documented local command above is the portable launch path. Any future URI handler must invoke
the same helper behavior: open a user-visible browser context for `gog.com`; simply opening GOG from
the GameHub web page is not enough because browser same-origin rules prevent GameHub from reading
`gog.com` cookies.

After loopback capture or JSON fallback, the Admin credential UI immediately converts the helper
result into the existing authenticated Core credential upsert:

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

The current Go helper emits `provider`, `user`, `cookies`, `capturedAtUtc`, and `expiresAtUtc` when
the browser reports a persistent cookie expiry. Session cookies omit `expiresAtUtc`.

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
- In JSON fallback mode, the Go helper writes progress messages to stderr and writes the cookie only
  in the final stdout JSON payload intended for immediate copy/paste into the Admin UI.
- In loopback handoff mode, stdout contains only the local handoff URL. The cookie is held in memory
  and served once from the `127.0.0.1` result endpoint.
- Plaintext session material may exist only in the helper capture, transient React state, the
  BFF/Core upsert request body, and Ingest's immediate encryption path.
- Core and Ingest must keep the existing authenticated, tenant-scoped credential-management and
  vault boundaries.
- A stale or expired capture should lead the UI to ask the user to reconnect GOG rather than trying
  to repair the provider session inside GameHub.

## Known Limitations

- The local helper still has to be started from a host terminal. Portable
  `gamehub-helper://connect/gog` registration is not implemented because it needs OS-specific
  installer work.
- The Admin UI cannot discover the random loopback port automatically without a registered helper or
  another local discovery mechanism, so the user pastes the handoff URL printed by the helper.
- Browser CORS allows only the exact configured frontend origins. If GameHub runs at a different
  local origin, pass that origin with `--origin`.
- If the UI reads the one-time result but the Core upsert fails, the UI keeps the captured username
  and cookie in transient React state so the user can retry the existing manual save without
  re-reading the helper endpoint.

PSN should reuse this shape only after the GOG helper contract is stable. PSN will likely need a
provider-specific cookie map and separate validation because its browser-store session state is more
brittle than GOG's single `gog-al` cookie.
