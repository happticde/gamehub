# ADR-001: Server-Side Game Library Retrieval

**Date:** 2026-03-04
**Status:** Proposed
**Deciders:** @jankahnt

## Context

Currently, game library data is fetched by the **Go CLI** (`gamehub-cli`), which calls external APIs (Steam, GOG, PSN), then POSTs the results to the Ingest service. This requires users to install the CLI binary and pass credentials via flags or config file.

The question: **should the Ingest service itself call the external APIs**, so users only need to hit an HTTP endpoint like `POST /api/retrieve/gog` with the required credentials?

## Decision

**Adopt a hybrid approach** — add `RetrieveController` endpoints to the Ingest service while keeping the CLI as an alternative.

### Proposed API

```
POST /api/retrieve/steam   Body: { "apiKey": "...", "steamId": "..." }
POST /api/retrieve/gog     Body: { "cookie": "...", "user": "..." }
POST /api/retrieve/psn     Body: { "cookies": { "JSESSIONID": "...", ... } }
```

Each endpoint fetches from the external API, then feeds the results directly into the existing ingest pipeline.

## Consequences

### Positive

- **No CLI needed** — trigger ingestion from any HTTP client (browser, Postman, cron, future UI)
- **Simpler automation** — a `curl` crontab replaces a Go binary
- **Single deployment** — everything runs inside Docker Compose
- **UI integration** — a future frontend can offer "Refresh Library" buttons per platform

### Negative

- **Credentials in HTTP requests** — API keys/cookies travel over the wire. Acceptable on localhost, but requires HTTPS if exposed externally. Alternatively, credentials could be stored server-side in config/env vars.
- **Long-running requests** — large libraries (100+ pages) may take 10-30s. Options: (a) accept synchronous long responses, or (b) introduce background jobs with a status polling endpoint.
- **Mixed responsibility** — Ingest currently only *stores* data; adding *fetch* logic broadens its scope. Mitigated by isolating it in a separate `RetrieveController`.
- **Code duplication** — platform-specific fetch logic (cookie auth, pagination, JSON parsing) must be rewritten in C# from the existing Go/Python implementations.

### Alternatives Considered

| Alternative | Rejected because |
|---|---|
| CLI only | Requires Go binary, not automatable via HTTP, blocks future UI |
| Separate fetch microservice | Over-engineering for a single-user app |
| Move all logic to Core | Core should only handle unification, not raw data collection |

## Implementation Notes

- Add a new `RetrieveController` in the Ingest service
- Reuse existing `IngestControllerBase` logic internally (call the same upsert methods)
- Keep the existing `POST /api/ingest/{platform}` endpoints so the CLI still works
- Consider storing credentials in `appsettings.json` / env vars so they don't need to be sent per request
