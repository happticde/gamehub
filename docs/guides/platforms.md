# Connect platforms

Open **Admin → Accounts** to add or reconnect a platform. Each connection belongs to the
signed-in account. GameHub stores credentials encrypted and shows connection status;
it does not display stored secrets back to you.

| Platform | What you need | Instructions |
| --- | --- | --- |
| Steam | Steam Web API key and 64-bit Steam ID | [Connect Steam](steam.md) |
| GOG | GOG login; guided helper or advanced manual connection | [Connect GOG](gog.md) |
| PlayStation | PlayStation login; Google Chrome for the guided path | [Connect PlayStation](playstation.md) |
| Epic Games | Epic login; guided helper or authorization-code fallback | [Connect Epic Games](epic.md) |

## After connecting

Open **Admin → Sync → Refresh Platforms + Sync**, wait for completion, and check a known title
in **Library**. A saved connection is not proof of a successful or complete import.

Reconnect if the provider rejects an expired session. For temporary outages and cooldowns, check
[Troubleshooting](troubleshooting.md) before forcing another request.

## Remove a connection

Use the removal control on that provider's **Accounts** card and confirm the provider.
Removal disables platform sync first. GameHub rechecks remaining supported connections before
enabling sync again. Removing a credential is not a command to delete your existing collection.

## Choose the right helper

The guided installer currently supports macOS Apple Silicon and Intel with Google Chrome.
Install it on the computer where you use the browser, even when GameHub runs on another server.
See [Connection helper](connection-helper.md) for installation and recovery.
Other systems can use the advanced manual options where available; a guided installer is not
provided for them.

Never put cookies, provider codes, or helper results into issues or support messages.
