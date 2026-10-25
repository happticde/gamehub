# Connect PlayStation

The guided path needs macOS and Google Chrome on the computer where you browse GameHub.
The helper opens or reuses a dedicated PlayStation Chrome profile so you can sign in with Sony.

## Connect and import

1. Open **Admin → Accounts → Connect PlayStation** (or **Reconnect PlayStation**).
2. Install the [connection helper](connection-helper.md), or choose **Already installed**.
3. Select **Open Helper & sign in with PlayStation**.
4. Confirm your intended GameHub address in the native prompt and allow the browser to open the helper.
5. Complete Sony login, MFA, or captcha in the Chrome window that opens.
6. Leave that window and the GameHub dialog open until GameHub confirms the credential is saved.
7. Select **Done**, then **Admin → Sync → Refresh Platforms + Sync**.
8. Check a known PlayStation game in **Library**.

The normal helper starts the persistent Chrome profile automatically; you do not need to launch
Chrome with debugging flags for routine setup.

## If Sony blocks login

For a rate limit or blocked fresh session, wait rather than repeatedly starting new attempts.
Use the dedicated profile opened by the helper so its sign-in state can be reused.
If the saved session later expires, use **Reconnect PlayStation**.

Manual handoff and cookie options remain under **Having trouble? Advanced connection options**.
Use the exact GameHub browser origin for a manual handoff. Do not share captured cookies or diagnostic
payloads. See [helper troubleshooting](troubleshooting.md#the-connection-helper-does-not-finish).
