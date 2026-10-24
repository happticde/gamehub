# GameHub interaction contract

## Canonical UI map

| Capability | Canonical owner | Source of truth | Allowed variants | Verification |
| --- | --- | --- | --- | --- |
| Select/Listbox | `components/ui/select.tsx` | Shared primitive | Authored selectors | Keyboard and popup checks |
| Form | `components/ui/input.tsx`, `textarea.tsx`, `button.tsx` | Existing API contracts | Inline errors and duplicate-submit guards | Security tests and browser checks |
| Dialog | `components/ui/dialog.tsx` | Shared primitive | Modal with focus restoration | Escape and keyboard checks |
| Destructive confirmation | `components/ui/alert-dialog.tsx` | Existing mutation contracts | Explicit confirmation | Cancel and confirm checks |
| Search | `hooks/useDebouncedMetadataSearch.ts`, `lib/latest-request-gate.ts` | Request gate | Debounced remote search | Slow, stale, cleared, and failed request checks |
| Authentication | `lib/authenticated-fetch.ts`, server BFF routes | Server authentication policy | BFF-confirmed final 401 | Security tests |
| CRUD | Existing Core routes and their owning panels | Server API policy | Server-confirmed writes with retry | Security and interaction tests |

## Requests and state

Initialize loading state for the first request. Refresh from user actions or timer events.
Commit fetched data only from asynchronous completion callbacks.
When an input changes, derive its visible state from the current input or adjust its
owned state during render behind an explicit previous-input guard.
Effects subscribe to external work and clean it up. They do not synchronously reset UI state.
An old response must not replace a newer search result or appear after search is cleared.

## Existing workflows

Merge requires a selected target and a final confirmation. Search does not write data.
Physical capture requires a confirmed IGDB result and PS3, PS4, or PS5 selection.
Provider credentials are saved through the existing BFF. Capture does not prove a successful save.
Keep the same controls and state behavior on narrow screens and in all existing themes.
