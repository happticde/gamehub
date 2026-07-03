# GOG Connect Helper Contract

Roadmap #17A introduces the first guided-account-connect slice for GOG without adding a hidden
server-side provider login flow. The local helper is intentionally user-visible: it opens a normal
GOG browser session, lets the user complete password, MFA, captcha, or provider prompts directly
with GOG, and captures only the `gog-al` cookie value plus the GOG username needed by the existing
Ingest fetcher.

## Implemented Local Helper

The local helper is the existing Go CLI. The recommended installed flow is:

```bash
cd services/cli
go build -o gamehub-cli .
./gamehub-cli helper install-protocol-handler
```

On macOS this installs a small local `GameHub Helper.app` in `~/Applications` and registers the
`gamehub-helper://` URI scheme with Launch Services. After installation, the Admin UI can launch:

```text
gamehub-helper://connect/gog?origin=http%3A%2F%2Flocalhost%3A3005
```

The handler opens Terminal and runs the same secure helper path:

```bash
gamehub-cli helper launch 'gamehub-helper://connect/gog?origin=http%3A%2F%2Flocalhost%3A3005'
```

`helper launch` validates the custom URI, rejects secret-looking query parameters, starts
`connect gog --handoff-server`, and prints the local one-shot handoff URL in Terminal. The user still
signs in directly with GOG in a visible browser. The Admin UI then polls the pasted local handoff URL
and stores the captured credential through the existing Core/Ingest vault upsert.

The portable manual launch fallback remains the loopback handoff mode:

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

## Protocol Handler Contract

The only supported custom URI for this slice is:

```text
gamehub-helper://connect/gog?origin={url-encoded-admin-origin}
```

Allowed query parameters:

- `origin`: optional, non-secret browser origin such as `http://localhost:3005`. When present, it is
  passed to the loopback helper as the exact CORS origin allowed to read the one-time result.

Rejected query parameters include `cookie`, `gog-al`, `gogAl`, `gogAlCookie`, `password`, and
`token`. Unknown parameters are rejected too. This keeps the protocol surface deliberately narrow
and prevents future UI code from accidentally moving provider session material into browser history,
OS URL-handler logs, or shell history.

The handler target is local launch only. It does not carry the handoff result, does not carry the
random loopback nonce, and does not store credential material. The actual `gog-al` transfer remains
the existing loopback handoff or JSON fallback.

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
handles `gamehub-helper://` links by opening Terminal and running:

```bash
gamehub-cli helper launch '<custom-uri>'
```

To reset or uninstall the handler app:

```bash
./gamehub-cli helper uninstall-protocol-handler
```

If a browser or macOS caches the old handler, running `install-protocol-handler` again refreshes the
Launch Services registration.

### OS Support Matrix

| OS | Status | Notes |
|----|--------|-------|
| macOS | Implemented MVP | Installs `~/Applications/GameHub Helper.app` and registers `gamehub-helper://` through Launch Services. |
| Windows | Follow-up | Needs a small installer or registry registration under `HKCU\Software\Classes\gamehub-helper`. |
| Linux | Follow-up | Needs a `.desktop` file, MIME/scheme registration through `xdg-mime`, and desktop-environment verification. |

All OSes keep the same custom URI contract and must launch the same visible-browser, loopback-handoff
helper flow.

## Loopback Handoff Protocol

Loopback handoff mode starts an HTTP server bound only to `127.0.0.1` on a random port. It creates a
32-byte random nonce and embeds that nonce in the local result URL. The `gog-al` cookie is never
placed in the URL, query string, browser history, logs, or status APIs.

The Admin UI sends:

```http
GET http://127.0.0.1:{random-port}/connect/gog/result?nonce={random-nonce}&origin={admin-origin}
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

The Admin UI may launch this custom URI:

```text
gamehub-helper://connect/gog?origin=http%3A%2F%2Flocalhost%3A3005
```

The Admin UI does not claim the handler is installed. It offers the launch action and keeps the
handoff URL paste field, JSON paste, and manual credential fields as fallbacks. The custom URI only
starts the host helper; simply opening GOG from the GameHub web page is not enough because browser
same-origin rules prevent GameHub from reading `gog.com` cookies.

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
- The custom URI must contain only non-secret launch metadata. In this slice, `origin` is the only
  accepted query parameter.
- The custom URI must never contain `gog-al`, another provider cookie, a password, a token, or the
  loopback result payload.
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

- Protocol-handler installation is implemented only for macOS in this MVP. Windows and Linux need
  their own registration installers.
- The protocol handler starts the helper but does not yet return the random loopback URL directly to
  the Admin UI. The user still pastes the handoff URL printed by the helper Terminal window.
- Browser CORS allows only the exact configured frontend origins. If GameHub runs at a different
  local origin, pass that origin with `--origin`.
- If the UI reads the one-time result but the Core upsert fails, the UI keeps the captured username
  and cookie in transient React state so the user can retry the existing manual save without
  re-reading the helper endpoint.

PSN should reuse this shape only after the GOG helper contract is stable. PSN will likely need a
provider-specific cookie map and separate validation because its browser-store session state is more
brittle than GOG's single `gog-al` cookie.
