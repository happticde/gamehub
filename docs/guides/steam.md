# Connect Steam

You need a Steam Web API key and your account's 64-bit Steam ID.
Steam does not require the local connection helper.

## Connect and import

1. Obtain your key from the [Steam Web API key page](https://steamcommunity.com/dev/apikey).
2. Find your account's Steam ID64. Use the numeric ID, not a profile nickname or custom URL.
3. Open **Admin → Accounts** and find **Steam**.
4. Enter the API key and Steam ID, then select **Store encrypted credential**.
5. Wait for the save confirmation.
6. Open **Admin → Sync → Refresh Platforms + Sync**.
7. After completion, open **Library** and check a game you own on Steam.

## If games are missing

Check that the ID identifies the intended account and that its Steam game details are visible
to the API. A successful connection save only confirms storage; the import checks provider access.
Inspect the provider status in **Admin → Sync**. If the key is rejected, replace it in **Accounts**.
For rate limits, wait for the displayed cooldown rather than repeatedly forcing refreshes.

See [Missing games](troubleshooting.md#games-are-missing) for the next checks.
