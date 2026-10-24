# Enable and maintain Dependabot

GameHub already has [`.github/dependabot.yml`](../.github/dependabot.yml) on `main`.
Keep that configuration; the remaining setup is in GitHub's repository settings.
You need repository administration access for the settings below.

## 1. Enable dependency security features

Open [GameHub's Advanced Security settings](https://github.com/happticde/gamehub/settings/security_analysis)
under **Settings → Security and quality → Advanced Security**.
Enable these features in order:

| Setting | Desired state | Purpose |
| --- | --- | --- |
| Dependency graph | On | Provides the repository's dependency information. |
| Dependabot alerts | On | Reports dependencies affected by known vulnerabilities. |
| Dependabot security updates | On | Opens pull requests for available fixes. |

GitHub may enable prerequisites automatically. For public repositories, the
dependency graph is always enabled, so an unavailable switch can be expected.

Leave the separate **Grouped security updates** switch off: the existing YAML
already defines security-update groups for each configured ecosystem. Its group
rules remain effective without that additional global grouping switch.

Under **Dependabot version updates**, an **Edit dependabot.yml** link means the
configuration already exists. Do not replace it with GitHub's starter template.
Version-update configuration and the security-update switches are separate.

## 2. Understand the existing update plan

Version checks are scheduled for **Monday at 08:00, Europe/Berlin**. Execution can
start later depending on GitHub's queue. Security updates respond to advisories
independently of that weekly version-update schedule.

| Ecosystem | Configured locations |
| --- | --- |
| GitHub Actions | Repository workflows |
| Docker | `frontend`, `services/core`, `services/ingest` |
| npm | `frontend`, `docs/site` |
| Go modules | `services/cli` |
| NuGet | Root `Directory.Packages.props`; shared by services, MCP, tools, and tests |

Normal version updates wait seven days after a release. Security updates do not wait.
Each ecosystem entry limits open version-update PRs to two. Security-update PRs have a separate limit.

The npm groups separate framework, lint, Tailwind, and UI packages.
This keeps a new lint rule from blocking unrelated UI updates.
Go browser-helper packages have their own group. Their `0.x` releases can change APIs,
so review them as migrations. EF Core, its database providers, and native SQLite packages
stay together. Major framework and database updates remain separate from minor and patch updates.

## 3. Verify that it works

1. Open **Security and quality → Dependabot** and review the alerts. Enabling the
   feature can take time to populate the dependency graph and alerts.
2. Open **Insights → Dependency graph → Dependabot**. Check that the configured
   ecosystems are listed and inspect **Recent update jobs** for failures.
3. Use **Check for updates** for a configured manifest to request a version check.
   A successful check can produce no PR when there is no eligible update, an
   existing PR covers it, or the configured version-update PR limit is reached.
4. Subscribe to the repository's **Watch → Custom → Security alerts** notifications
   and verify your account notification preferences so new alerts reach you.

## 4. Review and merge updates

Review the dependency changes and release notes, then require the aggregate
**`checks / CI passed`** check before merging. CI selects the relevant application,
documentation and image checks for each change. Review major upgrades separately
for breaking changes, migrations and license compatibility.

License notices and inventories are generated during builds. Inspect the CI
license-evidence artifacts when relevant; there is no committed inventory or
review-hash file to refresh manually. See [licensing](../licensing/README.md).

Merge one update PR at a time. Update the remaining branches from `main` and run CI again.
A green result from an older base does not verify the combined changes.

## Keep .NET versions together

Edit `Directory.Packages.props` to change a NuGet package version.
Project files use package names without their own versions. Tests therefore receive
the same EF Core and SQLite packages as the services.

CI checks for project-level version overrides before running builds.
It also checks that the devcontainer's `DOTNET_EF_VERSION` matches EF Core.
Update that Dockerfile value when changing EF Core, then rebuild the devcontainer.
The service Dockerfiles copy the central manifest before restoring packages.
Backend CI starts a temporary PostgreSQL database. This runs the PostgreSQL migration
and isolation tests as well as the SQLite tests when database packages change.

## Read a failed update check

Check the first failed source job before inspecting image builds.
NuGet downgrade errors usually mean related projects use different versions.
New lint errors need a code review. A compiler error after a Go update may need an API migration.
Retrying the same code does not fix these failures.

Image builds wait for the selected source checks to pass.
The final CI gate still rejects failed checks and unexpected skips.

An advisory without an available fix may remain open and produce no update PR.
Record any accepted risk with scope, owner, reason and an expiry; the existing CI
process uses [`.ci/security-exceptions.json`](../.ci/security-exceptions.json).
That CI file does not dismiss or suppress GitHub's Dependabot alerts. Reassess
exceptions when a fix becomes available or before they expire.

## Private vulnerability reports

Once the repository is public, enable **Private vulnerability reporting** on the
same Advanced Security page. Confirm that **Report a vulnerability** is available
on the [advisories page](https://github.com/happticde/gamehub/security/advisories).
Follow [SECURITY.md](../SECURITY.md) when reporting a suspected vulnerability.
This reporting channel is separate from Dependabot dependency alerts.

## GitHub references

- [Manage repository security features](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/enabling-features-for-your-repository/managing-security-and-analysis-settings-for-your-repository)
- [Configure Dependabot security updates and grouping](https://docs.github.com/en/code-security/how-tos/secure-your-supply-chain/secure-your-dependencies/configure-security-updates)
- [Check or rerun Dependabot jobs](https://docs.github.com/en/code-security/how-tos/secure-your-supply-chain/manage-your-dependency-security/re-run-dependabot-jobs)
- [Configure private vulnerability reporting](https://docs.github.com/en/code-security/how-tos/report-and-fix-vulnerabilities/configure-vulnerability-reporting/configure-for-a-repository)
