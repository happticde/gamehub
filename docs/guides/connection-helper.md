# Install the connection helper

The helper lets you sign in with GOG, PlayStation, or Epic in a local browser.
Install it on the Mac where you browse GameHub. Your GameHub server can run elsewhere.
Steam does not need this helper.

## Requirements

- Desktop macOS, Apple Silicon or Intel.
- Google Chrome installed on that Mac.
- Access to the GameHub **Admin → Accounts** page.

The guided installer is not currently supplied for Windows, Linux, or mobile browsers.
Advanced manual connection options are available in the platform dialogs where supported.

## Install once

1. Open **Admin → Accounts** and select **Connect** on GOG, PlayStation, or Epic.
2. In the assistant, download the helper matching your Mac. Check **Apple menu → About This Mac**
   if you are unsure whether it uses Apple Silicon or Intel.
3. Extract the archive and open **Install GameHub Helper.command**.
4. Complete any macOS approval needed to run the downloaded installer and CLI.
5. Return to GameHub and choose **Continue after installation**.
6. Choose **Open Helper & sign in with …**.
7. Confirm the exact GameHub address in the native prompt, then complete provider login.
8. Wait for GameHub to confirm the credential was stored and choose **Done**.

The installer places the CLI in `~/Library/Application Support/GameHub/Helper` and registers
`~/Applications/GameHub Helper.app`. Go is not required.
The packages are not Developer ID signed or notarized; local signing does not remove macOS
download approval requirements.

## Connect another platform

Use **Already installed** when the same helper is present. The installation acknowledgment
is shared while the Accounts page is open; it does not automatically detect the installed app.
Each connection attempt has its own status port. Start retries from a fresh assistant attempt.

Closing the dialog stops GameHub waiting for the result, but does not terminate the local
helper or login browser. Wait for an in-flight credential save to finish.

## Update the helper

Use the download offered by your installed frontend release. Reinstall when upgrading GameHub
if the release requires a matching helper. An older helper can disagree with a newer frontend.
Do not reuse launch links or captured results across attempts.

## If installation or login fails

For macOS approval, retry, browser handoff, or saved-credential errors, use
[Connection helper troubleshooting](troubleshooting.md#the-connection-helper-does-not-finish).
