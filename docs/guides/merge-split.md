# Merge and split games

These actions correct how platform ownership is grouped. Create a [JSON backup](backup-restore.md)
before extensive cleanup.

## Merge duplicate games

Use this when two library entries should represent the same game.

1. Open the game you want to merge into another.
2. Select **Merge into…**.
3. Search for and select the target game you want to keep.
4. Review the source, target, platform entries, and any conflicts shown.
5. Select **Merge games** only when the grouping is correct.
6. GameHub opens the target game. Check its platform entries.

A matching display name alone is not enough: editions or similarly named games may be different.
Review conflicts instead of assuming every field can be kept from both entries.

## Split an incorrectly grouped platform entry

1. Open the game containing the misplaced entry.
2. Find that platform entry and select **Split**.
3. Check the displayed platform and external ID in **Split this platform entry?**
4. Select **Split into new game**.
5. Inspect the separate game and confirm that the other platform entries stayed on the original.

The correction guides later synchronization, so a normal sync does not simply regroup the entry.
Use **Cancel** if you selected the wrong platform record.

For a wrong cover on an otherwise correct grouping, use [Fix Match](correct-matches.md).
