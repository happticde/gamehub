# Develop GameHub

Read the repository's `AGENTS.md` and relevant `docs/agents/` context before changing code.
Agent context and session history are maintained in Git but deliberately excluded from this
published handbook.

## Environment

Use the project's development container for builds, tests, package operations, and migrations.
The expected container is `game-library-backend_devcontainer-app-1`, with the repository mounted
at `/workspaces/game-library-backend`. Open the project using your editor's Dev Containers support
and verify that the container is running.

```bash
docker exec -w /workspaces/game-library-backend game-library-backend_devcontainer-app-1 \
  dotnet test --verbosity quiet
docker exec -w /workspaces/game-library-backend/frontend game-library-backend_devcontainer-app-1 \
  npm run lint
docker exec -w /workspaces/game-library-backend/frontend game-library-backend_devcontainer-app-1 \
  npm run build
docker exec -w /workspaces/game-library-backend/services/cli game-library-backend_devcontainer-app-1 \
  go test -race ./...
```

For documentation commands, see [Maintaining documentation](../documentation.md).

For an intentionally anonymous local service, explicitly set `ASPNETCORE_ENVIRONMENT=Development`
and `Authentication__AllowAnonymousLocalDevelopment=true`, leave browser/JWT/OIDC/multi-user
authentication disabled, and bind only to loopback. Missing keys alone never disable authentication.
The Next.js dev server defaults browser origin checks to `http://localhost:3005`; set `PUBLIC_ORIGIN`
when using a different dev origin. Do not use this anonymous setup for the Compose deployment.

## Make a focused change

Start a short-lived branch from current `main` and integrate through a pull request. Never commit
directly to protected `main`. Keep code, comments, documentation, commits, and PR descriptions in
English. Preserve operator-owned environment files; agents must not read or modify them.

Keep Core's unified library responsibilities separate from Ingest's provider/raw-data ownership.
Use the central tenant context and existing authentication policies rather than request-selected
tenant identifiers. Review provider-specific database migrations and cross-tenant tests when
changing durable user-owned state.

## Verify the relevant boundary

Backend changes need the relevant service build and tests. Auth/BFF changes need the security
contracts as well as the frontend build. CLI/helper changes need Go tests and the installer
contracts. CI/tooling changes need their integration tests and Actionlint. A documentation
change needs a complete Docusaurus build, link checks, and documentation tooling tests.

CI's required aggregate rejects missing, failed, cancelled, and unexpectedly skipped jobs.
Do not make a failing pipeline green by suppressing a check without understanding the failure.

## Prepare releases

Use a Conventional Commit PR title for squash merges, for example `feat: add a provider`,
`fix: correct a match`, or `docs: explain deployment`. Product releases remain manually
dispatched and use one version across the components. Documentation-only changes do not create
a releasable product delta. See [CI and releases](../ci-and-releases.md) for the release runbook.
