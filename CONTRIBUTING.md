# Contributing to GameHub

Open a pull request against `main`; do not push directly to it. Explain the
problem, the change, and the verification performed. Follow the development
guide in [docs/guides/development.md](docs/guides/development.md).

## Enable secret protection before committing

Run this once in every new clone. Git does not install repository hooks automatically.
Python 3 and the Gitleaks version pinned in `.ci/tools.json` are required. If that version
of Gitleaks is already on your `PATH`, enable the hook with:

```bash
sh scripts/install-git-hooks.sh
```

For commits on the host without a native scanner, use the running development container:

```bash
docker exec -w /workspaces/gamehub gamehub_devcontainer-app-1 \
  python3 scripts/ci/install-tool.py gitleaks /tmp/gamehub-ci-tools
git config --local gamehub.gitleaksContainer gamehub_devcontainer-app-1
sh scripts/install-git-hooks.sh
```

Use your actual container name if it differs. The checkout must be mounted at
`/workspaces/gamehub`. Repeat the tool installation after recreating the container.
For commits inside the container, add `/tmp/gamehub-ci-tools` to `PATH` first.
The setup preserves existing custom hooks by stopping with an integration message.

The pre-commit hook scans the full staged content of each added or changed file, including
renames and partial staging. It never changes the index or working files. Staged environment
files are rejected by name without reading their contents. Deletions are allowed.
Symlink targets are never opened. Scanner errors, missing tools, and version mismatches block
the commit. Findings do not print credential values. Local scans ignore inline
`gitleaks:allow` comments; review false positives through the shared scanner policy.

Run `.githooks/pre-commit` to check the current index manually. Local Git hooks can be
bypassed or disabled, and scanners cannot recognize every credential. The existing CI history
scan remains required. Revoke and replace any real credential that has already been committed.

## Licensing contributions

Unless a file already has a different license notice, contributions to GameHub
are submitted under **AGPL-3.0-only**. Contributions to the independent website
implementation/tooling in `docs/site` are submitted under **MIT**; handbook
content sourced from the rest of `docs` remains AGPL-3.0-only.

Only submit work you have the right to contribute under the applicable terms.
Identify copied or adapted code and preserve its copyright and license notices.
Include the source and exact version for newly introduced third-party material.
Review both direct and transitive dependency licenses when changing dependencies.

Contributors retain their rights. This policy does not assign copyright or grant
the maintainer unrestricted relicensing rights. A future proprietary license or
incompatible relicensing may require separate permission from contributors.

## Source availability and releases

Keep the license, generated third-party notices, dependency license checks, and source references
current. See [licensing/README.md](licensing/README.md) for the review procedure.
Modified deployments must offer the corresponding source for the version they
actually run where the AGPL requires it. A link to an unmodified upstream build
does not cover unpublished local changes.

Never include credentials, personal library exports, databases, or local
environment configuration in a contribution or published source archive.
