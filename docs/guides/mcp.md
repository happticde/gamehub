# Connect an AI assistant with MCP

The optional GameHub MCP server lets a compatible assistant search your collection, inspect games,
get statistics or a random pick, list tags, and update play status or personal ratings.

It runs as a local stdio process and connects to Core. It is separate from the platform connection
helper and is not needed to use the web application.

## Requirements

- A running Core service reachable from the assistant's computer.
- Docker running on the assistant's computer for the image-based setup below.
- A published MCP image matching your Core release, or a locally built MCP image.
- Authentication accepted by the target Core deployment.

Library tools can use a selected user connection with rotating renewal, or a Core API key in
the separate compatibility mode. Select `Mcp__AuthenticationMode=User` for user authorization;
`ApiKey` retains the earlier compatibility behavior and is the default for existing configurations.
User mode ignores `Core__ApiKey` and `Core__BaseUrl`, including after an authorization failure.
The [local acceptance and operations guide](https://github.com/happticde/gamehub/blob/main/docs/mcp-local-release-gate.md)
records the test boundary and release/enablement steps for OpenID Connect (OIDC) accounts.
End-user rollout requires the merged and verified [release gate #75](https://github.com/happticde/gamehub/issues/75)
and explicit operator configuration.

In versions including [#74](https://github.com/happticde/gamehub/issues/74), Admin **Security**
shows each assistant connection and its approved permissions. Choose **Zugriff widerrufen**
and confirm the connection ID to end only that connection. Other connections and your browser
session remain valid. A new login and consent are required to reconnect. See the
[connection management reference](https://github.com/happticde/gamehub/blob/main/docs/mcp-connections.md)
for lifecycle and ownership details.

## Staged device login and credential volume

This section applies only to a local build or published version that includes
[#72](https://github.com/happticde/gamehub/issues/72). Core must have the default-off
[browser consent and protocol deployment](https://github.com/happticde/gamehub/blob/main/docs/mcp-browser-consent.md) correctly configured.
Keep the public HTTPS issuer distinct from the external login provider. Route
`POST /connect/device`, `POST /connect/token` and `POST /connect/revoke` directly to Core;
the browser uses Next.js `/mcp/verify`. Normal certificate checks stay enabled and credential
requests never follow redirects.

Set these non-secret process variables in your terminal or private assistant configuration:

| Variable | Meaning |
| --- | --- |
| `Mcp__AuthenticationMode` | `User` for normal tool startup with the selected user connection; terminal setup/login/status/logout always use the device connection |
| `Mcp__Issuer` | Exact root HTTPS GameHub URL with trailing `/`, for example `https://gamehub.example.com/` |
| `Mcp__Resource` | Exactly the issuer followed by `api`, for example `https://gamehub.example.com/api` |
| `Mcp__Connection` | Stable local name, 1–64 ASCII letters/digits, `-` or `_`; use a different name per assistant installation |
| `Mcp__CredentialDirectory` | Absolute path inside the container; default `/var/lib/gamehub-mcp` |

Create a dedicated named volume and run explicit setup. These commands use a locally built image:

```bash
docker volume create gamehub-mcp-credentials
docker run --rm --mount type=volume,src=gamehub-mcp-credentials,dst=/var/lib/gamehub-mcp \
  -e Mcp__Issuer -e Mcp__Resource -e Mcp__Connection gamehub-mcp:local setup
docker run --rm -it --mount type=volume,src=gamehub-mcp-credentials,dst=/var/lib/gamehub-mcp \
  -e Mcp__Issuer -e Mcp__Resource -e Mcp__Connection gamehub-mcp:local login
```

The interactive command shows a fixed verification URL and a short code on the terminal's error
stream. Open the URL in your host browser, sign in, enter the code, inspect the account and
permissions, then approve or deny. No browser is opened inside Docker. You can use another device
if the terminal has no browser. Ctrl+C cancels; run login again for a new code. If approval already
completed but the result was lost, revoke the unused connection in GameHub before retrying.
Never pass a code or token as a command argument or copy terminal codes into diagnostics.

Login requests library reads and renewal by default. `login --tracking-write` additionally requests
play-status/rating updates; the browser still requires the separate unchecked permission choice.
Only one credential is retained for each issuer/resource/connection tuple. An existing credential
is never silently replaced by another login. Run logout first or select a new connection name.

Use the same variables and volume for later disposable containers:

```bash
docker run --rm --mount type=volume,src=gamehub-mcp-credentials,dst=/var/lib/gamehub-mcp \
  -e Mcp__Issuer -e Mcp__Resource -e Mcp__Connection gamehub-mcp:local status
docker run --rm --mount type=volume,src=gamehub-mcp-credentials,dst=/var/lib/gamehub-mcp \
  -e Mcp__Issuer -e Mcp__Resource -e Mcp__Connection gamehub-mcp:local logout
```

`status` reports only whether authorization is stored, never credentials or an assertion that the
server connection is still live. Logout presents the selected credential to Core first, then removes
only that local credential. If remote revocation is unavailable or uncertain, exit code **2** and
a clear message require revocation through GameHub; removing a file does not prove server logout.
Exit code **1** means invalid configuration, cancelled login or unsafe/unavailable storage. Success
is **0**. A corrupt file is preserved for operator repair rather than treated as missing authorization.
Use Admin **Security** to resolve uncertain remote revocation. Existing account-wide session
revocation also ends the connection but affects other sessions.

Core uses OpenIddict's [OAuth 2.0 Token Revocation](https://www.rfc-editor.org/rfc/rfc7009)
processing to validate the presented access/refresh token and client binding. GameHub then admits
the exact owner, resource and connection under the existing lifecycle locks and transactionally
closes that family, grant, authorization and its tokens. Other connections and browser families are
preserved. The route shares the 4 KiB form limit, strict HTTPS/Host/origin/query checks, global
30-per-minute request budget and 16 concurrent protocol slots. Revocation adds a 15-second
cancellation deadline and 5-second database statement timeout. Unknown/already invalid tokens
receive the standard empty 200 receipt without disclosing whether a connection exists; database
or commit failures cannot produce a successful receipt. The exact previous public-client
registration can gain the revocation endpoint permission at startup; other persisted trust drift
still aborts startup.

The image runs as its existing non-root application user. New named volumes inherit the image's
owned `0700` directory; credential/lock files use `0600`. Bind mounts must already have the matching
runtime owner and directory permissions. Setup rejects unsafe existing permissions and symbolic
links instead of changing shared host directories. A process-held exclusive file lock covers each
selected read/network/write sequence; it is released when the process exits. Do not delete lock
files while processes run. Writes flush a private temporary file before atomic replacement.
Interrupted temporary files are never credentials. Other issuers and connection names remain separate.

**The volume is not encrypted.** Host/Docker administrators and readable backups can obtain bearer
credentials. Operators own disk and backup encryption, permissions and backup/restore. Use a
dedicated local Docker volume with working POSIX permissions, file locks and atomic rename; network
shares with weaker semantics are unsupported. A host-keychain or external-store integration is a
separate prerequisite where encrypted client storage is required. Do not mount the protocol server's
private keys into this client volume.

Normal stdio startup never prompts, starts device login or opens a browser. Setup/login/status/logout
write nothing to stdout, preserving the assistant's JSON-RPC transport. Do not add `-t` to an
assistant's normal stdio launch. To launch tools with the stored connection, pass the same volume
and variables, plus the explicit mode:

```bash
docker run --rm -i --mount type=volume,src=gamehub-mcp-credentials,dst=/var/lib/gamehub-mcp \
  -e Mcp__AuthenticationMode=User -e Mcp__Issuer -e Mcp__Resource -e Mcp__Connection gamehub-mcp:local
```

For an assistant's process configuration, use `docker` as the command and the individual arguments
above as its argument array, with no terminal command after the image. Supply the non-secret
issuer/resource/connection variables through that assistant's private process environment. The
API-key examples below describe the separate compatibility mode.

Each tool rereads the selected credential under the cross-process lock. Access expiring within
30 seconds is renewed at the fixed issuer's `/connect/token`, using only the original resource
and scope set. A successful response must rotate both credentials and retain that scope set;
the replacement is flushed and atomically saved before any tool request. Core independently
preserves the original approval epoch and thirty-day absolute grant expiry. Reads use only the
five allowed library routes; writes require explicit `tracking:write` consent and use only the
personal-status/rating route. The lock spans both the read and update in a tracking tool, so a
concurrent logout/login cannot switch its owner between those requests.

A private `refresh.pending` marker is flushed before sending the one-time refresh credential.
Timeouts, cancellation, invalid or rejected responses, save failures and a process exit leave that
marker in place. Fresh processes then require **logout followed by login**; they never replay the
possibly consumed credential. Status reports this recovery state without exposing tokens. Logout
attempts remote revocation using the retained credential and removes the selected local state;
if remote logout cannot be confirmed, revoke that connection in GameHub. Core can revoke the
whole connection using its cryptographically validated, retained predecessor even after rotation.
That credential is presented only for revocation, never replayed at the refresh endpoint. Never delete a marker
to force an automatic retry. Even a rejected or lost tool response causes no automatic refresh,
operator-key fallback or write replay. A later explicit tool call can retry a service outage when
the credential itself is still usable. Contention on the local lock is bounded and can fail with
a sanitized storage error; retry after the other process finishes.

## Use the Docker image

The assistant starts a separate container for its stdio connection. The image contains the compiled
MCP server and .NET runtime. It needs no source checkout, SDK, devcontainer, database volume, or
published port. Docker selects Linux AMD64 or ARM64 to match your computer.

The image name is `ghcr.io/happticde/gamehub-mcp`. Use a version tag from a successful
[GameHub release](https://github.com/happticde/gamehub/releases) that lists MCP. Older releases
may not contain this image. `YOUR_RELEASE_TAG` below is a placeholder for that published tag.
The release workflow publishes images only when an operator runs it; merging source does not
publish a tag. Private GHCR packages require `docker login ghcr.io` first.

### Codex

Add this entry to your private `~/.codex/config.toml`:

```toml
[mcp_servers.gamehub]
command = "docker"
args = [
  "run", "--rm", "-i",
  "-e", "Core__BaseUrl",
  "-e", "Core__ApiKey",
  "ghcr.io/happticde/gamehub-mcp:YOUR_RELEASE_TAG"
]
startup_timeout_sec = 60

[mcp_servers.gamehub.env]
Core__BaseUrl = "http://host.docker.internal:5001"
Core__ApiKey = "replace-with-the-permitted-api-key"
```

Pull the chosen image once before connecting so the first startup does not wait for a download.
Keep your key in private client configuration. Do not commit it or put it into the image.
See the [official Codex MCP documentation](https://developers.openai.com/codex/mcp) for configuration
options.

### Other MCP clients

For a client that accepts an `mcpServers` JSON object, use:

```json
{
  "mcpServers": {
    "gamehub": {
      "command": "docker",
      "args": [
        "run", "--rm", "-i",
        "-e", "Core__BaseUrl",
        "-e", "Core__ApiKey",
        "ghcr.io/happticde/gamehub-mcp:YOUR_RELEASE_TAG"
      ],
      "env": {
        "Core__BaseUrl": "http://host.docker.internal:5001",
        "Core__ApiKey": "replace-with-the-permitted-api-key"
      }
    }
  }
}
```

Clients using a `servers` object need the equivalent stdio entry in their own configuration.
Use `-i` to keep stdin open. Do not add `-t` or `-d`: the assistant needs the plain stdio
connection. `--rm` removes the container after the session ends. No Docker socket is mounted
inside the MCP container. The client itself needs access to Docker.

### Reach Core from the container

`Core__BaseUrl` must be reachable from inside the MCP container:

| Core location | Configuration |
| --- | --- |
| On the Docker Desktop host | Use `http://host.docker.internal:5001` if Core is published on host port 5001. |
| In the same Docker network | Add `"--network", "YOUR_GAMEHUB_NETWORK"` before the image argument. Use the Core service address, for example `http://core:8080`. |
| On another private server | Use its reachable HTTPS Core address. |

On Linux Docker Engine, prefer the shared Docker network. Host access can instead use
`"--add-host", "host.docker.internal:host-gateway"`, but Core must listen on an interface reachable
from Docker. A host port bound only to `127.0.0.1` is not reachable through that gateway.
`localhost` inside the MCP container refers to the MCP container itself.
Keep Core private; do not expose it publicly just to connect an assistant.

### Build locally before publication

From the repository root:

```bash
docker build -f services/mcp/Dockerfile -t gamehub-mcp:local .
```

Use `gamehub-mcp:local` in place of the GHCR image in your client configuration.
Local builds require the checkout and download build dependencies. The resulting image does not.
The Dockerfile accepts the same `VERSION`, `REVISION`, and `SOURCE_REPOSITORY` build arguments
as the release workflow. License and dependency notices are stored under `/app/legal`.

## Run from source

Source-based development requires a matching checkout and the stable .NET 10 SDK on the machine
running the MCP process. For a client that accepts an `mcpServers` JSON object:

```json
{
  "mcpServers": {
    "gamehub": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "/absolute/path/to/gamehub/services/mcp/GameHub.Mcp.csproj",
        "--no-launch-profile"
      ],
      "env": {
        "Core__BaseUrl": "http://localhost:5001",
        "Core__ApiKey": "replace-with-the-permitted-api-key"
      }
    }
  }
}
```

Replace the path, URL, and key yourself in the client's private configuration.
The source-based address is relative to the computer running the MCP process.

## Check the connection

Restart or reconnect the assistant after changing its setup. For the Docker setup, check that
Docker is running, the image has been pulled, and Core is reachable from the container.

Ask the assistant to list tools, then try:

- “How many games are in my library?”
- “Find an unplayed RPG.”
- “Show details for this game.”
- “Pick a random game from my collection.”

The tools are `search_library`, `get_game`, `get_random_game`, `get_statistics`, `list_tags`,
`set_play_status`, and `set_rating`. The last two modify your collection;
review the intended game and value before asking for a change.

For authentication or reachability failures, check the Core URL and permitted authentication
mode. See [API overview](api.md) and [Troubleshooting](troubleshooting.md).

The Docker image supports explicit user login and the separate API-key compatibility mode.
User credentials preserve Core's guarded tenant/session boundaries; they never enable guarded
mode themselves. Remote Streamable HTTP transport remains separate future work.
