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
- Guarded multi-user state is server-only configuration (`MULTI_USER_ENABLED`) and defaults false.
  The BFF validates auth, Secure-cookie, and OIDC prerequisites; it never accepts enablement from
  browser state or provider claims. Core remains authoritative and validates mirrored settings.
- During disabled-mode rollout preparation, the local form is a first-link compatibility bridge.
  A successful unlinked password check stores a short-lived HttpOnly link-purpose cookie,
  immediately starts OIDC, then clears all session cookies after linking. Active mode hides the
  local form and linked accounts use provider login only.
- `OIDC_PROVISIONING_MODE` is server-only and defaults to `Disabled`; unknown values fail closed.
  Active-mode login remains the normal OIDC button for linked and personal-JIT accounts. No
  provider token or provisioning decision reaches a Client Component.
- External deletion uses sealed `delete`/`delete-retry` OIDC transactions. The BFF requests fresh
  provider login, obtains a restricted Core token, and immediately consumes it server-to-server;
  provider and deletion tokens never enter JavaScript, JSON responses, or `localStorage`.
- Operator readiness is intentionally not exposed to Client Components. The secret-free status API
  is an operator API-key endpoint, not a tenant-facing admin dashboard.
- Physical-catalogue suggestions are transient untrusted hints returned through `/api/core/*` and
  displayed separately from tenant-local confirmed GTIN mappings. Only explicit buttons may seed
  search or manual title/platform fields; they must clear any selected game and never bypass the
  final confirmation write. Provider disablement or failure is an empty-hint manual fallback.

## Mobile-First Conventions (2026-03-26)
- **Viewport**: Always use `userScalable: false` in `layout.tsx` to prevent iOS input zoom.
- **Filtering**: On mobile, use a `Dialog` (modal) for dense filtering options to maximize vertical screen space.
- **Media**: Prefer lazy-loaded, touch-native carousels (`ScreenshotCarousel.tsx`) with CSS scroll-snap over long vertical lists of images.
- **Breakpoints**: Use `md:` for desktop/tablet switches. Mobile is the default (hidden elements are `hidden md:block`).
