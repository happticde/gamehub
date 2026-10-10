# CI security and shared releases

GameHub uses one version for Core, Ingest, Frontend, and the CLI. A release produces three
multi-platform GHCR images, CLI/helper archives, SHA-256 checksums, and CycloneDX SBOMs.
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
secrets, so a documentation-only change still receives Gitleaks scanning but skips application
builds, Semgrep, dependency restoration/scanning, and container builds.

| Changed files | Additional checks | Runtime images built and scanned |
| --- | --- | --- |
| Markdown outside CI/tooling directories, `docs/**`, `.agents/**`, or `LICENSE` only | None | None |
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
| `.github/**`, `.ci/**`, `scripts/ci/**`, scanner policy, or `cliff.toml` | All checks | All six images |
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

Trivy blocks fixable `HIGH`/`CRITICAL` vulnerabilities and failed `HIGH`/`CRITICAL`
misconfiguration checks. Findings without a published fix remain in the reports and require
operator review; a green gate does not mean the report is empty. Scanner failures and malformed
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
major increment for breaking changes even while the project is on `0.x`. Documentation,
maintenance, performance, and legacy non-conventional changes normally result in a patch
increment when no feature or breaking change requires a larger one. Explicit increments
override automatic selection, so check compatibility before choosing one.

Existing release tags are not overwritten. If the calculated tag already exists, preparation
fails with `The calculated release already exists; there are no releasable changes.`

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
SHA256SUMS
cli.cdx.json
core-amd64.cdx.json / core-arm64.cdx.json
ingest-amd64.cdx.json / ingest-arm64.cdx.json
frontend-amd64.cdx.json / frontend-arm64.cdx.json
```

Download `SHA256SUMS` with the assets and run `sha256sum --check SHA256SUMS` on Linux or
`shasum -a 256 -c SHA256SUMS` on macOS after downloading all files listed in it. The manifest
currently covers the four archives and six image SBOMs; the separately uploaded `cli.cdx.json`
is not included in it. To verify a single archive, calculate its SHA-256 with
`sha256sum <archive>` or `shasum -a 256 <archive>` and compare it with that archive's entry.
The macOS archives contain the existing guided installer,
README, and versioned CLI. The frontend image embeds helper downloads built from its own source
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
