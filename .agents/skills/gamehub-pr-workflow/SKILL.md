---
name: gamehub-pr-workflow
description: Standard GameHub PR workflow for repository changes that need a codex branch, Docker-only verification, env-file safeguards, dependency/config checks, an English commit, push, and prefilled PR link.
---

# GameHub PR Workflow

Use this skill to turn a GameHub repository task into a safe, reviewable pull request.

## Operating rules

- Keep source code, comments, documentation, tests, commit messages, and pull request text in English.
- Never read, inspect, print, create, edit, or delete `.env` files.
- Never commit or push directly to `main`.
- Use the Build and Test via Docker skill for every build, test, lint, migration, package, or framework command.
- Do not add dependencies or change production configuration unless the user explicitly asks for that scope.
- Preserve auth and tenant-isolation boundaries. Tokens and provider credentials must not reach browser JavaScript or `localStorage`.

## Start-of-task workflow

1. Read `AGENTS.md` and the relevant `docs/agents/` context files for the task area.
2. Fetch `origin/main`.
3. Inspect the current branch and worktree state.
4. If the task requires changes, create a task-specific `codex/` branch directly from `origin/main` before editing files.
5. Preserve unrelated user changes. Stop and ask for direction if unrelated changes overlap the intended edits.

## Implementation workflow

- Keep changes scoped to the user's stated goal.
- Prefer reusable, precise types and project conventions over broad assertions or lint suppressions.
- Use `apply_patch` for file edits.
- After editing, verify that the intended changes actually exist.
- If an edit tool fails, halt, re-read the target file, and apply the edit successfully before continuing.

## Standard verification

Run only the checks that are relevant to the changed scope, unless the user asks for the full release-style suite.

For frontend changes:

- Changed-file lint when practical.
- Repository-wide frontend lint.
- Frontend production build.
- BFF security tests when auth, BFF, session, OIDC, cookies, local storage, or request-boundary code is touched.

For backend, migrations, tenant, sync, auth, or cross-service changes:

- Relevant service build.
- Full `.NET` test suite when the change can affect repository behavior beyond a narrow documentation-only edit.
- Migration verification when EF migrations or model snapshots change.

For every PR-bound change:

- `git diff --check`.
- Verify that no `.env` file is changed.
- Verify that dependency manifests, lockfiles, and production configuration changed only when intentionally in scope.

## Commit, push, and handoff

1. Review `git diff` and `git status --short --branch`.
2. Stage only intended files.
3. Create an English commit message that describes the user-facing change.
4. Push the task branch.
5. Provide a prefilled pull request link with:
   - Summary
   - Implementation details
   - Behavioral and security invariants
   - Verification
   - Limitations or follow-up

Mention skipped verification explicitly when a check is unnecessary for the scope, such as documentation-only or skill-only changes.
