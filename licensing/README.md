# GameHub licensing and source availability

GameHub application code, backend, CLI/helper and handbook content are
**AGPL-3.0-only**. The independent website implementation/tooling in `docs/site`
is **MIT**. See [LICENSE](../LICENSE), [COPYRIGHT](../COPYRIGHT), the
[website license](../docs/site/LICENSE), and [contribution terms](../CONTRIBUTING.md).
Third-party works retain their own terms. `REUSE.toml` records the source scope
and copied UI/asset exceptions; it does not relicense dependencies.

## Generated notices, not committed reports

Builds generate `THIRD_PARTY_NOTICES.txt` and `dependency-inventory.json` from
their resolved local packages. Neither generated report belongs in Git. There
is no frozen inventory or input-hash file to update after a dependency change.
CI retains the reports as downloadable license-evidence artifacts for seven days.

- Frontend: `npm run build` / `npm run dev` prepares production dependency
  notices in `.generated/legal`, then serves the license and notices through
  `/legal/index.html` without authentication. Development-only dependencies and
  optional packages absent on the build platform are omitted. Original notices
  for copied components, starter assets and Geist fonts are included.
- Documentation: `npm run build` generates notices for the installed website
  dependencies in `.generated/legal` and publishes the notices under `static/legal`.
- Core/Ingest: each Docker build collects its own restored NuGet package notices
  into `/app/legal`; test-project packages are not included.
- CLI/helper: packaging collects resolved Go module notices and the matching Go
  standard-library license. Downloads include `LICENSE.txt`,
  `THIRD_PARTY_NOTICES.txt` and `SOURCE.txt`. The installer preserves them next to
  the binary. The inventory is a CI report, not part of the download.

The dependency-free collector is
[`services/cli/tools/license-notices/main.go`](../services/cli/tools/license-notices/main.go).
It uses package license/notice files, preserves their original text, and
deduplicates by SHA-256. Where a package omits a separate legal file, it retains
the exact package metadata and requires the declared license's complete text.
These cases are marked `metadataOnly`; metadata does not verify copyright ownership.
Missing installed required packages, unresolved licenses and missing legal text
fail the build. No upstream license lookups or application configuration are read. Go packaging
resolves the locked module graph with `go mod download all` before collection.

`standard/` contains supplemental standard license texts for packages that omit them;
`notices/` contains original notices for copied frontend material. These small
original legal inputs are versioned, unlike generated dependency catalogs.
Npm builds need Go (available in the devcontainer), or a compiled collector via
`GAMEHUB_NOTICE_GENERATOR`. Docker and CI supply it automatically.

For example, from the repository root inside the devcontainer:

```sh
go run services/cli/tools/license-notices/main.go -kind go -input services/cli \
  -legal licensing -output /tmp/gamehub-cli-legal
```

## Source availability and modified deployments

Official builds pin source links to the workflow's exact commit. The manual
release workflow includes `gamehub-source-VERSION.tar.gz`, build scripts,
dependency manifests and checksums alongside binary downloads. Environment files
are excluded before their contents are opened. See the
[configuration guide](../docs/guides/configuration.md).

For forks, pass `SOURCE_REPOSITORY=owner/repo` and `REVISION=<full commit SHA>`
as Docker build arguments. For npm builds use `GAMEHUB_SOURCE_REPOSITORY`,
`GAMEHUB_SOURCE_REVISION` and `GAMEHUB_BUILD_VERSION`. These are public source
metadata. Versioned builds require an exact revision; development builds disclose
when no revision is pinned. Publish complete matching source, including local
modifications and build/installation scripts, and keep the source offer accurate.

Preserve source access and notices required by bundled copyleft dependencies.
Sharp/libvips includes LGPL native components; the generated report includes the
native component versions and upstream build/source location. Website dependencies
include MPL-1.1/EPL-2.0 components, which is why the site tooling has a separate
MIT boundary. An SBOM or license label alone does not satisfy source obligations.

The review of declared package licenses is not an exhaustive audit of native
codecs, container operating-system packages, trademarks or provider artwork/data.
Base images retain their own notices. These third-party materials are not granted
to others under GameHub's AGPL.
