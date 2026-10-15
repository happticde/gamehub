# CLI reference

`gamehub-cli` is the Go client for compatibility ingestion and the local account connection
helper. It is separate from the MCP server. Download the archive matching your operating system
and architecture from a published GitHub Release, verify its checksum, and check:

```bash
gamehub-cli --version
gamehub-cli --help
gamehub-cli ingest --help
gamehub-cli connect --help
```

See [Release artifacts](../ci-and-releases.md#retrieve-artifacts) for archive names and checksums.
Development builds explicitly report `dev`.

## Global options

| Option | Default / meaning |
| --- | --- |
| `--backend-url` | `http://localhost:5000`; Ingest base URL |
| `--api-key` | Compatibility API authentication; can also be supplied in the CLI config |
| `--version` | Display the embedded release version |
| `--help` | Inspect the installed command's options |

The CLI's normal ingestion commands do not bypass guarded production tenant delegation.
Use the authenticated web connection flow for provisioned accounts.

## Compatibility ingestion commands

| Command | Required provider input |
| --- | --- |
| `ingest steam` | `--key` and `--id` (Steam Web API key and Steam ID64) |
| `ingest gog` | `--user` and `--token` (GOG username and supported cookie) |
| `ingest psn` | `--cookies` (supported JSON cookie shape) |

Use each command's `--help` for the exact installed schema. These inputs are sensitive:
command-line arguments can remain in shell history or process listings. Prefer the guided
connection flow when available and never paste an invocation containing real credentials into
a support report.

The CLI reads an optional `~/.gamehub.yaml` with keys such as `api-key`, `steam.key`,
`steam.id`, `gog.token`, and `psn.cookies`; provider command flags take precedence over their
corresponding stored values. Keep that operator-managed file outside Git and restrict its
filesystem permissions. See the installed command's help and source for supported settings;
the Ingest target itself is selected with `--backend-url`.

## Connection helpers

`connect gog`, `connect psn`, and `connect epic` guide a visible local provider login. Handoff
mode serves one captured result on loopback for the permitted GameHub origin. The assistant
normally supplies the appropriate launch parameters; do not copy an old launch URL into a new
attempt.

The `helper` command installs/uninstalls the macOS protocol handler and handles
`gamehub-helper://` launches. Prefer the installer embedded in your frontend release so the
handler and frontend agree on the protocol and status-port behavior.

For browser debugging and manual fallbacks, see [Platform connect helpers](../platform-connect-helpers.md).
