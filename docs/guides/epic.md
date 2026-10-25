# Connect Epic Games

Use the [connection helper](connection-helper.md) to sign in directly with Epic.
The guided installer currently supports macOS with Chrome.

## Connect and import

1. Open **Admin → Accounts → Connect Epic Games** (or **Reconnect Epic Games**).
2. Install the helper when prompted, or choose **Already installed**.
3. Select **Open Helper & sign in with Epic Games**.
4. Confirm that the native prompt shows your intended GameHub address.
5. Sign in to Epic in the browser that opens and leave it open until GameHub receives the result.
6. Wait for the saved-credential confirmation, then select **Done**.
7. Open **Admin → Sync → Refresh Platforms + Sync**.
8. Check a known Epic game in **Library**.

Epic's captured authorization code is short-lived. Start a fresh connection if it expires.
Do not reuse a result from an old attempt.

## Manual connection and failures

The dialog's **Having trouble? Advanced connection options** provides authorization-code and
supported refresh-token fallbacks. Treat both as secrets.

A saved connection can still fail during library retrieval. If the provider reports expired
authorization, reconnect. For cooldowns, wait until retry is allowed. For an unexpected provider
response, keep existing data and report only the sanitized status and timestamp; do not upload
raw Epic responses. See [Import failures](troubleshooting.md#import-fails-or-is-rate-limited).
