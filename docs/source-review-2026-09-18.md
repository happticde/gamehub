# Source quality review — 2026-09-18

## Scope and approach

The review covered the tracked C#, TypeScript/TSX, Go, configuration, and test sources in the repository. It focused on changes that have a concrete effect on security, resource ownership, correctness, complexity, or long-term maintenance. Generated migration output, user-owned environment files, and unrelated untracked files were left untouched.

Existing authentication, tenant boundaries, lifecycle guards, backup portability, and provider audit behavior were treated as invariants. No schema migration, authentication mode, tenant boundary, or production configuration was changed.

## Changes made

- Return paths in the OIDC flow now accept only normalized same-origin paths. Backslashes, control characters, external origins, double-slash paths, and login-loop paths are rejected. The callback validates the sealed return path again before redirecting.
- Global search now cancels stale requests and gates result, error, and loading updates by request generation. A completed older request can no longer overwrite newer results.
- Next.js and its existing lint integration were updated to the current patched versions in the existing semver ranges. Transitive vulnerable packages were refreshed without adding a direct production dependency. The complete npm audit is now clean.
- IGDB credentials are sent in an encoded POST body rather than a URL. Failed authentication does not trigger a game request, stale tokens are cleared before reauthentication, provider error bodies are not logged, and requests/responses are disposed. Search literals are escaped before being embedded in the provider query language.
- Steam credentials are sent in the `x-webapi-key` header in both the ingest service and CLI. Steam IDs are encoded as query data, redirects are disabled at both boundaries, and the CLI request logic is now directly testable.
- Paginated endpoints use overflow-safe offsets, deterministic timestamp/ID tie-breaking, and safe page-size defaults. This applies to Core library pages and Ingest export/list pages.
- Steam, GOG, and PSN response/request resources are disposed at page boundaries. Returned Steam JSON values are cloned before the source document is disposed. The CLI closes provider pages before requesting the next page, stops on malformed PSN pagination, and does not forward the backend API key across redirects.
- The MCP client uses normal TLS validation and disallows automatic redirects. Library status/rating updates preserve the other personal-tracking field, support explicit rating removal, and reject malformed or undefined tracking values instead of inventing state.
- Regression tests cover redirect validation, provider request encoding and disposal, pagination overflow and ordering, CLI response ownership, Steam header handling, MCP state preservation, and malformed tracking payloads.

## Findings intentionally deferred

- `LibraryController` and the large frontend credential panel remain candidates for domain-level extraction. Splitting them mechanically would add churn without a demonstrated behavior or complexity win; this should be done with focused seams and tests.
- Several library queries use multiple collection includes and statistics round trips. A query-plan/production-data benchmark should precede any rewrite so that tenant filters, ordering, and result cardinality remain unchanged.
- The MCP update path still uses a full personal-tracking object for the existing API contract. Concurrent edits can therefore require a future partial-update or concurrency-token contract; this review removes deterministic field loss but does not invent that API change.
- CI can be strengthened by running the existing Go tests and frontend security-focused tests in addition to the current service/build checks.

## Verification and limits

- .NET build: 0 warnings, 0 errors.
- .NET tests: 482 passed, 0 failed, 0 skipped.
- Frontend lint, production build, and repository tests: 85 tests passed; all pages built successfully.
- Go tests with race detection and `go vet`: passed.
- `npm audit` including development dependencies: 0 vulnerabilities.
- `dotnet list package --vulnerable --include-transitive`: no vulnerable packages reported.
- `git diff --check`: passed.

The PostgreSQL migration tests are opt-in through their existing test connection setting and were not run against a provisioned PostgreSQL instance in this review. Provider calls were covered with deterministic HTTP fixtures; no live account credentials or production data were used. This is a source-quality and regression review, not a penetration test or load benchmark.

## References

- [Steam Web API authentication](https://partner.steamgames.com/doc/webapi_overview/auth)
- [Twitch OAuth client-credentials request format](https://dev.twitch.tv/docs/authentication/getting-tokens-oauth/#client-credentials-grant-flow)
- [Next.js security advisory](https://github.com/vercel/next.js/security/advisories/GHSA-2xp9-vwfh-vxw4)
- [Next.js 16.3.5 release](https://github.com/vercel/next.js/releases/tag/v16.3.5)
- [Microsoft TLS certificate validation API guidance](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclienthandler.dangerousacceptanyservercertificatevalidator?view=net-10.0)
