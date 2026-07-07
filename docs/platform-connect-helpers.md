# Platform Connect Helpers

This guide is the practical operator runbook for GameHub's local account-connect helpers. For the
lower-level protocol and payload contract, see
[Platform Connect Helper Contract](gog-connect-helper-contract.md).

The helpers are local, user-visible browser flows for self-hosted GameHub deployments. They let an
operator sign in directly with the provider in a normal browser window, complete provider-owned MFA
or captcha prompts, and hand only the minimum captured session material back to GameHub's existing
credential upsert path.

Supported platforms:

- Epic Games: captures the short-lived launcher-compatible `authorizationCode` JSON page.
- GOG: captures the `gog-al` cookie and username.
- PlayStation: captures the PlayStation/Sony cookie map required by the current PSN fetcher.

These flows do not add official OAuth support, do not make the providers production-proof, and do
not replace the existing encrypted Ingest credential vault.

## Build the CLI

Run the helper on the host machine, not inside the devcontainer, because it needs to open or attach
to a local browser.

```bash
cd services/cli
go build -o gamehub-cli .
```

## Install the Protocol Handler

On macOS, install the `gamehub-helper://` handler from the built binary:

```bash
./gamehub-cli helper install-protocol-handler
```

The installer creates a local `GameHub Helper.app` in `~/Applications` and registers the custom URI
scheme with Launch Services. The Admin UI can then launch URLs such as:

```text
gamehub-helper://connect/gog?origin=http%3A%2F%2Flocalhost%3A3005&launchId=one-time-random-value
```

The same handler supports `gog`, `psn`, and `epic`. Re-run the installer after moving or rebuilding
the CLI binary. To remove the registration:

```bash
./gamehub-cli helper uninstall-protocol-handler
```

Windows and Linux protocol-handler installers are still follow-up work; use the manual handoff or
JSON fallback on those hosts.

## Normal Admin UI Flow

1. Open the Admin credentials/connectors view.
2. Click the provider connect action for Epic Games, GOG, or PlayStation.
3. Allow the browser or operating system to open `gamehub-helper://` with GameHub Helper.
4. Complete provider login in the visible provider browser. GameHub never asks for the provider
   password in its own UI.
5. Leave the helper running while the Admin UI polls the local launch-status and handoff endpoints.
6. When capture succeeds, the Admin UI saves the credential through the existing Core/Ingest vault
   upsert path and shows the updated credential status.

The protocol URL carries only non-secret launch metadata such as origin and launch ID. Provider
cookies, Epic authorization codes, and tokens are not placed in the custom URI.

## Manual and Fallback Paths

If the protocol handler is not installed or the Admin UI cannot auto-discover the helper, start the
one-time handoff server manually and paste the printed URL into the matching Admin UI handoff field:

```bash
./gamehub-cli connect gog --handoff-server
./gamehub-cli connect epic --handoff-server
./gamehub-cli connect psn --handoff-server
```

If GameHub runs on a non-default local origin, pass the exact Admin UI origin:

```bash
./gamehub-cli connect gog --handoff-server --origin http://localhost:3000
```

The helper prints a loopback URL shaped like:

```text
http://127.0.0.1:49152/connect/gog/result?nonce=one-time-random-value
```

Keep the command running while signing in. The Admin UI reads the result once from memory; after a
successful read, timeout, or helper exit, the result is no longer available.

JSON stdout fallback is available when the UI cannot reach the loopback handoff URL:

```bash
./gamehub-cli connect gog
./gamehub-cli connect epic
./gamehub-cli connect psn
```

Copy only the final JSON payload into the matching Admin UI JSON field. Progress messages are sent
to stderr. Do not paste JSON payloads into logs, issues, screenshots, or chat.

Manual credential fields remain available as the last fallback. Use them only for the current
provider credential shape shown by the Admin UI, then save through the normal credential action so
Ingest encrypts the value in the vault.

## PlayStation Recommended Path

For PSN, the reliable path is a persistent local Chrome profile exposed through loopback remote
debugging. Sony can reject fresh helper-launched browser profiles or rate-limit final session-cookie
minting with HTTP `429`, even after earlier sign-in steps succeed. The persistent profile keeps a
normal browser session that Sony has already accepted, while GameHub still reads the result through
the same one-time local handoff and vault upsert path.

Start Chrome yourself and sign in to PlayStation in that profile:

```bash
/Applications/Google\ Chrome.app/Contents/MacOS/Google\ Chrome \
  --remote-debugging-address=127.0.0.1 \
  --remote-debugging-port=49333 \
  --user-data-dir=$HOME/Library/Caches/GameHub/psn-chrome-profile \
  https://store.playstation.com/
```

Confirm that Chrome is reachable:

```text
http://127.0.0.1:49333/json/version
```

After the profile is signed in, keep Chrome open and run:

```bash
./gamehub-cli connect psn \
  --remote-debugging-url http://127.0.0.1:49333 \
  --handoff-server \
  --origin http://localhost:3005
```

Paste the printed handoff URL into the PlayStation connector field. Do not click the normal
PlayStation connect button again during this attempt, because that starts a fresh helper-launched
browser flow.

The fresh helper-launched PSN browser remains useful for diagnosis or fallback only. It is not the
recommended operator path because Sony's fresh-profile behavior is fragile.

## Troubleshooting

### Helper does not start

- Rebuild the CLI with `go build -o gamehub-cli .`.
- Re-run `./gamehub-cli helper install-protocol-handler` after moving the binary.
- On macOS, confirm that `~/Applications/GameHub Helper.app` exists and try the connect action again.
- Use `./gamehub-cli connect <provider> --handoff-server` and paste the handoff URL manually if the
  custom URI handler is unavailable.

### Browser login runs, but no result appears

- Leave the helper command and provider browser open until capture finishes or timeout expires.
- Confirm that the Admin UI origin matches the helper `--origin` value.
- For GOG, make sure the browser reached a logged-in `gog.com` session.
- For Epic, make sure the final Epic JSON page contains an `authorizationCode`.
- For PSN, prefer the persistent Chrome remote-debugging path described above.

### Captured, but saving failed

- Do not re-read or repost the helper payload. Retry the Admin UI save action if it kept the
  captured value in transient state.
- Confirm that the GameHub Core and Ingest services are running and that the current GameHub session
  is still valid.
- If the one-time handoff was already consumed and the UI no longer has the captured value, rerun the
  helper and capture a fresh result.

### Handoff URL expired

- Start the helper again and use the new printed URL.
- Increase the post-capture availability window only for slow manual workflows:

```bash
./gamehub-cli connect gog --handoff-server --handoff-result-ttl 15m
```

### PSN HTTP 429 or Sony blocks fresh profiles

- Stop retrying the fresh helper-launched PSN browser immediately; repeated attempts can extend the
  provider-side block.
- Use the persistent Chrome remote-debugging profile on `127.0.0.1:49333` or another unused loopback
  port.
- For sanitized diagnosis, run:

```bash
./gamehub-cli connect psn \
  --handoff-server \
  --diagnostics-file /tmp/gamehub-psn-login.jsonl
```

The diagnostics file records sanitized events, status codes, sanitized URLs, and cookie names only.
It must not contain cookies, headers, query strings, fragments, or tokens.

## Security Boundaries

- GameHub never asks for GOG, PlayStation/Sony, or Epic Games passwords.
- Tokens, cookies, authorization codes, and helper JSON payloads must not be posted in logs,
  screenshots, GitHub issues, chat, or support tickets.
- Secrets flow only through helper capture, transient Admin UI state, the authenticated Core/BFF
  upsert request, and Ingest's immediate encryption path.
- Ingest stores provider credentials encrypted in the tenant-scoped credential vault.
- Status endpoints, launch-status endpoints, URLs, localStorage, and logs must not expose provider
  secrets.
- `.env` files remain user-owned. Do not read, print, edit, delete, or ask an assistant to inspect
  them.
