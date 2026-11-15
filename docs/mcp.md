# GameHub MCP Server

For the Docker image and current Codex setup, use the maintained [MCP guide](guides/mcp.md).
The source-based examples below require a stable .NET 10 SDK and a matching checkout.
Library tools support user authorization and rotating renewal with `Mcp__AuthenticationMode=User`.
Explicit device login, protected-volume storage, launch configuration and failure recovery are
described in the [MCP guide](guides/mcp.md#staged-device-login-and-credential-volume).
The examples below retain the separate API-key compatibility mode; guarded end-user rollout waits for #75.

Use Admin **Security** to inspect and individually revoke owned assistant connections.
See [connection management](mcp-connections.md) for the browser boundary and lifecycle behavior.

The separate Core [browser consent boundary](mcp-browser-consent.md) is default-off and staged
for the upcoming user-authenticated client. Do not enable it for end users before release gate #75.

The GameHub MCP server exposes your game library to any AI assistant that supports the [Model Context Protocol](https://modelcontextprotocol.io/). This lets you query and manage your library using natural language.

## Available Tools

| Tool | Description |
|---|---|
| `search_library` | Search games by title, genre, platform, or play status |
| `get_game` | Full details for a specific game by ID |
| `get_random_game` | Random game pick (supports filters) |
| `get_statistics` | Library stats: platforms, genres, ratings, play status |
| `list_tags` | All genre tags in the library |
| `set_play_status` | Update play status (Unplayed/Playing/Completed/etc.) |
| `set_rating` | Set personal star rating (1–5) |

---

## Prerequisites

- **.NET 10+ SDK** installed on the host machine
- The **GameHub Core service** must be running (default: `http://localhost:5001`)
- If auth is enabled, your `Core:ApiKey` config value must match `Authentication:ApiKey` in Core's `appsettings.json`

---

## Client Configuration

### VS Code / GitHub Copilot

Create a private `.vscode/mcp.json` using this stdio configuration:

```json
{
  "servers": {
    "gamehub": {
      "type": "stdio",
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "${workspaceFolder}/services/mcp/GameHub.Mcp.csproj",
        "--no-launch-profile"
      ],
      "env": {
        "Core__BaseUrl": "http://localhost:5001",
        "Core__ApiKey": "your-api-key-here"
      }
    }
  }
}
```

### Claude Desktop

Add to `~/Library/Application Support/Claude/claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "gamehub": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "/path/to/gamehub/services/mcp/GameHub.Mcp.csproj",
        "--no-launch-profile"
      ],
      "env": {
        "Core__BaseUrl": "http://localhost:5001",
        "Core__ApiKey": ""
      }
    }
  }
}
```

### Cursor

Add to `~/.cursor/mcp.json` (global) or `.cursor/mcp.json` (project-level):

```json
{
  "mcpServers": {
    "gamehub": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "/path/to/gamehub/services/mcp/GameHub.Mcp.csproj",
        "--no-launch-profile"
      ],
      "env": {
        "Core__BaseUrl": "http://localhost:5001"
      }
    }
  }
}
```

---

## Configuration

The MCP server reads these values from environment variables or `appsettings.json`:

| Key | Default | Description |
|---|---|---|
| `Mcp:AuthenticationMode` | `ApiKey` | `User` uses only the selected HTTPS issuer/resource/connection and credential volume; never falls back to an operator key |
| `Core:BaseUrl` | `http://localhost:5001` | URL of the GameHub Core service |
| `Core:ApiKey` | *(empty)* | API key — must match Core's `Authentication:ApiKey` |

Environment variables use double-underscore as separator: `Core__BaseUrl`, `Core__ApiKey`.

---

## Example Prompts

Once connected, you can ask your AI assistant:

- *"How many games do I own, and which platform has the most?"*
- *"Find me an unplayed RPG to try tonight."*
- *"Show me my top-rated games."*
- *"Mark The Witcher 3 as Completed."*
- *"Give me a random horror game."*
- *"What's the genre breakdown of my library?"*

---

## Architecture

```
AI Client (Claude/Cursor/VS Code)
  │  stdio (JSON-RPC 2.0)
  ▼
GameHub.Mcp (console app)
  │  HTTP REST
  ▼
GameHub Core (:5001)
  │  EF Core
  ▼
SQLite / PostgreSQL
```

The MCP server is a thin proxy: it forwards tool calls to the Core REST API and returns the raw JSON responses. All business logic stays in Core.

---

## Development

Build the MCP project inside the devcontainer:

```bash
docker exec -w /workspaces/gamehub gamehub_devcontainer-app-1 \
  dotnet build services/mcp/GameHub.Mcp.csproj --verbosity quiet
```

Run directly (outside Docker, with .NET SDK installed):

```bash
dotnet run --project services/mcp/GameHub.Mcp.csproj
```
