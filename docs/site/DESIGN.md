---
version: alpha
name: GameHub handbook
description: A readable field guide for collecting games and operating GameHub.
colors:
  primary: "#2457c5"
  background: "#fbfcfe"
  text: "#18263d"
  muted: "#52627b"
  border: "#d5deec"
  focus: "#b74c17"
  darkBackground: "#152033"
  darkText: "#e5ecf7"
  darkPrimary: "#9bbcff"
typography:
  sans:
    fontFamily: "Avenir Next, Avenir, Segoe UI, sans-serif"
    lineHeight: "1.75"
  heading:
    fontFamily: "Trebuchet MS, Avenir Next, sans-serif"
  mono:
    fontFamily: "SFMono-Regular, Consolas, monospace"
rounded:
  DEFAULT: "0.5rem"
spacing:
  page-max: "1160px"
  section-gap: "4rem"
omitted:
  - section: components
    reason: Docusaurus owns navigation, menus, search, code blocks, and article behavior.
---

# GameHub handbook design

## Overview

The visual reference is a well-organized game shelf and the practical handbook beside it.
The homepage's blue book-spine rules organize four reader tasks. Article pages prioritize
reading and predictable navigation. This is an English content site for self-hosters, players,
and contributors; no specific geographic market is assumed. Typical use is a desktop beside
a terminal or a phone while following setup instructions.

Scope is `docs/site/` only. The authenticated Next.js application retains its existing design.
Canonical runtime tokens live in `src/css/custom.css`; this document records their roles and
rationale. Docusaurus owns shared interaction primitives. Avoid a promotional landing-page
register, animated dashboards, giant decorative statistics, and dense dark-only code manuals.

## Colors

Blue marks navigation and handbook sections. Light paper and navy text keep long guides readable.
Burnt-orange focus rings are distinguishable from link color. Dark mode uses navy surfaces,
pale-blue links, and light text without changing hierarchy. The global scrollbar baseline uses
visible muted thumbs, hover and active colors, and system colors in forced-color mode.

## Typography

Rounded Trebuchet headings recall printed game handbooks; Avenir/system body stacks keep reading
familiar. Fonts are installed-system fallbacks, so no external font request or late font swap is
required. Code uses SFMono/Consolas. Article text stays within 80 characters per line.

## Layout

The homepage has one readable introduction and two shelves of task links. At 650px these become
a single column and full-width actions. Docusaurus handles its responsive article sidebar and
table of contents. Reserve the 32px brand mark and scrollbar gutter; never clip long code or tables.

## Elevation & Depth

Use borders and spacing for hierarchy. The navbar has a border instead of a shadow. Static
homepage sections are links with a quiet hover surface; no floating cards or decorative blur.

## Shapes

Use the Docusaurus control radius of 0.5rem. Section links have straight book-spine rules.
Keep navigation recognizable rather than replacing built-in controls with custom lookalikes.

## Components

Keep Docusaurus's version dropdown, search, mobile drawer, theme toggle, breadcrumbs, and code
copy controls. Links and buttons retain pointer, hover, active, and visible keyboard focus.
Search uses the locally generated index; no remote search account or credentials are required.
The Next banner communicates unreleased content; older-version banners communicate release age.
Reduced-motion preferences disable decorative transitions. There are no forms, account actions,
or application data mutations on this surface.

## Do's and Don'ts

- Do preserve reading width, nested link behavior, and visible focus in both themes.
- Do keep version and unreleased status understandable without relying on color.
- Do use the same navigation shell on all handbook pages.
- Don't publish internal agent history or local file links.
- Don't share application authentication state with this public static site.
