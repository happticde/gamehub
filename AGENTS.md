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

Before starting work, use `view_file` on the relevant file(s):

| Area | Context File | Contents |
|-------|--------------|----------|
| **Frontend** | [frontend.md](file:///Users/jankahnt/Documents/programming/src/csharp/game-library-backend/docs/agents/frontend.md) | React/Next.js conventions, shadcn/ui rules, user preferences |
| **Backend** | [backend.md](file:///Users/jankahnt/Documents/programming/src/csharp/game-library-backend/docs/agents/backend.md) | Service patterns, Core vs Ingest logic, configuration, migrations |
| **Dev Env** | [dev-env.md](file:///Users/jankahnt/Documents/programming/src/csharp/game-library-backend/docs/agents/dev-env.md) | Docker exec commands, environment restrictions, build/test workflows |
| **History** | [history.md](file:///Users/jankahnt/Documents/programming/src/csharp/game-library-backend/docs/agents/history.md) | Last session summary, "What Changed", current status |

## Key Directories

- `services/core/`: Unified library logic.
- `services/ingest/`: Platform fetchers.
- `frontend/`: Next.js application.
- `docs/`: [ROADMAP.md](file:///Users/jankahnt/Documents/programming/src/csharp/game-library-backend/docs/ROADMAP.md), [DEBUGGING.md](file:///Users/jankahnt/Documents/programming/src/csharp/game-library-backend/docs/DEBUGGING.md), [adr/](file:///Users/jankahnt/Documents/programming/src/csharp/game-library-backend/docs/adr/)

## 🚨 Critical AI Agent Rules 🚨

1. **NEVER quietly ignore failed code edits.** If a code edit tool returns an error, you MUST halt, re-read the file, and apply the edit successfully.
2. **Verify your work.** Always double-check that your code edits were actually applied.
3. **No sweeping assumptions.** Do not tell the user a feature is complete until you have verified the code exists.
4. **Research latest stable versions.** Before adding dependencies, check the current latest stable release date/version.
5. **Contextual Awareness.** Check `docs/agents/` files before making architectural changes.
