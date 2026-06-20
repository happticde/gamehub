# Frontend Agent Context

## 🧠 Developer Context
- The user is **new to React and TypeScript**.
- When writing frontend code, **explain concepts clearly, use best practices, and break down complex React/Next.js paradigms** (e.g., Server Components vs Client Components, hooks, state management).
- Write highly readable, well-commented TypeScript code to assist in their learning journey.

## Tech Stack (Frontend)
- Next.js (React), TypeScript, Tailwind CSS, shadcn/ui.
- Components are in `frontend/components/`.
- Styling via `frontend/app/globals.css`.

## Conventions
- Use functional components and hooks.
- Prefer Server Components (`'use server'`) where possible; use Client Components (`'use client'`) only when interaction is needed.
- Follow shadcn/ui patterns (manual file creation, no `npx`).
- Authenticated Core calls use the Next.js BFF route `/api/core/*`. Access and rotating refresh
  tokens stay in HttpOnly cookies; browser components must never read or persist them directly.
- React Server Components call `fetchCoreApi()` so the server forwards the same access token
  without creating a cross-tenant shared cache entry.

## Mobile-First Conventions (2026-03-26)
- **Viewport**: Always use `userScalable: false` in `layout.tsx` to prevent iOS input zoom.
- **Filtering**: On mobile, use a `Dialog` (modal) for dense filtering options to maximize vertical screen space.
- **Media**: Prefer lazy-loaded, touch-native carousels (`ScreenshotCarousel.tsx`) with CSS scroll-snap over long vertical lists of images.
- **Breakpoints**: Use `md:` for desktop/tablet switches. Mobile is the default (hidden elements are `hidden md:block`).
