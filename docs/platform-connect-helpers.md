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

For the Epic retrieval lifecycle after a credential is saved, including audit-state response,
cooldown discipline, and safe payload-drift investigation, see
[Epic Stored Retrieval Operations](epic-stored-retrieval-operations.md).

## Recommended macOS Setup

The Admin **Accounts** tab shows a compact card for each provider. PlayStation, GOG and Epic
Games use the same three-step assistant: install the helper, sign in with the provider, and confirm
encrypted credential storage. Steam retains its API-key setup. Manual handoff URLs, JSON results,
credential fields and diagnostics are grouped under **Having trouble? Advanced connection options**.

1. Choose **Connect** or **Reconnect** on the provider card.
2. Download the helper for Apple Silicon or Intel. Find the processor in Apple menu → About This Mac.
3. Extract the archive and open **Install GameHub Helper.command**. The installer copies the CLI to
   `~/Library/Application Support/GameHub/Helper` and registers `~/Applications/GameHub Helper.app`.
   No local Go installation is required, and the extracted download can be deleted afterwards.
4. Choose **Continue after installation** (or **Already installed**), then **Open Helper & sign in**.
5. Allow the browser to open GameHub Helper and complete the provider-owned login in its browser.
6. Wait for GameHub to report that the credential was stored. The next library import separately
   verifies whether the provider accepts the session; storage alone is not a successful import.

Install on the Mac where the GameHub browser runs, even if the server runs elsewhere. Google Chrome
must be installed on that Mac. The installation acknowledgment is shared between the three
connectors while the Accounts page is open; it is not an automatic installation check. Closing a
connecting dialog stops frontend polling, but does not terminate the local helper/browser. The
dialog cannot be dismissed during an in-flight credential write.

Each installed-helper launch now uses its own dynamic loopback status port, so retries do not
compete for an older attempt's fixed status slot. Update the frontend and the downloaded helper
together; older CLI builds do not accept the new `statusPort` metadata. Existing manual URLs
without that parameter still use port `53935`.

The downloads currently support desktop macOS only. The packages are unsigned and not notarized;
macOS may require approval to open them. Signing/notarization and installers for other systems are
follow-up work. Existing advanced CLI paths are retained, without claiming guided support for them.

For PlayStation, the normal installed helper starts or reuses the dedicated persistent Chrome
profile automatically. Manual Chrome commands are troubleshooting options, not a separate
recommended setup path. GOG and Epic keep their own existing visible-browser capture flows.

### Bundling the Downloads

The frontend Docker build cross-compiles the existing Go CLI for Darwin `arm64` and `amd64`, packages
only the executable, installer and README, and serves the archives from `/downloads/helper/`.
Docker Compose supplies the restricted `helper-source` build context from `services/cli`.
No new environment variables or provider credentials are required for packaging.

For local frontend development, generate the same downloads through the devcontainer:

```bash
docker exec -w /workspaces/game-library-backend/services/cli \
  game-library-backend_devcontainer-app-1 \
  sh scripts/package-macos-helper.sh /workspaces/game-library-backend/frontend/public/downloads/helper
```

Generated downloads are ignored by Git. If they are absent, the assistant reports that downloads
are unavailable instead of offering a broken link. Building the frontend Dockerfile independently
also requires the named context `helper-source=./services/cli`; see the frontend image CI command.

## Build the CLI (Advanced)

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
scheme with Launch Services. After writing the final protocol `Info.plist`, it applies a local
ad-hoc signature and verifies the bundle before registration. This repairs the signature produced
by `osacompile`, which becomes invalid when the plist is changed. It does not provide Developer ID
trust or notarization. The Admin UI can then launch URLs such as:

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

1. Open the Admin Accounts view and choose the matching connect assistant.
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

Paste the printed handoff URL into the PlayStation assistant's advanced connection field. Do not
start another connect attempt while manually importing this one-time result. The installed
protocol handler already uses this persistent profile; plain `connect psn` without a remote-debugging
URL still opens the fresh diagnostic browser.

The fresh helper-launched PSN browser remains useful for diagnosis or fallback only. It is not the
recommended operator path because Sony's fresh-profile behavior is fragile.

## Troubleshooting

### macOS blocks the downloaded installer or CLI

After trying to open the blocked file, go to **System Settings → Privacy & Security**,
choose **Open Anyway** for that file, and confirm **Open**. The installer and `gamehub-cli`
can require separate approvals.

The installer executes the CLI only at
`~/Library/Application Support/GameHub/Helper/gamehub-cli`. It leaves that file in place
when execution fails and reuses it when the downloaded version is identical. After approving
`gamehub-cli`, run the installer again to finish registering the protocol handler. A new
helper version may need another approval. If no security warning appeared, inspect the
installer error and confirm that the download matches the Mac's processor.

### Helper does not start

- After updating GameHub, download and install the matching helper version. Older helpers reject
  the new per-attempt `statusPort` parameter. A previous legacy attempt may occupy port `53935`;
  updated UI/helper launches use separate ports, while manual handoff remains available.
- If the installed app has a modified/invalid `Info.plist` signature, reinstall from an updated
  helper download. The installer now signs and verifies the final app before registering it.
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
