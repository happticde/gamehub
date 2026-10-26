# CI security and shared releases

GameHub uses one version for Core, Ingest, Frontend, and the CLI. A release produces three
multi-platform GHCR images, CLI/helper archives, SHA-256 checksums, and CycloneDX SBOMs.
Moving source to a new repository does not transfer GitHub Releases or GHCR packages. Publish
a verified release from this repository before using its image names in a deployment; existing
packages under another owner retain their own access and visibility settings.

The private frontend npm package version is not the release authority: Git tags, image labels,
.NET assembly versions, and `gamehub-cli --version` receive the workflow's shared version.

## Pull request and scheduled checks

`.github/workflows/ci.yml` calls the same verification workflow used by releases. It runs on
pull requests to `main`, main pushes, Monday mornings, and manual dispatch. There are no
`pull_request_target` jobs and build/scan jobs receive only read permission.

### Checks selected by changed files

Pull requests and pushes select checks using
[`scripts/ci/select-checks.py`](../scripts/ci/select-checks.py). Every run keeps **Select relevant
checks**, **Secret history scan**, and the final **CI passed** gate. Documentation can contain
secrets, so documentation-only changes still receive Gitleaks scanning. Public handbook changes
also run the independent Docusaurus build/contracts and documentation dependency scan, while
skipping application builds, Semgrep, application dependency restoration/scanning, and container
builds. Agent-context-only changes keep the secret scan and aggregate gate.
Pages publication remains exclusively manual; see [Documentation](documentation.md).

Image builds wait for all selected source checks to succeed.
Failed lint, restore, test, or security checks stop the expensive image jobs.
The final aggregate still rejects missing checks and unexpected skips.
NuGet versions come from `Directory.Packages.props`, including test dependencies.
An early check rejects local version overrides and a mismatched devcontainer EF tool.

Both documentation workflows install the locked dependencies with `scripts/ci/npm-ci.sh`.
Recognized npm connection errors retry the complete installation up to three times, waiting
10 and 20 seconds between attempts. Persistent network failures retain npm's exit status;
lockfile, integrity, authentication, and lifecycle errors fail immediately. Documentation
contracts, builds, and vulnerability scans must still succeed after installation.

| Changed files | Additional checks | Runtime images built and scanned |
| --- | --- | --- |
| `docs/**` except agent context, or the documentation-only root `Makefile` | Docusaurus build/contracts and targeted documentation dependency scan | None |
| Markdown outside CI/tooling directories, `docs/agents/**`, `.agents/**`, or `LICENSE` only | None | None |
| `.github/workflows/docs.yml` | Workflow/tooling and documentation checks | None |
| Frontend application, assets, or build configuration | Frontend lint/contracts/build and Semgrep | Frontend, AMD64 and ARM64 |
| Frontend tests only | Frontend lint/contracts/build | None |
| Core or Ingest implementation/configuration | Full backend build/tests and Semgrep | The changed service, AMD64 and ARM64 |
| Shared backend implementation | Full backend build/tests and Semgrep | Core and Ingest, both architectures |
| Backend tests only | Full backend build/tests | None |
| MCP or backend benchmark tooling | Full backend build/tests and Semgrep | None |
| CLI implementation or helper installer inputs | CLI race tests/vet, installer contracts, compiled binary scan, and Semgrep | Frontend, both architectures, because it embeds the helper |
| Go tests or installer contract tests only | CLI tests/contracts and compiled binary scan; CI tooling for CLI script changes | None |
| Dependency manifests, lockfiles, or dependency configuration | Add the repository-wide Trivy source/dependency scan to the affected component checks | As selected for the component |
| Service Dockerfile or frontend `.dockerignore` | Trivy source/production-configuration scan | The affected service, both architectures |
| Root `.dockerignore` | Trivy source/production-configuration scan | Core and Ingest, both architectures |
| Root .NET solution/SDK/build configuration | Backend build/tests, Semgrep, and Trivy source/dependency scan | Core and Ingest, both architectures |
| Docker Compose configuration | Trivy source/production-configuration scan | None |
| Other `.github/**`, `.ci/**`, `scripts/ci/**`, scanner policy, or `cliff.toml` | All checks | All six images |
| Unrecognized paths | All checks, conservatively | All six images |

Selections are combined when several components change. `services/cli/helper-distribution/README.txt`
is a packaged helper input and rebuilds the frontend image despite containing documentation.
CLI packaging script changes also run workflow/tooling checks. Backend builds/tests remain
solution-wide because Core, Ingest, Shared, MCP, and test projects share references. Semgrep and
the Trivy source scan retain their repository-wide scope whenever selected.

PR selection compares the base and head through their merge base. Push selection compares the
previous and new commit across the whole push. Git returns the complete changed-path list,
including deleted files and both sides of renames; the selector does not use GitHub's limited
workflow path-filter list. A missing comparison or unavailable history selects all checks.
The selection job writes its plan to the workflow run's job summary.

Scheduled checks, manual CI runs, and releases always run the complete suite. The reusable
workflow defaults to full verification; selective checks require an explicit `select-changes`
input, and a release version always forces full verification.
Release verification also builds a numbered documentation candidate from that exact checkout.
It does not deploy Pages. A manual documentation publication builds all supported published
release snapshots plus Next without running product builds.

The workflow deliberately still starts for documentation PRs. Skipping an entire workflow with
path filters can leave required checks pending and block merging, as described in
[GitHub's workflow trigger documentation](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/trigger-a-workflow).
Keep **CI passed** as the required aggregate. It requires the selector and Gitleaks to succeed,
requires every selected job to succeed, and accepts `skipped` only for explicitly unselected
jobs. Failures, cancellations, missing results, and unexpected skips fail the gate.

CI and release jobs explicitly use GitHub-hosted Ubuntu 26.04 runners. Image jobs use
`ubuntu-26.04` for AMD64 and `ubuntu-26.04-arm` for ARM64, so both architectures build natively.
The explicit OS labels avoid automatic distribution changes through `ubuntu-latest`, while
GitHub continues updating the runner images. Runtime container bases are configured separately
in their Dockerfiles.

The frontend's standalone server starts directly with Node. npm and Yarn remain available
in the build stages but are removed, together with their bundled dependencies, from the
runtime stage. Runtime image scans still cover Alpine, Node, and the shipped application
dependencies; no package paths or CVEs are excluded from the gate.

| Check | Scope and policy |
| --- | --- |
| Gitleaks | Full fetched Git history, with complete secret redaction. Only individually reviewed historical documentation fingerprints are ignored. A synthetic secret in a new README is tested to remain blocking. Git subprocess failures fail the scan. |
| Semgrep | Five local, tested rules cover disabled TLS validation in Go/C#/TypeScript, interpolated raw SQL in C#, and dynamic JavaScript evaluation. The pinned scanner runs without network access; metrics and version checks are disabled. These focused rules are not a comprehensive application security audit. |
| Trivy source | npm, Go modules, resolved transitive NuGet dependencies, and production Docker configuration. NuGet locks are generated only in an isolated snapshot. Development dependencies are included. |
| Trivy binaries/images | A built Linux CLI plus the six runtime images: Core, Ingest, Frontend, each for AMD64 and ARM64. Full JSON reports retain lower-severity and unfixed findings. |
| Build/contracts | .NET build/tests, Go race tests and vet, frontend lint/security/capture tests/build, and macOS installer contracts. |
| Workflow/tooling | Actionlint, ShellCheck, checksum rejection, source export boundaries, actual secret detection, Trivy gating, and real git-cliff version decisions. |

Trivy blocks `HIGH`/`CRITICAL` vulnerabilities and failed `HIGH`/`CRITICAL` misconfiguration checks.
Unfixed High findings require an exact package/version/target exception with an owner, reason,
and unexpired review date in `.ci/security-exceptions.json`. Critical findings and newly available
fixes cannot be exempted. The temporary `braces` build-tool exception expires on 2026-11-14 and
does not cover runtime images. Full reports retain those findings; a green gate does not mean
the report is empty. Scanner failures and malformed
or empty reports fail closed. Reports are retained for seven days. Image transfer artifacts
are retained for one day to reduce storage costs.

User-owned environment files are never opened by the snapshot exporter and are excluded from
Gitleaks's Git pathspec before scanning. Docker contexts also exclude nested environment files.
Untracked files, local build outputs, and the development container are not source scan inputs.
Keep credentials out of version control; these exclusions are not a secret-storage mechanism.

All external Actions use full commit SHAs. Scanner/release binaries use versions and committed
archive checksums in `.ci/tools.json`. Semgrep and BuildKit images use verified digests;
Buildx has an explicit version. Update these pins in reviewed PRs, checking upstream release
metadata and hashes. Dependabot proposes weekly updates for Actions, Docker bases, npm, Go,
and NuGet. The vulnerability database and Trivy configuration checks remain current feeds.

## Build performance

Independent checks and the six image matrix jobs run in parallel within the account's
available runner concurrency. ARM64 image builds use native runners, eliminating QEMU
emulation of npm, Next.js, Go, and .NET. GitHub lists standard ARM64 Linux runners for both
[public and private repositories](https://docs.github.com/en/actions/reference/runners/github-hosted-runners).

NuGet downloads and the frontend check's Next.js incremental cache persist between runs.
The existing npm and Go caches remain enabled. Docker builds use the pinned official build
Action to authenticate GitHub's v2 cache, with separate scopes per service and architecture.
Cache export is limited to three minutes and is best-effort: a failed export causes later
cold builds, never bypasses a build, scan, or publication gate. See
[Docker's cache authentication documentation](https://docs.docker.com/build/cache/backends/gha/).

The frontend's Go helper and Next.js build are independent stages. Helper downloads are copied
into the runtime's public directory after the application build, retaining the same URLs and
shared version without invalidating the Next.js build when only the helper version changes.
Go module downloads have a separate layer keyed by the CLI module files. Cold runs still need
to download and compile dependencies; compare hosted job timings after caches are populated.

## Branches and dependency maintenance

Use `main` as the integration and release branch. Start each change on a short-lived branch
from current `main` (`codex/...`, `feature/...`, or `fix/...`) and open a pull request back to
`main`. Dependabot also targets `main`. A permanent `develop` or dependency integration branch
is not part of this workflow. Merge after **CI passed** succeeds; do not push directly to `main`.

The repository's GitHub merge settings use squash merging only, with the PR title and description
as the default commit message. Use a Conventional Commit PR title, such as
`chore(deps): update frontend dependencies`, `fix: repair helper launch`, or
`feat: add a provider`. Put breaking-change information in the title (`!`) or a
`BREAKING CHANGE:` footer. GitHub automatically deletes merged head branches; they can be
restored from the merged PR. These GitHub settings are separate from repository files.

[`dependabot.yml`](../.github/dependabot.yml) defines the maintenance policy:

| Setting | Policy |
| --- | --- |
| Regular version checks | Monday at 08:00, `Europe/Berlin` (including daylight saving time). This is the requested check time, not a guarantee of PR creation time. |
| Minor and patch updates | One wildcard group per ecosystem: GitHub Actions, Docker, npm, Go, and NuGet. Docker and NuGet groups cover their configured service directories. |
| Major updates | Individual PRs, except one Next.js family group (`next`, `eslint-config-next`) and one EF Core family group (Microsoft EF Core packages, EF health checks, and the Npgsql EF provider). Review compatibility across the whole family. |
| Open version-update PRs | At most two per ecosystem, ten across the five entries. Existing backlog can occupy these slots; this is not a weekly PR quota. |
| Security updates | Separate groups per ecosystem, including major fixes. When Dependabot security updates are enabled, they are independent of the weekly version schedule and version-update PR limit. |
| Commit messages | Explicit `chore` prefix with dependency scope (`deps` or `deps-dev`) for consistent squash titles and release notes. |

Normally, available minor/patch updates produce up to five regular grouped PRs per check cycle.
Major upgrades, security fixes, subsequent cycles, incompatible updates, and already-open PRs
can increase the total. Do not treat five as a repository-wide hard limit. The configuration
takes effect after merging into the default branch; observe the next Dependabot run for actual
grouping and existing-PR reconciliation. No automatic merge is configured.

Dependabot alerts and security updates were enabled in GitHub on 2026-10-11 and verified after
reloading the settings page. The generic **Grouped security updates** switch remains off:
the explicit `security-updates` groups in `dependabot.yml` define grouping after this file
reaches `main`.

Groups stay within ecosystems so the CI selector can run the affected component checks.
GitHub Actions updates and changes to `dependabot.yml` still select the full suite because
`.github/**` is a CI/security input. Grouping reduces PR and CI volume without skipping checks.
The npm entry covers both `frontend` and `docs/site`; documentation package/lockfile changes
select only documentation checks. A grouped PR touching both directories selects both.

TypeScript 7.0.x is ignored because it has no compiler API for `typescript-eslint`.
Keep the frontend's existing TypeScript 5.9.3 lockfile until a supported upgrade is verified.
Close the incompatible [Dependabot PR #30](https://github.com/happticde/gamehub/pull/30)
without merging it. The ignore rule prevents new 7.0.x proposals after it reaches `main`.
TypeScript 6.x and 7.1+ remain eligible for proposals, but still need compatibility checks.
Before a native compiler migration, check
[Microsoft's compatibility guidance](https://devblogs.microsoft.com/typescript/announcing-typescript-7-0/#running-side-by-side-with-typescript-6-0)
and [typescript-eslint's support tracker](https://github.com/typescript-eslint/typescript-eslint/issues/10940).

See [GitHub's grouping guide](https://docs.github.com/en/code-security/tutorials/secure-your-dependencies/optimizing-pr-creation-version-updates)
and [Dependabot's option reference](https://docs.github.com/en/code-security/reference/supply-chain-security/dependabot-options-reference).

Collect approved updates and features on `main`, then dispatch one shared release when the batch
is ready, normally weekly or after a completed feature. An urgent security fix can receive an
earlier patch release. This cadence is an operating practice, not a scheduled publication job.
Merging multiple dependency PRs creates no release tags; only the manual release workflow
publishes the next shared version with notes covering all included commits. A dependency's own
major version does not imply a GameHub major version: use breaking-change markers for changes
to GameHub's compatibility contract. Maintenance-only batches normally get a patch version
when a release is explicitly started.

### Branch protection availability

As checked on 2026-10-11, GitHub shows the existing `main` ruleset as **Not enforced** for this
private repository and states that enforcement requires upgrading the organization to
**GitHub Team**. Classic branch protection is not configured. The PR-only policy above must
currently be followed by contributors; repository files and a passing CI workflow cannot
enforce it at GitHub's merge boundary. No billing, plan, or visibility change was made.
Once plan support is available, activate the `main` ruleset and require the aggregate
**CI passed** check as described below.

## Release a version

### Start the release

Release publication is manual. Merging a PR into `main` starts **CI**, but does not publish a
release. Dependabot runs, scheduled checks, and manually creating a Git tag also do not start
the **Release** workflow. Its only trigger is `workflow_dispatch` in
[release.yml](../.github/workflows/release.yml).

1. Merge all changes intended for this release through pull requests and confirm CI passed.
2. Open [Actions → Release](https://github.com/happticde/gamehub/actions/workflows/release.yml).
3. Click **Run workflow** and select **main** in the branch selector. The preparation job is
   skipped on other branches, so those runs cannot publish a release.
4. Set **Version increment** (`bump`) to **auto**, or select an explicit increment from the
   table below. This field selects an increment, not a complete version number.
5. Click the final **Run workflow** button, then open the new run to follow its jobs.

The run uses the `main` commit selected when it starts. Changes merged afterwards belong to a
later release; verification and publication use the same selected commit. Do not create the
release tag or a GitHub Release manually before starting the workflow.

### Choose the version

[git-cliff](../cliff.toml) calculates one version for Core, Ingest, Frontend, and the CLI.
It reads commits since the last matching `vMAJOR.MINOR.PATCH` tag. Without an existing matching
tag, the configured initial version is **v0.1.0**.

| `bump` selection | Behavior after an existing release | Example from `v0.3.2` |
| --- | --- | --- |
| `auto` | Uses the highest increment required by the commits: breaking change, feature, then patch. | `feat: add a provider` → `v0.4.0`; `fix: repair helper launch` → `v0.3.3` |
| `patch` | Explicitly increments the patch version. | `v0.3.3` |
| `minor` | Explicitly increments the minor version and resets patch. | `v0.4.0` |
| `major` | Explicitly increments the major version and resets minor and patch. | `v1.0.0` |

Use Conventional Commit messages in the commits that reach `main`, including the PR title when
using squash merge. `feat:` requests a minor increment, `fix:` requests a patch increment, and
`!` or a `BREAKING CHANGE:` footer requests a major increment. The configuration requests a
major increment for breaking changes even while the project is on `0.x`. A complete path check
rejects documentation-only changes before calculating a version, even for explicit increments.
Documentation commits may still appear in the notes of a product release containing other
changes. Maintenance, performance, and legacy non-conventional product changes normally result
in a patch increment when no feature or breaking change requires a larger one. Explicit increments
override automatic selection, so check compatibility before choosing one.

Existing release tags are not overwritten. If the calculated tag already exists, preparation
fails with `The calculated release already exists; there are no releasable changes.`
If only documentation changed since the latest reachable stable tag, preparation fails with
`No product changes since the last release; documentation must be published separately.` No
application builds or publication jobs start. Use the separate manual documentation workflow.

### Follow the jobs and release notes

| Stage | What happens | Publication state |
| --- | --- | --- |
| `prepare` | Calculates the tag and generates `RELEASE_NOTES.md` with git-cliff. Uploads these as the `release-metadata` workflow artifact. | No new Git tag, container, or GitHub Release is published. |
| `verify` | Runs the reusable source, build, test, CLI, and six image checks for the selected commit and version. Saves the scanned images and CLI archives. | Publication remains blocked until every required check succeeds. |
| `publish-images` | Six parallel jobs upload the verified Core, Ingest, and Frontend images for AMD64 and ARM64. Images are loaded from artifacts, without rebuilding. | Architecture-specific tags such as `v0.1.0-arm64` become available on GHCR. |
| `publish-release` | Creates multi-platform version manifests, verifies checksums, adds image SBOMs, creates a draft release/tag, and uploads all assets. Then promotes container `latest` tags and publishes the GitHub Release. | The published release and its downloads are ready after this job succeeds. |

Every successfully published release includes generated notes. They group commits into
**Features**, **Fixes**, **Performance**, **Documentation**, and **Other changes**, with short
commit IDs. Merge commits are omitted. The final notes also list the versioned GHCR image
references. Read them on the GitHub Release page; before publication, download the
`release-metadata` artifact from the workflow run. This workflow does not commit a
`CHANGELOG.md` or a release log back to `main`.

Release execution is serialized. No version commit is pushed to protected `main`; the release
tag points to the checked-out commit. Tag creation and publishing happen in the same workflow,
so no PAT or secondary tag-trigger workflow is required. Write permissions are limited to the
publication jobs. The first runs should be reviewed before relying on this process for production.

### Confirm publication

1. Confirm the **Release** run, including `publish-release`, finished successfully. Passing CI
   alone or successful architecture uploads do not confirm a complete release.
2. Open [GitHub Releases](https://github.com/happticde/gamehub/releases) and check the published
   **GameHub vX.Y.Z** release, its notes, and the downloads listed below. A draft is incomplete.
3. Check all three GHCR version tags contain Linux AMD64 and ARM64 manifests. For example,
   inspect a published frontend release with:

   ```bash
   docker buildx imagetools inspect ghcr.io/happticde/gamehub-frontend:v0.1.0
   ```

4. Download the CLI/helper archive for your OS and architecture, verify it against the release's
   `SHA256SUMS`, and confirm the extracted `gamehub-cli --version` reports the release version.

Use the actual published version in commands. A private repository or private GHCR package
requires authenticated access; see [GitHub setup and recovery](#github-setup-and-recovery).
A failed run may leave partial publication artifacts, so follow that recovery procedure before
trying again. Starting the release does not deploy the images to a running GameHub installation;
update the deployment's image references separately.

## Retrieve artifacts

For release `v0.1.0`, the image references are:

```text
ghcr.io/happticde/gamehub-core:v0.1.0
ghcr.io/happticde/gamehub-ingest:v0.1.0
ghcr.io/happticde/gamehub-frontend:v0.1.0
```

Each manifest supports Linux AMD64 and ARM64. Release tags are preferred for deployments;
`latest` follows the most recently published release. Image labels contain the version, source
repository, and commit. These are runtime application images; databases are not bundled.

GitHub Release assets include:

```text
gamehub-cli-0.1.0-linux-amd64.tar.gz
gamehub-cli-0.1.0-linux-arm64.tar.gz
gamehub-helper-0.1.0-macos-amd64.tar.gz
gamehub-helper-0.1.0-macos-arm64.tar.gz
gamehub-source-0.1.0.tar.gz
SHA256SUMS
cli.cdx.json
core-amd64.cdx.json / core-arm64.cdx.json
ingest-amd64.cdx.json / ingest-arm64.cdx.json
frontend-amd64.cdx.json / frontend-arm64.cdx.json
```

Download `SHA256SUMS` with the assets and run `sha256sum --check SHA256SUMS` on Linux or
`shasum -a 256 -c SHA256SUMS` on macOS after downloading all files listed in it. The manifest
covers the four binary archives, the corresponding GameHub source archive, and six image SBOMs; the separately uploaded `cli.cdx.json`
is not included in it. To verify a single archive, calculate its SHA-256 with
`sha256sum <archive>` or `shasum -a 256 <archive>` and compare it with that archive's entry.
The macOS archives contain the existing guided installer,
README, versioned CLI, license text, third-party notices, and source references. The installer
preserves the legal files alongside the installed binary. The frontend image embeds helper downloads built from its own source
and version; no separate release lookup is needed in the credential assistant.

## GitHub setup and recovery

- Ensure repository policies allow the release jobs' `contents: write` and `packages: write`
  permissions for `GITHUB_TOKEN`. Existing GHCR packages may need to grant this repository
  Actions access. No provider credential or application secret is needed by the workflows.
- Configure branch protection to require the aggregate **CI passed** check. Remove obsolete
  required job names after observing the new checks on a PR. A failed or skipped dependency
  that the selection plan requires cannot make the aggregate green. Require the aggregate
  rather than individual component jobs that may intentionally be skipped. This repository-setting
  change is not made by the code PR.
- New GHCR packages normally start private. Set each application's package visibility to public
  once if anonymous pulls are desired; otherwise authenticate with `read:packages`. GitHub
  Release downloads follow repository visibility. Changing visibility is an operator decision.
  See the [official GHCR documentation](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-container-registry).
- Builds use standard native Ubuntu AMD64 and ARM64 runners. Ensure repository/organization
  policy allows both labels; runner queue time still depends on the account's concurrency.
- A failed publish can leave architecture tags, version manifests, or a draft release. Registry
  and GitHub publication are not an atomic transaction. Inspect the failure and rerun failed jobs
  while image artifacts are available. Do not rerun successful image publication unnecessarily.
  A draft is reusable only when its tag points to the same commit; a published release is rejected.
  If artifacts have expired, remove the failed draft/tag and incomplete image tags before starting
  a fresh release run from the intended commit. Never move a successfully published version tag.
- SBOMs and checksums are provided; cryptographic provenance signing and macOS Developer ID
  signing/notarization are not implemented. Windows helper installation remains future scope.

## Browser adapter checks

The CLI job uses Chrome from the Ubuntu runner image. It runs the browser tests with
`GAMEHUB_BROWSER_TESTS=1`. The job fails if no browser is installed.
The tests cover the GOG pipe connection, the Epic and PSN WebSocket connections, and PSN remote attach.
They check cookie reads, promise results, page text, network events, and clean shutdown.
All page requests use local fixtures. No real provider login or credential is needed.

To run these tests locally, install Chrome or Chromium in the development container. Then run:

```powershell
docker exec -e GAMEHUB_BROWSER_TESTS=1 -w /workspaces/gamehub/services/cli gamehub_devcontainer-app-1 go test -race ./...
```

The normal `go test` command skips browser tests. Real provider sign-in, MFA, and CAPTCHA still need
a manual check when a provider changes its login flow.

## Initial security findings

The first scan found fixable frontend vulnerabilities in Next.js, sharp, and source-map-js,
plus vulnerable Go standard-library code in the compiled CLI. The implementation updates
Next.js/eslint-config-next to `16.3.8`, sharp to `0.35.5`, source-map-js to `1.2.2`, compatible
brace-expansion patches, and the Go 1.26 toolchain/helper builder to `1.26.9`.
Upstream references: [Next.js advisory](https://github.com/vercel/next.js/security/advisories/GHSA-vcvr-r3jv-pc5j)
and [Go security patch history](https://go.dev/doc/devel/release).

The remaining `HIGH` source finding, `CVE-2026-93687` in the development dependency
`braces@3.0.3`, has no published patched version at implementation time. It remains visible
in the full report and is not covered by an ignore entry. Historical Gitleaks exceptions cover
only thirteen confirmed documentation fingerprints: prose, a local database address, and explicit
API-key placeholders. New secrets in documentation remain blocking.

Local verification covers the source scanners, build/tests, workflow syntax, release decisions,
and CLI packages. Full image builds, registry uploads, and GitHub Release publication need the
first hosted CI/release run: the current development container has neither Docker CLI nor a
Docker socket, and no release or registry settings were changed during implementation.
