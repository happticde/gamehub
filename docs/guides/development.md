# Develop GameHub

Read the repository's `AGENTS.md` and relevant `docs/agents/` context before changing code.
Agent context and session history are maintained in Git but deliberately excluded from this
published handbook.

## Build from source

For a normal installation, use the ready-made images in [Installation](installation.md).
Use a source build when developing GameHub or testing your own changes.

```bash
git clone https://github.com/happticde/gamehub.git
cd gamehub
```

Check out a release tag if you want to build that version. Use `main` for current development.
Supply the settings from [Configuration](configuration.md).
The source-build file does not use `GAMEHUB_VERSION`; your checkout determines the code.
Then run:

```bash
docker compose -f docker-compose.yml up -d --build
```

This file builds the application from your checkout. Keep it separate from an installed
release's `compose.yaml` and its database volumes.

## Environment

Use the project's development container for builds, tests, package operations, and migrations.
The expected container is `gamehub_devcontainer-app-1`, with the repository mounted
at `/workspaces/gamehub`. Open the project using your editor's Dev Containers support
and verify that the container is running. After moving the checkout or changing the Compose
project name, rebuild/reopen the devcontainer so its name and workspace match this checkout.

```bash
docker exec -w /workspaces/gamehub gamehub_devcontainer-app-1 \
  dotnet test --verbosity quiet
docker exec -w /workspaces/gamehub/frontend gamehub_devcontainer-app-1 \
  npm run lint
docker exec -w /workspaces/gamehub/frontend gamehub_devcontainer-app-1 \
  npm run build
docker exec -w /workspaces/gamehub/services/cli gamehub_devcontainer-app-1 \
  go test -race ./...
```

For documentation commands, see [Maintaining documentation](../documentation.md).

NuGet versions live in the root `Directory.Packages.props`.
Keep service and test project references free of version overrides.
When upgrading EF Core, also update `DOTNET_EF_VERSION` in `.devcontainer/Dockerfile`
and rebuild the devcontainer. CI checks this match before building images.

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
