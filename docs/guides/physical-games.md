# Add physical games

You can record physical PS3, PS4, and PS5 copies alongside digital ownership.
The host must configure IGDB for game search.

## Add a game

1. Open `/physical/add`.
2. Search IGDB for the title and select the correct game.
3. Select **Continue** to choose the physical platform.
4. Explicitly choose **PS3**, **PS4**, or **PS5**; a search result's platform information is only a hint.
5. Select **Review selection**.
6. Check the game title, IGDB ID, and physical platform.
7. Select **Confirm and add physical game** and wait for the save to complete.

Searching and selecting do not create a copy; the final confirmation does.
If you already own the same IGDB game, GameHub can add the copy to that game.
Multiple physical copies of the same title on the same platform are supported.

## Add or remove copies on a game

Open its detail page and use the physical-collection controls to add a supported copy
or remove the selected copy. Review the platform and copy before confirming removal.
Deleting a physical copy does not remove digital ownership.

## Supported input

The active flow is title search and explicit confirmation. Barcode scanning and product-catalogue
capture are not available in this workflow. If a search fails, check
[IGDB troubleshooting](troubleshooting.md#covers-or-descriptions-are-missing-or-wrong).

Physical copies are included in [JSON backups](backup-restore.md).
