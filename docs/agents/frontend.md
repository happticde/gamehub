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
- The public OIDC Back-Channel Logout route is server-to-server only. It accepts exactly one bounded
  form `logout_token`, forwards it to Core with the BFF service key, returns only bodyless
  200/400/503 outcomes, and never reads or clears browser cookies. Core owns all token validation,
  `sub`/`sid` consistency, hashed provider-session mapping, replay protection, and durable local
  session-family invalidation.
- The Admin Security tab manages active GameHub session families through `/api/core/auth/sessions`.
  The client receives only local session ID, creation time, and current state. Other-family actions
  stay on the same-origin BFF; current sign-out reuses the native logout form. Tokens remain in
  HttpOnly cookies, and no provider, device, user-agent, or IP metadata enters browser state.
- Protected page navigation live-checks the current family through Core before rendering and keeps
  the existing single refresh attempt. A final Core `401` converges through
  `/api/auth/session-ended`, which only clears GameHub cookies and redirects to the fixed login
  notice. Browser-side Core calls use `fetchAuthenticated()` and react only to the BFF-owned final
  `401` marker; network failures and `403`/`404`/`429`/`5xx` never trigger this path.
- Operator readiness is intentionally not exposed to Client Components. The secret-free status API
  is an operator API-key endpoint, not a tenant-facing admin dashboard.
- The Admin Accounts tab uses `PlatformConnectAssistant` for PlayStation, GOG and Epic. Keep the
  shared macOS helper installation separate from provider-specific capture/save logic. Download
  URLs are fixed same-origin build artifacts for Apple Silicon/Intel, and installation confirmation
  is shared only in page state. Technical fallbacks stay inside the assistant; credential-save
  errors must remain visible inside the dialog. Stored credentials do not prove a successful import.
- Helper launches choose a fresh dynamic loopback status port per attempt and pass it as non-secret
  `statusPort` metadata. Poll that exact port; do not restore the shared fixed slot for new launches.
  Ship frontend and helper updates together. Origin/launch-ID validation remains mandatory.
- Browser mutation routes use `rejectUnsafeBrowserRequest()` before cookie use, body reads, or upstream work. `PUBLIC_ORIGIN` is the trusted exact browser origin (or configured OIDC callback origin outside Compose), never a Host/forwarded-header decision. Back-channel logout remains outside this browser-only boundary. Protocol-handler launches require native confirmation of the destination before any provider capture or loopback status server starts.
- The active physical-capture UI is manual-first via the existing IGDB enrichment search. It must
  debounce requests, reject stale responses, require explicit result and PS3/PS4/PS5 selection, and
  write only from the final confirmation. IGDB platform data is a hint only. Barcode, typed GTIN,
  tenant-local GTIN candidates, and product-catalogue suggestions remain outside the active UI.

## Mobile-First Conventions (2026-03-26)
- **Viewport**: Always use `userScalable: false` in `layout.tsx` to prevent iOS input zoom.
- **Filtering**: On mobile, use a `Dialog` (modal) for dense filtering options to maximize vertical screen space.
- **Media**: Prefer lazy-loaded, touch-native carousels (`ScreenshotCarousel.tsx`) with CSS scroll-snap over long vertical lists of images.
- **Breakpoints**: Use `md:` for desktop/tablet switches. Mobile is the default (hidden elements are `hidden md:block`).
