# ADR 006: Frontend UI Concepts & Architecture

**Date**: 2026-03-08
**Status**: Accepted
**Context**:
With the GameHub Next.js frontend now actively serving the Unified Library and Ingest Dashboard, we have established several core architectural UI patterns. This document serves to record the basic foundational concepts that define how our frontend is constructed, ensuring consistency for future feature development.

## 1. Component Architecture (shadcn/ui & Tailwind CSS)

We utilize a "headless" component architecture using **radix-ui primitives** combined with **shadcn/ui**. 

- **No NPM Package Lock-in:** Instead of installing a massive generic UI library (like Material UI or Bootstrap), we copy the source code of specific components (e.g., `Button`, `Select`, `Popover`) directly into our `frontend/components/ui` folder. This grants us 100% control over the markup and styling.
- **Styling:** We use **Tailwind CSS** for all styling. This relies on utility classes (`flex`, `items-center`, `text-muted-foreground`) to style components directly in the JSX, preventing sprawling external CSS files and ensuring styles are purged cleanly during production builds.
- **Aesthetics:** We applied the "New York" preset with a default "Dark Mode" styling, leveraging sleek zinc/slate grays (`bg-background`, `bg-card`) and subtle border radii for a premium, modern feel. 

## 2. Server Components vs Client Components

Next.js 15 uses the App Router, which fundamentally changes how React components are rendered.

### Server Components (Default)
Most of our pages (like `app/library/page.tsx` and `app/ingest/page.tsx`) are **Server Components**.
- They fetch data directly from the C# backend on the server *before* sending any HTML to the browser.
- They cannot use React state (`useState`), effects (`useEffect`), or browser APIs.
- They are incredibly fast, SEO-friendly, and secure (since they can hold secrets without leaking them to the browser).

### Client Components (`"use client"`)
When we need interactivity, we use **Client Components**.
- Files like `LibraryFilters.tsx`, `SyncTriggerButton.tsx`, and `Navbar.tsx` begin with the `"use client"` directive.
- They handle `onClick`, `onChange`, and React Hooks (`useState`, `useTransition`, `usePathname`).
- We keep Client Components as low in the tree as possible to maximize server-rendering performance.

## 3. "URL as State" Pattern

For dynamic features like searching, filtering (Tags, Platforms), and Pagination, we do **not** rely purely on hidden React `useState`. Instead, we push all state into the browser's URL query string.

- **Example URL:** `/library?search=witcher&tags=RPG&platforms=Steam&page=1`
- **Why?**
  1. **Shareable:** You can bookmark or share the URL and instantly return to the exact same filtered view.
  2. **Server-Side Rendering:** Because the parameters are in the URL, the Next.js *Server Component* (`page.tsx`) can read them instantly, fetch the exact subset of games from the C# backend, and render the correct HTML on the very first paint.
  3. **Implementation:** Client components (`LibraryFilters.tsx`) use `useSearchParams` and `useRouter` to elegantly append or delete parameters (like arrays of selected platforms) via `params.append("platforms", "Steam")` and `router.push()`.

## 4. Subverting Security / API Proxying

The Next.js frontend runs on port `3000`, but our C# APIs run on ports `5000` (Ingest) and `5001` (Core).

- **Proxy Writes:** To avoid Cross-Origin Resource Sharing (CORS) errors or exposing internal Docker networking to the user's browser, we utilize Next.js `rewrites()` in `next.config.ts`.
- **Flow:** When a client component (like the `SyncTriggerButton`) wants to trigger a backend task, it calls `.fetch("/api/core/library/sync")`. The Next.js Node server intercepts this request and proxies it invisibly to `http://host.docker.internal:5001/api/library/sync`.
- This creates a secure "Backend for Frontend" boundary.
