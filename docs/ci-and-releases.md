# CI security and shared releases

GameHub uses one version for Core, Ingest, Frontend, and the CLI. A release produces three
multi-platform GHCR images, CLI/helper archives, SHA-256 checksums, and CycloneDX SBOMs.
The private frontend npm package version is not the release authority: Git tags, image labels,
.NET assembly versions, and `gamehub-cli --version` receive the workflow's shared version.

## Pull request and scheduled checks

`.github/workflows/ci.yml` calls the same verification workflow used by releases. It runs on
pull requests to `main`, main pushes, Monday mornings, and manual dispatch. There are no
`pull_request_target` jobs and build/scan jobs receive only read permission.

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
archive checksums in `.ci/tools.json`. Semgrep, BuildKit, and QEMU images use verified digests;
Buildx has an explicit version. Update these pins in reviewed PRs, checking upstream release
metadata and hashes. Dependabot proposes weekly updates for Actions, Docker bases, npm, Go,
and NuGet. The vulnerability database and Trivy configuration checks remain current feeds.

## Release a version

1. Merge the intended changes through a pull request and confirm CI passed.
2. Open **Actions → Release → Run workflow**, select **main**, and normally choose **auto**.
3. `git-cliff` calculates the version and notes from the selected main commit. The first version
   is `v0.1.0`; existing tags are not overwritten. `feat:` increments minor, `fix:` increments
   patch, and `!`/`BREAKING CHANGE` increments major. Legacy non-conventional commits remain in
   the notes and cause a patch bump. Explicit patch/minor/major selection is available.
4. All source, build, binary, and image checks run again for that exact commit and version.
   The workflow saves the scanned images instead of rebuilding them during publication.
5. Only after every check succeeds, the publication jobs upload the images and create their
   multi-platform manifests. A draft GitHub Release receives the CLI/helper archives and SBOMs.
   Checksums cover all archives and SBOMs. After successful uploads, `latest` is promoted and
   the GitHub Release is published.

Release execution is serialized. No version commit is pushed to protected `main`; the release
tag points to the checked-out commit. Tag creation and publishing happen in the same workflow,
so no PAT or secondary tag-trigger workflow is required. Write permissions are limited to the
publication jobs. The first runs should be reviewed before relying on this process for production.

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
`shasum -a 256 -c SHA256SUMS` on macOS. The macOS archives contain the existing guided installer,
README, and versioned CLI. The frontend image embeds helper downloads built from its own source
and version; no separate release lookup is needed in the credential assistant.

## GitHub setup and recovery

- Ensure repository policies allow the release jobs' `contents: write` and `packages: write`
  permissions for `GITHUB_TOKEN`. Existing GHCR packages may need to grant this repository
  Actions access. No provider credential or application secret is needed by the workflows.
- Configure branch protection to require the aggregate **CI passed** check. Remove obsolete
  required job names after observing the new checks on a PR. A failed or skipped dependency
  cannot make the aggregate green. This repository-setting change is not made by the code PR.
- New GHCR packages normally start private. Set each application's package visibility to public
  once if anonymous pulls are desired; otherwise authenticate with `read:packages`. GitHub
  Release downloads follow repository visibility. Changing visibility is an operator decision.
  See the [official GHCR documentation](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-container-registry).
- Builds use standard Ubuntu AMD64 runners and pinned QEMU for ARM64, avoiding a dependency on
  native ARM runner availability for private repositories. Emulated builds can take longer.
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
