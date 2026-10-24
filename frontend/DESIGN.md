# GameHub application design

GameHub helps a signed-in owner manage a game collection and platform connections.
Keep the existing cover-led library and compact admin panels when changing behavior.

## Runtime owners

- `app/globals.css` owns colors, spacing tokens, radius, and theme variants.
- `app/layout.tsx` owns Geist Sans and Geist Mono. The retro theme uses the mono face.
- `components/ThemeProvider.tsx` owns theme selection.
- `components/ui/` owns shared buttons, inputs, cards, dialogs, alerts, and popovers.
- `components/library/MetadataSearchResultCard.tsx` owns the shared IGDB result layout.

Use the existing light, dark, and retro variants. Use semantic tokens such as
`background`, `foreground`, `primary`, and `destructive` through the shared primitives.
Keep existing mobile filter dialogs and touch-friendly screenshot navigation.
Behavioral maintenance does not change the palette, typography, or page layout.

## Content and feedback

Keep labels short. Name the action and the affected game or platform.
Show failures beside the action. Keep entered data available for a retry.
Loading feedback must not move the primary controls.
See `UX-CONTRACT.md` for request and interaction rules.
