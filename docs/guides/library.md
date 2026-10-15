# Manage your collection

## Find games

Open `/library` to search, filter, and sort your unified collection. Filters include platform,
tags, developer, and publisher. Open a result to inspect its metadata and platform items.
Statistics at `/stats` describe the active tenant's library.

The global search opens matching game details directly. The library's random pick helps choose
a game from the current selection. On small screens, use the filter dialog to reach the same
filter controls without squeezing the game grid.

## Track progress and ratings

Set personal play status and a one-to-five-star personal rating on the game details page.
Provider playtime and your own status are different information: a game can have provider
playtime while still being marked unfinished. Manually chosen statuses are preserved according
to the personal tracking rules rather than inferred afresh on every sync.

Ignore entries you do not want in the normal collection views. Review the ignored selection
before assuming a game was removed by an import.

## Use tags

Genre tags can be supplied by metadata enrichment. Custom tags let you organize the collection
yourself. Automatic enrichment replaces its own genre assignments while preserving custom
assignments. Removing a custom tag definition removes its assignments; provider-created system
definitions are protected from deletion.

## Correct an IGDB match

Use **Fix Match** on a game with the wrong cover, title, or description. Search for the correct
IGDB entry, review it, and explicitly confirm the choice. An exact IGDB identifier can narrow
the search. Match correction changes the metadata identity; it does not move platform ownership
between unified games.

## Merge or split ownership

Merge combines platform records that should belong to one unified game. Split detaches a
platform record that was incorrectly grouped. Check the source, target, and platform identifier
before confirming. These manual links are durable and guide later synchronization.

Do not use a metadata correction to solve an ownership merge problem, or merge unrelated games
just because their display titles resemble each other.

## Add physical games

The active flow searches IGDB and requires you to choose a result and a supported physical
platform: PS3, PS4, or PS5. Review the final confirmation before saving. Multiple copies of the
same game on the same platform are supported.

A physical copy is separate from a digital platform item. Deleting a copy does not remove
digital ownership. Barcode and product-catalogue capture are not part of the active workflow;
see [ADR-014](../adr/014-human-confirmed-physical-game-capture.md).

## Protect your changes

Create regular [JSON backups](backup-restore.md), especially before bulk maintenance and
application upgrades. CSV is useful for analysis but cannot restore the full library.
