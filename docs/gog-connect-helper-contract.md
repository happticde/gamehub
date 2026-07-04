# Platform Connect Helper Contract

Roadmap #17A introduced the first guided-account-connect slice for GOG without adding a hidden
server-side provider login flow. Roadmap #17B extends the same browser-centered local helper
contract to PlayStation/PSN. The local helper is intentionally user-visible: it opens a normal
provider browser session, lets the user complete password, MFA, captcha, or provider prompts
directly with GOG or Sony/PlayStation, and captures only the session material needed by the
existing Ingest fetcher.

For GOG, the helper captures the `gog-al` cookie value plus the GOG username. For PSN, the helper
captures a small named map of PlayStation/Sony session cookies for the current Ingest PSN fetcher,
which expects schema-v1 credentials shaped as:

```json
{
  "cookies": {
    "pdccws_p": "provider-cookie-value",
    "npsso": "provider-cookie-value",
    "sb-cdn.session": "provider-cookie-value"
  }
}
```

PSN provider behavior is more fragile than GOG's single-cookie flow. Sony may change cookie names,
domains, login redirects, or the GraphQL session requirements without notice. The current helper
therefore validates that at least one known PlayStation session cookie was captured, stores the full
named cookie map through the existing vault upsert, and preserves manual cookie entry as the
fallback. The PSN helper intentionally avoids the broad `chromedp` automation-default flag set
because Sony's sign-in can reject browser sessions that advertise unusual command-line behavior.

## Implemented Local Helper

The local helper is the existing Go CLI. The recommended installed flow is:

```bash
cd services/cli
go build -o gamehub-cli .
./gamehub-cli helper install-protocol-handler
```

On macOS this installs a small local `GameHub Helper.app` in `~/Applications` and registers the
`gamehub-helper://` URI scheme with Launch Services. After installation, the Admin UI can launch
GOG:

```text
gamehub-helper://connect/gog?origin=http%3A%2F%2Flocalhost%3A3005&launchId=one-time-random-value
```

or PlayStation:

```text
gamehub-helper://connect/psn?origin=http%3A%2F%2Flocalhost%3A3005&launchId=one-time-random-value
```

The handler starts the same secure helper path in the background:

```bash
gamehub-cli helper launch 'gamehub-helper://connect/gog?origin=http%3A%2F%2Flocalhost%3A3005&launchId=one-time-random-value'
```

The PSN form is identical except for the provider path:

```bash
gamehub-cli helper launch 'gamehub-helper://connect/psn?origin=http%3A%2F%2Flocalhost%3A3005&launchId=one-time-random-value'
```

`helper launch` validates the custom URI, rejects secret-looking query parameters, starts a
short-lived local launch-status endpoint, and then starts `connect gog --handoff-server` or
`connect psn --handoff-server`. The user still signs in directly with the provider in a visible
browser. The Admin UI polls the local launch-status endpoint by `launchId`, receives the local
one-shot handoff URL after the helper is ready, polls that handoff URL, and stores the captured
credential through the existing Core/Ingest vault upsert.

The portable manual launch fallback remains the loopback handoff mode:

```bash
cd services/cli
go run . connect gog --handoff-server
```

For PlayStation:

```bash
cd services/cli
go run . connect psn --handoff-server
```

To investigate Sony-side failures such as HTTP 429 responses without recording cookies, headers,
query strings, or fragments, add a sanitized diagnostics file:

```bash
go run . connect psn \
  --handoff-server \
  --diagnostics-file /tmp/gamehub-psn-login.jsonl
```

For repeated use, build the CLI and run the same command from the binary:

```bash
go build -o gamehub-cli .
./gamehub-cli connect gog --handoff-server
```

For PlayStation:

```bash
go build -o gamehub-cli .
./gamehub-cli connect psn --handoff-server
```

The helper prints a local handoff URL to stdout before opening GOG:

```text
http://127.0.0.1:49152/connect/gog/result?nonce=one-time-random-value
```

The PlayStation helper prints the same shape under `/connect/psn/result`.

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

For PlayStation:

```bash
cd services/cli
go run . connect psn
```

For repeated use:

```bash
go build -o gamehub-cli .
./gamehub-cli connect psn
```

If Sony blocks the helper-launched PlayStation login before MFA/2FA, use the validated normal Chrome
debug-profile fallback. This replaces the normal GameHub "Connect PlayStation" launch for this
attempt and reads the already signed-in browser session, including the observed `pdccws_p` cookie.
First start Chrome yourself and sign in there:

```bash
/Applications/Google\ Chrome.app/Contents/MacOS/Google\ Chrome \
  --remote-debugging-address=127.0.0.1 \
  --remote-debugging-port=49333 \
  --user-data-dir=/tmp/gamehub-psn-chrome \
  https://store.playstation.com/
```

If `http://127.0.0.1:49333/json/version` does not return Chrome debug JSON, the debug profile is not
reachable. On macOS, quit all Chrome windows and start the command again so the
`--remote-debugging-port` flag is not handed to an already running Chrome instance. A `404 Not
Found` response usually means another local service or another browser process is answering on that
port; choose a different unused loopback port and use the same port in both commands.

After the normal Chrome profile is signed in, keep it open and run:

```bash
./gamehub-cli connect psn \
  --remote-debugging-url http://127.0.0.1:49333 \
  --handoff-server \
  --origin http://localhost:3005
```

Paste the local handoff URL printed by this command into the PlayStation connector field. Do not
click the normal GameHub Connect PlayStation button again during this fallback attempt, because that
starts a fresh helper-launched Sony login flow.

This fallback still uses a one-time local handoff and the existing Core/Ingest vault upsert. It does
not put PSN cookies into the custom URI, query strings, localStorage, logs, or status APIs.

The normal guided modes open a temporary, user-visible Chrome/Chromium browser profile at the
provider. The user signs in directly in that browser window, including MFA or captcha prompts. The
helper polls the controlled browser context until it can read the provider-specific session
material, then removes the temporary browser profile when the command exits. The PSN
`--remote-debugging-url` fallback does not create or remove a browser profile; it only attaches to
the already running local Chrome debugging endpoint.

Supported flags:

- `--timeout 10m`: maximum wait time for login and capture.
- `--user gog-user`: optional username override if GOG's `userData.json` endpoint cannot report the
  username even though the cookie was captured. GOG only.
- `--handoff-server`: starts the local one-shot loopback result server instead of printing the
  cookie JSON to stdout.
- `--origin http://localhost:3005,http://127.0.0.1:3005`: comma-separated exact browser Origins
  allowed to read the loopback result through CORS. Use the frontend origin shown in the browser if
  it differs.
- `--handoff-result-ttl 10m`: maximum time to keep a captured result available after login before
  discarding it.
- `--diagnostics-file /tmp/gamehub-psn-login.jsonl`: writes sanitized PSN login network diagnostics
  as JSON lines. The trace includes request/response/failure events, status codes, sanitized URLs,
  and captured cookie names only; it omits cookies, headers, query strings, and fragments.
- `--remote-debugging-url http://127.0.0.1:49333`: PSN-only fallback that connects to an already
  running local Chrome debugging endpoint instead of opening a fresh PlayStation login browser. The
  URL must point to loopback (`localhost` or `127.0.0.1`) and may be useful when Sony blocks the
  helper-launched login flow before MFA/2FA.

The helper must run on the user's host machine, not inside the devcontainer, because it needs to
open a visible local browser. It requires a locally installed Chrome or Chromium browser discoverable
by `chromedp`.

## Protocol Handler Contract

The supported custom URIs are:

```text
gamehub-helper://connect/gog?origin={url-encoded-admin-origin}&launchId={random-launch-id}
```

```text
gamehub-helper://connect/psn?origin={url-encoded-admin-origin}&launchId={random-launch-id}
```

Allowed query parameters:

- `origin`: optional, non-secret browser origin such as `http://localhost:3005`. When present, it is
  passed to the loopback helper as the exact CORS origin allowed to read the one-time result.
- `launchId`: optional, non-secret, high-entropy URL-safe identifier generated by the Admin UI. When
  present, the helper exposes launch readiness only to callers that provide the same ID.

Rejected query parameters include `cookie`, `cookies`, `gog-al`, `gogAl`, `gogAlCookie`, `npsso`,
`psnAuthBearerToken`, `psnAuthRefreshToken`, `JSESSIONID`, `pdccws_p`, `sb-cdn.session`,
`psn-csrf-token`, `password`, `session`, and `token`, as well as handoff/result aliases such as `nonce`,
`handoffUrl`, and `resultUrl`. Unknown parameters are rejected too. This keeps the protocol surface
deliberately narrow and prevents future UI code from accidentally moving provider session material
into browser history, OS URL-handler logs, or shell history.

The handler target is local launch only. It does not carry the handoff result, does not carry the
random loopback nonce, and does not store credential material. The actual `gog-al` transfer remains
the existing loopback handoff or JSON fallback.

## Launch Status Protocol

When `launchId` is present, `helper launch` binds a short-lived HTTP server to:

```text
http://127.0.0.1:53935/connect/gog/launch-status?launchId={random-launch-id}&origin={admin-origin}
```

or:

```text
http://127.0.0.1:53935/connect/psn/launch-status?launchId={random-launch-id}&origin={admin-origin}
```

This endpoint exists only while the helper process is running. It never returns `gog-al` or PSN
cookies. It returns only the helper readiness state and, once ready, the local handoff URL that still
requires the separate one-time handoff nonce:

- `202 Accepted` with `{"status":"starting"}` while the helper is creating the one-shot handoff
  server or opening the visible GOG browser.
- `200 OK` with `{"status":"handoff_ready","handoffUrl":"http://127.0.0.1:.../connect/{provider}/result?nonce=..."}` once the handoff endpoint is ready.
- `404 Not Found` with `{"status":"invalid_launch"}` for the wrong `launchId`.
- `403 Forbidden` when a browser Origin is present and is neither configured nor a matching
  nonce-bound local/private origin.
- `500 Internal Server Error` with `{"status":"failed","message":"..."}` when helper startup fails.

The endpoint sets `Cache-Control: no-store`, grants CORS only to the requested Admin origin, and
responds to browser Private Network Access preflight checks for that same origin. The `launchId` and
handoff URL are not provider credentials, but they are short-lived local coordination values and must
not be logged by GameHub services or copied into provider credential storage.

## Installer and Registration

### macOS

Build the CLI on the host and install the handler:

```bash
cd services/cli
go build -o gamehub-cli .
./gamehub-cli helper install-protocol-handler
```

Optional installer flags:

- `--cli-path /absolute/path/to/gamehub-cli`: register a specific durable binary path. This is
  required if the command is run from a temporary `go run` binary.
- `--app-path "$HOME/Applications/GameHub Helper.app"`: choose a different app-bundle path.

The installer creates an AppleScript app bundle and registers it with Launch Services. The app
handles `gamehub-helper://` links by starting this command in the background:

```bash
gamehub-cli helper launch '<custom-uri>'
```

To reset or uninstall the handler app:

```bash
./gamehub-cli helper uninstall-protocol-handler
```

If a browser or macOS caches the old handler, running `install-protocol-handler` again refreshes the
Launch Services registration. The same handler app supports both GOG and PlayStation launch URLs.

### OS Support Matrix

| OS | Status | Notes |
|----|--------|-------|
| macOS | Implemented MVP | Installs `~/Applications/GameHub Helper.app`, registers `gamehub-helper://` through Launch Services, starts the GOG or PlayStation helper without opening Terminal, and lets the Admin UI auto-discover the local handoff URL through the launch-status endpoint. |
| Windows | Follow-up | Needs a small installer or registry registration under `HKCU\Software\Classes\gamehub-helper`. |
| Linux | Follow-up | Needs a `.desktop` file, MIME/scheme registration through `xdg-mime`, and desktop-environment verification. |

All OSes keep the same custom URI contract and must launch the same visible-browser, loopback-handoff
helper flow.

## Loopback Handoff Protocol

Loopback handoff mode starts an HTTP server bound only to `127.0.0.1` on a random port. It creates a
32-byte random nonce and embeds that nonce in the local result URL. The `gog-al` cookie and PSN
cookies are never placed in the URL, query string, browser history, logs, or status APIs.

The Admin UI sends:

```http
GET http://127.0.0.1:{random-port}/connect/{provider}/result?nonce={random-nonce}&origin={admin-origin}
Origin: http://localhost:3005
```

The default allowed origins also include `http://127.0.0.1:3005`. If the Admin UI is running on a
different host or port, start the helper with that exact browser origin, for example:

```bash
go run . connect gog --handoff-server --origin http://localhost:3000
```

The `origin` query parameter is not secret and never contains the GOG cookie. It lets the UI attach
the exact Admin page origin that the browser will send in the CORS `Origin` header. The helper uses
that value only when the nonce is correct, the query origin exactly matches the browser header, and
the origin host is loopback, private-network, or explicitly configured through `--origin`.

Responses:

- `202 Accepted` with `{"status":"pending"}` while the visible browser login is still in progress.
- `200 OK` with the helper result JSON exactly once after capture.
- `410 Gone` with `{"status":"consumed"}` after the result has already been read.
- `404 Not Found` with `{"status":"invalid_nonce"}` for the wrong nonce.
- `403 Forbidden` when a browser Origin is present and is neither configured nor a matching
  nonce-bound local/private origin.

The server also sets `Cache-Control: no-store`, grants CORS only to configured or nonce-bound local
origins, and responds to browser Private Network Access preflight checks for those same allowed
origins. The nonce is not a credential by itself, but it gates access to the one-time plaintext
result while the helper is running, so it should be treated as a short-lived handoff secret. The
result is stored only in helper memory and is removed when the first successful read returns it, when
the TTL expires, or when the helper exits.

## Browser-to-GameHub Boundary

The Admin UI may launch these custom URIs:

```text
gamehub-helper://connect/gog?origin=http%3A%2F%2Flocalhost%3A3005&launchId=one-time-random-value
```

```text
gamehub-helper://connect/psn?origin=http%3A%2F%2Flocalhost%3A3005&launchId=one-time-random-value
```

The Admin UI does not claim the handler is installed. It offers the launch action and keeps the
handoff URL paste field, JSON paste, and manual credential fields as fallbacks. The normal installed
flow no longer requires reading Terminal output: after the custom URI launch, the Admin UI polls the
local launch-status endpoint, receives the handoff URL, and then continues through the existing
one-shot loopback result endpoint. The custom URI only starts the host helper; simply opening GOG
from the GameHub web page is not enough because browser same-origin rules prevent GameHub from
reading `gog.com` cookies.

After loopback capture or JSON fallback, the Admin credential UI immediately converts the helper
result into the existing authenticated Core credential upsert.

For GOG:

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

For PlayStation:

```http
PUT /api/core/platform-credentials/Psn
Content-Type: application/json

{
  "schemaVersion": 1,
  "credentials": {
    "cookies": {
      "pdccws_p": "provider-cookie-value",
      "npsso": "provider-cookie-value",
      "sb-cdn.session": "provider-cookie-value"
    }
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

The PlayStation helper output uses the same envelope but does not include a username:

```json
{
  "provider": "psn",
  "cookies": {
    "pdccws_p": "provider-cookie-value",
    "npsso": "provider-cookie-value",
    "sb-cdn.session": "provider-cookie-value"
  },
  "capturedAtUtc": "2026-07-03T12:00:00Z",
  "expiresAtUtc": "2026-07-04T12:00:00Z"
}
```

Accepted PlayStation aliases during this slice:

- `provider` or `platform`, when present, must be `psn` or `playstation`.
- `cookies` must be a non-empty object of cookie name/value pairs.
- `status: "expired"` or `status: "session_expired"` marks the capture as unusable.
- `expiresAtUtc` or `expiresAt` marks the capture as unusable when it is already in the past.

## Security Invariants

- GameHub must not ask for the user's GOG password.
- GameHub must not ask for the user's PSN/Sony password.
- GameHub must not run a hidden server-side headless GOG or PSN password login.
- The custom URI must contain only non-secret launch metadata. In this slice, `origin` and
  `launchId` are the only accepted query parameters.
- The custom URI must never contain `gog-al`, PSN cookies, another provider cookie, a password, a
  token, or the loopback result payload.
- The launch-status endpoint must never return `gog-al` or PSN cookies; it may return only
  readiness state and the local handoff URL.
- The helper must not put `gog-al` or PSN cookies in `localStorage`, query strings, logs, or status
  APIs.
- In JSON fallback mode, the Go helper writes progress messages to stderr and writes provider
  cookies only in the final stdout JSON payload intended for immediate copy/paste into the Admin UI.
- In loopback handoff mode, stdout contains only the local handoff URL. Provider cookies are held in
  memory and served once from the `127.0.0.1` result endpoint.
- Plaintext session material may exist only in the helper capture, transient React state, the
  BFF/Core upsert request body, and Ingest's immediate encryption path.
- Core and Ingest must keep the existing authenticated, tenant-scoped credential-management and
  vault boundaries.
- A stale or expired capture should lead the UI to ask the user to reconnect the provider rather
  than trying to repair the provider session inside GameHub.

## Known Limitations

- Protocol-handler installation is implemented only for macOS in this MVP. Windows and Linux need
  their own registration installers.
- The launch-status endpoint uses a fixed loopback port (`53935`). If another local process already
  owns that port, the Admin UI cannot auto-discover the helper and the user can still use the manual
  handoff URL fallback.
- Browser CORS allows only the exact configured frontend origins. If GameHub runs at a different
  local origin, pass that origin with `--origin`.
- If the UI reads the one-time result but the Core upsert fails, the UI keeps the captured username
  and cookie in transient React state so the user can retry the existing manual save without
  re-reading the helper endpoint.
- If the UI reads a PlayStation one-time result but the Core upsert fails, the UI keeps the captured
  cookie map in transient React state so the user can retry the existing manual save without
  re-reading the helper endpoint.
- PlayStation's cookie names and browser-store session state are more brittle than GOG's single
  `gog-al` cookie. If Sony changes the required cookies, the helper allowlist and docs may need an
  update while the Core/Ingest vault schema can remain `{ "cookies": { ... } }`.
- Sony sign-in may show a provider-owned "Can't connect to the server" or "Something went wrong"
  error when it rejects the helper browser session. The helper starts PSN with a visible,
  non-headless browser profile and avoids unsupported automation-related Chrome flags, but manual
  JSON or cookie fallback remains the escape hatch if Sony blocks the local helper window for a
  particular account, region, browser build, or network.
- If Sony returns HTTP `429 Too Many Requests` from account endpoints such as
  `ca.account.sony.com/api/v1/ssocookie`, the helper fails fast with a rate-limit message instead of
  waiting until timeout. Repeated immediate retries can extend the block; wait for Sony's limit to
  clear before trying the guided helper again.
