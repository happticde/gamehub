# Connect GOG

The recommended path uses the [connection helper](connection-helper.md) on a Mac with Chrome.
Sign in directly with GOG; do not enter your GOG password into GameHub.

## Connect and import

1. Open **Admin → Accounts → Connect GOG** (or **Reconnect GOG**).
2. Install the helper when prompted, or choose **Already installed**.
3. Select **Open Helper & sign in with GOG**.
4. Confirm that the native prompt shows your intended GameHub address.
5. Sign in to the intended GOG account in the browser opened by the helper.
6. Keep the GameHub dialog open until it confirms the credential is saved, then select **Done**.
7. Open **Admin → Sync → Refresh Platforms + Sync** and check a known GOG game in **Library**.

## Reconnect or use a manual path

Repeat **Reconnect GOG** when the session expires. Under **Having trouble? Advanced connection
options**, the dialog also offers supported manual handoff and credential input.
Use the GOG username belonging to the captured session. A wrong account or expired
`gog-al` cookie can cause a rejected or empty import.

If login succeeds but GameHub receives nothing, follow [helper troubleshooting](troubleshooting.md#the-connection-helper-does-not-finish).
