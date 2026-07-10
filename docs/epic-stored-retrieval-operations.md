# Epic Stored Retrieval Operations

This runbook covers the existing Epic Games stored-retrieval MVP after an operator has connected
an account through the local helper. It is an operational guide for the existing Core, Ingest, audit,
and cooldown behavior; it does not add an Epic authorization flow, automatic retries, or provider
monitoring infrastructure.

For account connection and local-helper troubleshooting, see
[Platform Connect Helpers](platform-connect-helpers.md). For the helper payload boundary, see
[Platform Connect Helper Contract](gog-connect-helper-contract.md).

## Operating Boundary

- Epic uses a launcher-compatible, Legendary-inspired retrieval path. It is not official Epic OAuth
  support and must not be presented as a provider partnership or production-proof integration.
- Refresh tokens remain in the tenant-scoped encrypted Ingest vault. Access tokens are in-memory
  only. Never request, copy, or attach either token to a ticket, log, screenshot, or chat.
- Epic retrieval writes existing raw items and Core continues to unify the previously stored tenant
  library when a provider refresh cannot run. This protects availability, but it does not make stale
  raw data current.
- The audit history and application logs expose safe categories only. They are the supported
  diagnostics surface; do not add provider response bodies or credential values to diagnostics.

## Controlled Live Validation

Use a dedicated, non-critical Epic account when validating a deployment or a provider change.

1. Connect the account through the normal Admin helper flow and verify only the credential status.
2. Trigger one Epic sync and check the tenant's Ingest and Core audit entries.
3. Confirm that the expected library items unify through the existing `app_name`/`app_title` path.
4. Inspect a small sample of safe fallback metadata such as artwork, summary, developer, publisher,
   or release date. Existing enriched metadata must remain preferred.
5. Keep only the audit status, timestamps, counts, and redacted fixture shape as operational
   evidence. Do not retain helper JSON, authorization codes, tokens, cookies, account identifiers,
   request headers, query strings, or full provider responses.

## Audit Response Guide

The Admin audit history is tenant-scoped. Ingest records provider-facing states; Core can record
`PartialSuccess` when it exports existing raw data after a failed refresh or an active cooldown.

| Audit state | Meaning | Operator response |
| --- | --- | --- |
| `Success` | The library was retrieved and ingested. | No action. Review counts only when a library change is expected. |
| `PartialSuccess` | Some catalog records were skipped, or Core exported existing raw data after refresh was deferred or failed. | Existing games remain usable. Retry once later only if a current refresh is needed. If the state repeats for the same title or catalog shape, follow the payload-drift workflow below. |
| `TokenExpired` | Epic rejected the stored credential. | Reconnect through the normal visible Epic helper flow, then trigger one sync. Do not attempt to repair or paste a refresh token into logs or tooling. |
| `RateLimited` | Epic returned a rate-limit response. | Respect the displayed retry time when available. Core keeps a transient tenant-and-platform cooldown and will still export existing raw data. Do not repeatedly force syncs or reconnect the account. |
| `UpstreamUnavailable` | Epic could not be reached or returned a temporary upstream failure. | Leave the credential unchanged and retry later. Existing raw data continues to unify. Escalate only when the condition persists beyond the local operational window. |
| `MalformedResponse` | A required Epic response shape was unexpected. | Do not change the mapper from live payloads. Start the payload-drift workflow and preserve only a redacted, minimal fixture. |
| `Error` | A safe non-specific retrieval failure was recorded. | Check the current tenant's service health and the paired Core/Ingest audit entries. Treat it as an outage until a safe category or reproducible fixture identifies a narrower cause. |

## Cooldown and Retry Discipline

When Epic supplies `Retry-After`, Ingest passes only its UTC retry time to Core. Core stores a
process-local cooldown per tenant and platform. During that period, a sync skips the Epic refresh
and unifies the tenant's existing raw library instead.

- Wait until the indicated time before requesting another Epic sync.
- A restart clears the cooldown; it is not permission to bypass the provider's limit.
- Do not schedule client-side retry loops or replay token exchanges.
- Do not reconnect for `RateLimited` or `UpstreamUnavailable`; reconnect only after a credential
  rejection or an operator-confirmed credential problem.

## Payload-Drift Workflow

Use this workflow for repeated `MalformedResponse`, repeated partial catalog skips, or a confirmed
new Epic response variant.

1. Record the safe audit category, timestamp, affected capability, and whether stored raw export
   still completed. Do not record provider response content.
2. Create a minimal, redacted launcher-asset or catalog-metadata fixture that contains only the
   fields required to reproduce the parser decision. Replace account data, identifiers unrelated to
   matching, URLs with signed/query material, tokens, cookies, and headers.
3. Add or update a mapper regression test for the fixture before changing parser behavior.
4. Preserve the `app_name`/`app_title` matching shape and optional-field tolerance. Safe metadata
   remains fallback-only and must not replace existing enriched metadata.
5. Validate the change without live Epic calls, then perform one controlled live validation using
   the procedure above.

The existing redacted fixture suite is the contract boundary. A live provider payload is never a
test fixture.

## Operational Observation

There is no separate metrics or alerting subsystem in this MVP. Review the existing tenant-scoped
audit history and service logs for these signals:

- any `TokenExpired` result, which requires a reconnect;
- repeated `RateLimited` results or a cooldown that outlasts the provider-supplied retry time;
- repeated `UpstreamUnavailable` results, which indicate a provider or network incident;
- any `MalformedResponse`, which requires fixture-led investigation;
- `PartialSuccess` results whose game count drops unexpectedly or remains repeated after a later
  successful provider response.

Treat the list as an operator review checklist, not as implemented alerting. Adding metrics,
notifications, retry orchestration, or provider health checks requires a separately scoped change.
