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

The current MCP client uses a Core API key. It is a compatibility integration, not an OIDC
login client for provisioned accounts. Do not assume it can access every guarded deployment.

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

The Docker image retains the existing API-key authentication. It does not add OIDC login or
enable access to guarded multi-user library endpoints. User authentication and remote Streamable
HTTP transport are planned follow-up work.
