# Connect an AI assistant with MCP

The optional GameHub MCP server lets a compatible assistant search your collection, inspect games,
get statistics or a random pick, list tags, and update play status or personal ratings.

It runs as a local stdio process and connects to Core. It is separate from the platform connection
helper and is not needed to use the web application.

## Requirements

- A running Core service reachable from the assistant's computer.
- The .NET 10 SDK on that computer for the source-based launch below.
- The GameHub source checkout matching your installed release.
- Authentication accepted by the target Core deployment.

The current MCP client uses a Core API key. It is a compatibility integration, not an OIDC
login client for provisioned accounts. Do not assume it can access every guarded deployment.

## Configure your assistant

For a client that accepts an `mcpServers` JSON object, configure a stdio server:

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
The address is relative to the computer running the MCP process. Keep Core private; do not expose
it publicly just to make a remote assistant connect.

Clients using a `servers` object instead need their own equivalent stdio configuration.
Restart or reconnect the assistant after changing its setup.

## Check the connection

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
