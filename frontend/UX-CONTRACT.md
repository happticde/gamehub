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
| Assistant consent | `components/McpConsentForm.tsx`, `lib/mcp-consent.ts`, `/api/mcp/consent` | [ADR-016](../docs/adr/016-mcp-user-authorization.md), [browser boundary](../docs/mcp-browser-consent.md) | Code entry, explicit review, approval/denial receipt | Browser, transport, startup and lifecycle tests |
| Assistant connections | `components/admin/McpConnectionsPanel.tsx`, `lib/mcp-connections.ts`, Core `/auth/mcp-connections` | [ADR-016](../docs/adr/016-mcp-user-authorization.md), [connection management](../docs/mcp-connections.md) | Owned active consents; single confirmed revocation | Production HTTP, database race, browser and payload tests |
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

Assistant consent displays the exact instance, account reference and entered code before a decision.
Reading is the default; requested tracking writes need an unchecked optional control. An expired,
changed or uncertain decision returns to code review; it never retries approval automatically.
Codes and confirmation remain in page memory only. Login returns to `/mcp/verify` without carrying
a code in the URL. The Core service owns identity, provider provenance, expiry and one-time use.

Assistant connections use the Security tab's existing cards and German action labels. Dates use
German formatting in the browser's local time zone. Keep the full local connection ID available
to distinguish identical public clients. This ID is not a credential. Show only approved scopes,
the resource, creation and absolute expiry. No provider or request metadata belongs in this view.
Single revocation uses the shared alert dialog, initially focuses Cancel, and waits for Core's
confirmation. A failed or uncertain response keeps the dialog available for an explicit retry;
successful removal returns focus to Refresh. List requests have bounded waits and abort on unmount.
