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
The same early check keeps all Go builders aligned with the CLI toolchain.
Update the CLI toolchain and its Docker build stages together when applying a Go security patch.

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

## Alert review: 2026-11-04

### Frontend minimatch (alert #11)

[GHSA-g3hv-75wg-m249](https://github.com/advisories/GHSA-g3hv-75wg-m249)
affects minimatch versions from 7.0.0 through versions below 11.0.1.
The frontend lockfile previously resolved 10.2.6 through ESLint and its configuration.
The frontend override now selects 11.0.4 for those affected version ranges.
This was the latest stable release in the npm registry, published on 2026-11-02.
It supports the Node 24 used in CI. Older minimatch 3.x consumers retain their existing API.
Remove the override when the parent packages resolve patched versions themselves.
The alert can close after the corrected lockfile reaches `main` and GitHub scans it.

## Alert recheck: 2026-11-12

### Documentation deepmerge (alert #8, CVE rejected)

The [official CVE record](https://raw.githubusercontent.com/CVEProject/cvelistV5/main/cves/2026/93xxx/CVE-2026-93753.json)
marks CVE-2026-93753 as `REJECTED` on 2026-11-02. The stated reason is:
"This CVE ID has been rejected because it does not describe a security vulnerability."
This corrects the earlier review, which did not account for the rejection.

[GHSA-gcqx-w48m-w2h4](https://github.com/advisories/GHSA-gcqx-w48m-w2h4)
still lists deepmerge 4.2.0 through 4.3.1 as affected, with low severity and no patched version.
The npm registry still lists deepmerge 4.3.1, Docusaurus 3.10.2, and `@svgr/plugin-svgo` 8.1.0
as their latest stable releases. A fresh `npm audit` still reports this advisory across five
packages in the same dependency chain. These are not five independent findings.
Its proposed Docusaurus 3.6.3 downgrade is not a patched deepmerge release.

The declared dependency path is `@docusaurus/preset-classic` → `@docusaurus/plugin-svgr` →
`@svgr/webpack` → `@svgr/plugin-svgo` → `deepmerge`.
The installed `@svgr/plugin-svgo` 8.1.0 compiled entry point imports `svgo` and `cosmiconfig`;
it contains no deepmerge reference. The dependency remains in the package manifest and lockfile.
This observation is separate from the CVE rejection; no package patch was applied.

These packages build the static handbook. The published site contains static assets;
it does not run Docusaurus or deepmerge as a Node server. The preview command also serves
only generated files. This scope is separate from GitHub's npm runtime-dependency label.
Do not expose a documentation development server or build untrusted SVG/configuration input.

The maintainer can dismiss this specific alert as a false positive, citing the official CVE
record and its rejection date. Suggested dismissal note:

> CVE-2026-93753 was rejected by its CNA on 2026-11-02 because it does not describe a security
> vulnerability. See the [official CVE record](https://raw.githubusercontent.com/CVEProject/cvelistV5/main/cves/2026/93xxx/CVE-2026-93753.json).
> GitHub/npm still carry the older advisory metadata. This dismissal applies only to
> GHSA-gcqx-w48m-w2h4.

No alert was dismissed and no scanner exception was added by the agent. Keep dependency
updates and security scans enabled. Reassess if the advisory changes or a different finding
affects this package. This review supersedes the earlier 2026-11-18 recheck deadline for this CVE.

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
