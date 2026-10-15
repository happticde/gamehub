# Connect platforms

GameHub stores platform credentials encrypted in Ingest. Authenticated browser operations go
through the Next.js BFF and Core; the browser receives configuration status rather than stored
plaintext credentials. Each account manages its own connections.

| Platform | Normal connection | What to verify |
| --- | --- | --- |
| Steam | Steam Web API key and 64-bit Steam ID | Correct account, provider visibility, successful retrieval |
| GOG | Guided local browser helper or manual supported credential input | Correct GOG account and a fresh session |
| PlayStation | Guided local Chrome session or manual supported cookie input | Correct account and complete required session cookies |
| Epic | Guided helper, authorization code, or manual refresh-token fallback | Code validity and successful stored retrieval |

## Guided local helper

The GOG, PlayStation, and Epic assistant can launch the shared local helper. The existing guided
installer is supplied for macOS Apple Silicon and Intel. Install the package matching your Mac
and the GameHub frontend release; follow the assistant's installation and platform-login steps.
Other environments can use documented manual paths rather than assuming macOS protocol-handler
support exists there.

The helper opens a visible provider login and hands the result back to the specific GameHub
origin and launch attempt. Sign in yourself. Never paste captured cookies, authorization codes,
or helper payloads into an issue or chat. Retry through the assistant so it creates a fresh
launch instead of reusing an old browser callback.

Use [Platform connect helpers](../platform-connect-helpers.md) for installation, browser,
loopback-port, origin, and recovery details. The [helper contract](../gog-connect-helper-contract.md)
describes the technical boundary for all supported helper payloads.

## After saving credentials

Check the connection status, trigger retrieval, and inspect its outcome. Then synchronize
Ingest's raw records into Core. Credential storage, retrieval, synchronization, and IGDB
enrichment are separate stages; success at one stage does not guarantee success at the next.

Deleting a credential disables sync first. Core only re-enables it after verifying that another
supported credential remains available. A provider outage can therefore leave sync disabled
until verification succeeds.

## Expired or rejected credentials

Reconnect through the assistant or replace the credential through the supported manual flow.
Respect cooldowns and inspect sanitized retrieval history before forcing repeated calls.
For Epic, read [Stored retrieval operations](../epic-stored-retrieval-operations.md).

## CLI and direct API access

The [CLI](cli.md) and direct Ingest API remain useful for local compatibility ingestion.
An API key cannot choose arbitrary tenants and does not bypass guarded production policies.
Use the authenticated frontend path for provisioned accounts.
