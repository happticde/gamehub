---
name: Add shadcn Component
description: How to manually add a shadcn/ui component to this project (npx is unavailable)
---

# Add shadcn Component

Since `npx` is not available on the host, shadcn components must be created manually. Follow this checklist exactly.

## 1. Check Existing Components for Patterns

Read 2-3 existing components in `frontend/components/ui/` (e.g., `dialog.tsx`, `select.tsx`, `popover.tsx`) to observe:
- Import style
- Export style
- Styling patterns

## 2. Create the Component File

File: `frontend/components/ui/<component-name>.tsx`

### Import Pattern

```tsx
"use client"

import * as React from "react"
import { ComponentName as ComponentNamePrimitive } from "radix-ui"

import { cn } from "@/lib/utils"
```

> **CRITICAL**: Import from `"radix-ui"` with destructuring. Do NOT use sub-package paths like `"radix-ui/react-dialog"` — those will cause lint errors.

### Component Pattern

Use **function declarations** (not `const`), with `data-slot` attributes:

```tsx
function ComponentName({
  className,
  ...props
}: React.ComponentProps<typeof ComponentNamePrimitive.Root>) {
  return (
    <ComponentNamePrimitive.Root
      data-slot="component-name"
      className={cn("...", className)}
      {...props}
    />
  )
}
```

### Export Pattern

Named exports at bottom of file:

```tsx
export { ComponentName, ComponentNameTrigger, ComponentNameContent }
```

## 3. Styling Conventions

- Use `cn()` from `@/lib/utils` for all className merging
- Use Tailwind classes with the project's design tokens (`bg-muted`, `text-muted-foreground`, `border-border`, etc.)
- Add animation classes: `data-[state=open]:animate-in`, `data-[state=closed]:animate-out`
- Include focus-visible ring: `focus-visible:ring-3 focus-visible:ring-ring/50`

## 4. Verify

```bash
docker exec -w /workspaces/gamehub/frontend gamehub_devcontainer-app-1 npm run build
```
