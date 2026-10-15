# Security hardening — 2026-10-15

This follow-up addresses the publication-readiness audit without enabling multi-user admission,
changing tenant ownership, or modifying operator-owned environment files.

- Protocol-handler launches require native confirmation of the exact receiving origin before a
  provider browser or loopback status listener starts. Query parameters cannot authorize another
  private-network origin. Loopback listeners reject non-loopback Host headers to prevent DNS
  rebinding. Explicit manual CLI origin selection remains available.
- Missing API keys fail closed. Anonymous services require an explicit Development-only opt-in
  and cannot coexist with browser/JWT/OIDC/multi-user authentication. Production starts require
  configured authentication and strong API/service keys. Compose requires independent API/JWT
  keys and a PostgreSQL password, enables frontend login, and binds published ports to loopback.
- Browser writes require the configured exact `PUBLIC_ORIGIN` before cookie processing, request
  buffering, refresh rotation, or upstream fetch. Host and forwarding headers grant no trust.
  The provider callback and public back-channel logout retain their existing protocol checks.
- Authentication attempts use hashed normalized-email, refresh-token, provider-token, or validated
  account/tenant buckets instead of the BFF source IP. A bounded 4096-entry window store and a
  separate concurrency limit bound memory and active verification. Forwarded client-IP headers
  never select identity. Repeated password guesses at
  one known email still consume that account's own protection budget.
- `golang.org/x/text` is updated to v0.43.0 and the documentation KaTeX override to 0.19.0 after
  checking current stable releases. The unpatched `braces` 3.0.3 issue has a package/version/target
  scoped exception expiring 2026-11-14. CI now blocks other unreviewed unfixed High findings as
  well as all Critical and fixable High findings.

## Deployment transition

Install updated helper downloads and frontend together. Old installed helpers retain the old
trust behavior until replaced; frontend updates alone cannot secure them. Expect a native origin
confirmation each time a protocol-handler connection starts. Manual handoff commands for LAN or
hosted frontends need an explicit `--origin` matching the actual frontend.

Provide `GAMEHUB_API_KEY` (32–512 printable non-space ASCII characters), an independent
`GAMEHUB_JWT_SIGNING_KEY` (at least 32 UTF-8 bytes), and `POSTGRES_PASSWORD` before using Compose.
For an HTTPS domain, set `GAMEHUB_PUBLIC_ORIGIN` to the exact origin, for example
`https://games.example.com`, and keep Secure cookies enabled. The local Compose default is
`http://localhost:3000`. Frontend mutation routes require `Origin`, so non-browser automation
should use the authenticated Core API rather than impersonating a browser BFF caller.

Recreate deployment containers to adopt configuration changes. No production service restart,
secret rotation, provider login, repository visibility change, or data migration is performed by
this source change. Existing GitHub logs, artifacts, permission settings, and release downloads
still need a separate publication check.

## Verification

- 504 .NET tests passed, including production authentication refusal, explicit development mode,
  per-account/session throttling, tenant-separated bucket keys, and bounded anonymous bucket memory.
- A separate Core process with audit-owned SQLite storage returned 401 for anonymous library and
  backup reads with JWT enabled and no API key. Ten failed attempts exhausted only one email's
  bucket; another email and refresh still reached authentication. Oversized auth bodies returned 413.
- 78 frontend security/contracts tests passed, including actual BFF handlers rejecting sibling
  origins before cookie changes or upstream calls. Frontend lint and production build passed.
- Go race tests and vet passed; both macOS helper archives built. Four installer contracts passed.
- Nine documentation contracts and the Docusaurus production build passed in an isolated snapshot.
- 28 CI-tooling tests passed. Trivy retained only the two scoped source-lockfile `braces` findings;
  the revised gate passed. The five local Semgrep rules found no issues. Go vulnerability analysis
  reported no vulnerabilities after the dependency update.

No schema or migration changed; PostgreSQL migrations were not repeated for this source-only
follow-up. Native macOS confirmation was not interactively exercised, and complete runtime image
scans remain for the hosted CI matrix. All builds/tests used Docker; snapshots excluded environment
files. A documentation test initially inherited an incorrectly supplied Git environment and
created a local test commit/configuration; that test state was removed while preserving all work,
and verification was repeated with private temporary Git metadata. It is absent from this branch's
committed history.
