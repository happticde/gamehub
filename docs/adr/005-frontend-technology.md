# ADR 005: Frontend Technology Stack Choice

**Date**: 2026-03-08
**Status**: Proposed
**Context**:
The GameHub backend is maturing, and the architecture involves autonomous REST/JSON APIs running in .NET 10. To provide a rich, interactive, and premium user experience for viewing the unified library, triggering manual merges/splits, and searching games, we need to select a frontend technology stack. The backend currently uses an API Key for authentication and is designed to easily migrate to JWT-based identity if a user-facing frontend is attached.

## Considered Alternatives

We evaluated the following modern frontend technologies:

1. **Next.js (React)** 
   A Meta-framework for React that supports robust Server-Side Rendering (SSR), Static Site Generation (SSG), and elegant API routes (a perfect Backend-For-Frontend).
2. **Blazor WebAssembly / Auto**
   Microsoft's UI framework that allows writing client-side code in C# instead of JavaScript/TypeScript.
3. **Vue.js (with Nuxt)** 
   A progressive framework similar to React but often praised for its approachability and simplified reactivity model.
4. **SvelteKit** 
   A compiler-based framework that results in extremely small bundle sizes and high performance with excellent Developer Experience (DX).

---

## Comparison Table

| Feature / Aspect | Next.js (React) | Blazor WebAssembly | Vue.js (Nuxt) | SvelteKit |
| :--- | :--- | :--- | :--- | :--- |
| **Language** | TypeScript / JavaScript | C# | TypeScript / JavaScript | TypeScript / JavaScript |
| **Ecosystem & Community** | **Massive**. Largest pool of UI libraries (e.g., shadcn/ui, Tailwind). | Small but growing. Mostly enterprise-focused components. | Large. Good UI libraries but smaller than React's. | Growing rapidly, but still smaller than React/Vue. |
| **Backend Integration** | Excellent BFF (Backend-For-Frontend) capabilities to hide API keys from the client. | Native GRPC/HTTP integration is great since the backend is C#. | Nuxt provides Nitro for good BFF capabilities. | Great endpoints formulation via `+server.ts`. |
| **Learning Curve** | Moderate (React hooks, Server Components can be complex). | Low for C# devs, high if unfamiliar with the Web/WASM quirks. | Low (very approachable template syntax). | Very Low (Close to vanilla HTML/JS). |
| **Performance** | Excellent (RSC, App Router optimizations). | Fair (Requires downloading heavy .NET runtime on first load). | Excellent. | **Best-in-class**. |

---

## Decision

We propose selecting **Next.js (React) with TypeScript** as the frontend framework.

### Why Next.js (React)?
1. **Ecosystem & UI Quality:** React has the most comprehensive component ecosystem. We want to achieve a "premium" and highly aesthetic design easily. Tools like Framer Motion (for dynamic animations), Tailwind CSS, and headless UI libraries (e.g., Radix UI, shadcn/ui) are incredibly mature here and will allow us to achieve "wow" factor visuals faster than any other stack.
2. **Backend-For-Frontend (BFF):** Next.js API routes (or Server Actions) allow us to securely proxy our C# backend APIs. This means the client browser never directly sees the `X-Api-Key` used for the Ingest and Core services. Next.js handles the secret server-side and serves public data to the client.
3. **Auth Readiness:** Moving to a robust user system in the future (NextAuth.js / Auth.js) fits seamlessly into an evolving JWT architecture that the GameHub `.NET` backend is already primed for.

### Why not the others?
- **Blazor:** While the backend is built in C#, Blazor WebAssembly generally suffers from larger initial payload sizes. Furthermore, the ecosystem for highly polished, animated, consumer-facing UIs is significantly trailing behind React's dynamic ecosystem (most Blazor UI kits look very "enterprise/admin" focused).
- **SvelteKit & Vue:** Both are excellent choices with arguably better native Developer Experiences than React. However, they lack the sheer volume of plug-and-play premium UI components (like `shadcn/ui` and advanced `Framer Motion` recipes) that React possesses.

## Consequences
- **Pros:** 
  - Access to the industry's best UI component libraries to deliver a stunning visual experience.
  - Highly secure BFF pattern to hide the ASP.NET API Keys from the end user's browser.
  - Vercel/Docker deployment agility.
- **Cons:** 
  - Team must context switch between C# (.NET backend) and TypeScript (React frontend).
  - React Server Components (App Router) introduce a learning curve for data fetching methodologies.
