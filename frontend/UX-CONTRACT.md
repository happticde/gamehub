# GameHub interaction contract

## Canonical UI map

| Capability | Owner | Rule |
| --- | --- | --- |
| Select/Listbox | `components/ui/select.tsx` | Reuse the authored selector and its keyboard behavior. |
| Form | `components/ui/input.tsx`, `textarea.tsx`, `button.tsx` | Keep inline errors, input values, and duplicate-submit guards. |
| Dialog | `components/ui/dialog.tsx` | Preserve focus, Escape, and focus restoration. |
| Destructive confirmation | `components/ui/alert-dialog.tsx` | Name the consequence and require an explicit action. |
| Search | `hooks/useDebouncedMetadataSearch.ts`, `lib/latest-request-gate.ts` | Debounce requests, cancel obsolete work, and reject stale responses. |
| Authentication | `lib/authenticated-fetch.ts`, server BFF routes | Tokens stay in HttpOnly cookies. Only the BFF-confirmed final 401 ends a browser session. |
| CRUD | Existing Core routes and their owning panels | Writes need server confirmation. Preserve errors and retry paths. |

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
