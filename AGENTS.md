# GameHub Backend — Agent Context

> This file provides high-level context for AI coding assistants.
> **Optimization**: To save tokens, detailed context is split into modular files.
> **Instruction**: Read the relevant context file(s) below based on your current task.

## Architecture

Two ASP.NET Core microservices + Go CLI, orchestrated via Docker Compose:

```
┌──────────────┐     ┌──────────────┐     ┌──────────────┐
│   Ingest     │◄────│    Core      │◄────│   Frontend   │
│  :5000       │     │   :5001      │     │  Next.js     │
│              │     │              │     │ (React Space)│
│ • Raw game   │     │ • Unified    │     │ • User UI    │
│   storage    │     │   library    │     │ • BFF API    │
│ • Platform   │     │ • Slug-based │     │   Routes     │
│   fetchers   │     │   matching   │     │              │
│ • Export API │     │ • Merge/Split│     └──────────────┘
└──────────────┘     └──────────────┘
       ▲
       │
┌──────┴────────┐
│   Go CLI      │
│ gamehub-cli   │
└───────────────┘
```

## Modular Context Files

Before starting work, read the relevant file(s) below. Resolve these paths from the current repository root:

| Area | Context File | Contents |
|-------|--------------|----------|
| **Frontend** | [frontend.md](docs/agents/frontend.md) | React/Next.js conventions, shadcn/ui rules, user preferences |
| **Backend** | [backend.md](docs/agents/backend.md) | Service patterns, Core vs Ingest logic, configuration, migrations |
| **Dev Env** | [dev-env.md](docs/agents/dev-env.md) | Docker exec commands, environment restrictions, build/test workflows |
| **History** | [history.md](docs/agents/history.md) | Last session summary, "What Changed", current status |

## Key Directories

- `services/core/`: Unified library logic.
- `services/ingest/`: Platform fetchers.
- `frontend/`: Next.js application.
- `docs/`: [ROADMAP.md](docs/ROADMAP.md), [DEBUGGING.md](docs/DEBUGGING.md), [adr/](docs/adr/)

## Reusable Workflow Skills

- Use [gamehub-pr-workflow](.agents/skills/gamehub-pr-workflow/SKILL.md) for PR-bound changes: protected branch setup, Docker verification, env-file safeguards, dependency/config checks, English commit, push, and prefilled PR link.

## 🚨 Critical AI Agent Rules 🚨

1. **NEVER quietly ignore failed code edits.** If a code edit tool returns an error, you MUST halt, re-read the file, and apply the edit successfully.
2. **Verify your work.** Always double-check that your code edits were actually applied.
3. **No sweeping assumptions.** Do not tell the user a feature is complete until you have verified the code exists.
4. **Research latest stable versions.** Before adding dependencies, check the current latest stable release date/version.
5. **Contextual Awareness.** Check `docs/agents/` files before making architectural changes.
6. **Protected main branch.** NEVER commit or push directly to `main`. Before changing any file, verify the current branch and create a new task-specific branch when on `main`. Changes may reach `main` only through a pull request.
7. **Tenant isolation awareness.** GameHub is currently single-user, but multi-tenancy is planned in Roadmap item #8. Do not enable or claim multi-user support until all user-owned reads, writes, background jobs, and unique constraints are scoped to the authenticated tenant. Changes involving library data, sync, tags, manual links, authentication, or backup/restore must review the multi-tenancy requirements in ADR-008 and include cross-tenant isolation tests when tenant support is introduced.
8. **Environment files are user-owned.** NEVER read, inspect, print, create, edit, or delete `.env` files. Tell the user which variables are required, but leave all secret configuration to the user.
9. **Prefilled pull request links.** When providing a link to create a pull request, always include a meaningful prefilled title and a structured description covering the summary, verification performed, and important limitations or follow-up work. The PR description in the `body` query parameter MUST be fully URL-encoded; Markdown headers such as `## Summary` MUST be encoded as `%23%23%20Summary`, otherwise GitHub treats `#` as a URL fragment and drops the description.
10. **Human-owned merge and publication.** Agents implement, verify, push task branches, and create pull requests. The user handles PR merges and all product releases and documentation deployments. Hand off the PR and any required manual publication steps.
10. **Simple language.** Use simple language in documentation and replies. Write short, direct sentences. Keep one main idea per sentence. Explain necessary technical terms in plain words. Avoid nested clauses and overly complex wording. Keep repository documentation in English.
