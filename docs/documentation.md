# Maintain and publish documentation

GameHub's handbook uses Docusaurus. Its website is an independent npm project under `docs/site/`;
the maintained Markdown pages remain in `docs/`. The explicit `docs/site/content.json` manifest
defines the public pages and navigation. Agent context, session history, and source-review notes
are excluded. Adding a file to Git does not automatically publish it.

## Check or preview locally

From the repository root, use the documentation Make targets. They run npm directly inside the
devcontainer, or automatically use `docker exec` when called from the host. The devcontainer must
already be running; the targets do not start application services or publish anything.

```bash
make docs-install
make docs-check
make docs-preview
```

Forward port **3001** from the devcontainer and open `http://localhost:3001/gamehub/`. The production
preview rebuilds before serving so the search index and pages are current. Stop it with `Ctrl+C`.
To preview the version selector with a local candidate, use:

```bash
make docs-preview DOCS_VERSION=0.1.0
```

This creates no Git tag or product release. Omit `DOCS_VERSION` on the next build to return to
Next-only documentation. Use `DOCS_PORT=3002` for another port. `make docs-dev` starts the development
preview; restart it after editing original Markdown sources so the staged copies are refreshed.
`make help` lists all targets, including separate `docs-test` and `docs-build` commands.
For a differently named container or mount, override `DOCS_CONTAINER` or `DOCS_WORKSPACE`.
The production preview uses the devcontainer's Python static server with an isolated temporary
document root. This also serves dotted version directories correctly; the Docusaurus preview
server in the pinned release returns 404 for their index pages. The temporary mount is removed
when the preview stops. No additional package dependency is required.

The equivalent npm commands are also available. Run framework and dependency commands inside the
development container:

```bash
docker exec -w /workspaces/game-library-backend/docs/site game-library-backend_devcontainer-app-1 npm ci
docker exec -w /workspaces/game-library-backend/docs/site game-library-backend_devcontainer-app-1 npm run check
docker exec -w /workspaces/game-library-backend/docs/site game-library-backend_devcontainer-app-1 npm start
```

Forward port 3000 from the devcontainer for the development preview. If that port is already in
use, pass `--port 3001` to the Docusaurus start command. A production preview uses `npm run serve`.
With the npm script, pass additional options as `npm start -- --port 3001`.
Local search is indexed during the production build and must be tested in that preview.
The default project base path is `/gamehub/`.

`npm run check` runs the documentation tooling contracts and a complete static build. Missing
pages, broken internal Markdown links, and broken internal site links fail the build. External
websites are not exhaustively checked on each build. CommonMark `.md` is used for existing
runbooks; `.mdx` is reserved for site components if explicitly introduced later.

## Change content

Edit the original Markdown page, not generated output. Add new pages to a section in
`docs/site/content.json`. Keep links to other handbook pages relative and use English content.
Links to source files outside `docs/` are rewritten to GitHub at the corresponding source ref.
Do not use local `file://` links, real secrets, private account details, or deployment logs.

Generated current pages, version directories, sidebars, search indexes, and build outputs are
ignored by Git. Docusaurus dependencies have their own lockfile and Dependabot coverage. Product
frontend dependencies remain independent.

The lockfile uses reviewed security overrides for `tinypool`, `serialize-javascript`,
`postcss-selector-parser`, and the CommonJS-compatible `uuid` used by `sockjs`. Recheck these
overrides when upgrading Docusaurus and remove them once its supported dependency ranges include
the fixes. Mermaid's layout peer stays on the stable Mermaid-11-compatible 0.1 line required
by this Docusaurus release. Unfixed findings remain visible under the repository's Trivy policy;
no documentation-specific vulnerability exclusions are added.

## Automated verification, manual publication

| Event | Documentation behavior | Product behavior |
| --- | --- | --- |
| Handbook content/config/dependency or docs-only root Makefile PR or main push | Build, contracts, secret scan, targeted dependency scan | No application/container builds for docs-only changes |
| Agent-context-only change | Existing secret scan and aggregate gate | No application/container builds |
| Mixed documentation and code change | Documentation checks plus selected code checks | Affected product components only |
| Manual product release | Full checks, including a numbered documentation candidate | Existing verified product publication |
| Manual **Publish documentation (manual)** run | Build all documented published versions and Next, then deploy if changed | No product builds, version bump, or release |

The documentation publication workflow has only `workflow_dispatch`. There is no automatic
publication on merge, tags, releases, schedules, or successful CI. Product releases do not call
it. A lightweight aggregate CI still runs for documentation PRs so required checks cannot remain
pending. Changes to shared CI/security policy intentionally retain full verification; changes to
the dedicated Pages workflow run workflow tooling and documentation checks only.

## Activate GitHub Pages when ready

Repository visibility and Pages settings are operator decisions. Nothing in the implementation
changes repository visibility, billing, Pages settings, or a live deployment.

1. Confirm the intended public content in `content.json`. A private source repository does not
   make a normal Pages site private.
2. Confirm Pages is available for the repository's current plan/visibility. Public repositories
   support Pages on GitHub Free; private organization repositories need a supporting paid plan.
3. In **Settings → Pages → Build and deployment**, choose **GitHub Actions** as the source.
4. Allow the `github-pages` environment to deploy from `main`. Configure reviewers if desired.
5. Merge the documentation implementation into `main`.
6. Open **Actions → Publish documentation (manual) → Run workflow**, choose **main**, and run.
7. Confirm both build and deployment succeed, then open the reported Pages URL. Check the home
   page, search, a nested page, mobile navigation, and the version selector.

The default URL is `https://happticde.github.io/gamehub/`. Repository forks derive owner and
project from `GITHUB_REPOSITORY`. For a custom domain or project path, consistently configure
`DOCS_URL` and `DOCS_BASE_URL` in the build environment and follow GitHub's domain setup guide.
The workflow intentionally does not create a Pages site automatically.

Sources: [GitHub Pages availability](https://docs.github.com/en/pages/getting-started-with-github-pages/what-is-github-pages),
[custom deployment workflows](https://docs.github.com/en/pages/getting-started-with-github-pages/using-custom-workflows-with-github-pages).

## Versions and reproducibility

The home page chooses the highest documented stable semantic version, or Next if none exists.
Numbered documentation has permanent paths such as `/gamehub/0.2.0/`; Next lives at
`/gamehub/next/`. Older numbered versions show Docusaurus's older-version banner.

The manual publisher fetches all GitHub Release metadata and full tag history. Only published,
non-draft, non-prerelease `vMAJOR.MINOR.PATCH` releases are eligible. For each eligible release,
it resolves the tag to a commit and reads that commit's own content manifest and Markdown pages.
The latest renderer builds these frozen sources into Docusaurus's version directories. It never
fills a missing old page with current content. Releases predating the manifest are explicitly
skipped; missing tags, unsupported manifests, and malformed snapshots fail rather than silently
substituting another version.

`DOCS_RELEASES_FILE` can point to an API-shaped JSON release array for a local historical build.
`DOCS_CANDIDATE_VERSION` adds a candidate snapshot from the checked-out source for release CI.
The candidate validates the future numbered URL before publication; it is not itself a published
release and does not deploy Pages.

Generated `.generated/metadata.json` records source commits and page counts. The application
version authority remains the Git release tag, never `docs/site/package.json`.

## Skip unchanged deployments and recover

Every manual publication verifies and builds before attempting deployment. A fingerprint covers
public content, navigation, renderer/dependencies, Pages workflow, and documented release versions.
It is compared with the state artifact from the previous successful manual publication run.
Matching inputs skip the actual Pages deployment. A new product version changes that fingerprint
even when its prose is unchanged, because the version selector and release mapping changed.

If the previous state artifact has expired or no successful run exists, publication proceeds.
The **force** input deliberately republishes unchanged inputs, for example after Pages settings
or domain recovery. A failed build/scan cannot deploy; a failed deployment does not become the
baseline for future comparisons. Concurrent publication runs are serialized.

Fix broken content through a normal PR and rerun manually. Existing numbered content is frozen
to the product tag: a correction on `main` changes Next and subsequent releases. Updating an
older handbook independently would require a separately reviewed correction mechanism; do not
move release tags to change documentation.

## Product release guard

`prepare-release.sh` checks the complete changed paths since the latest reachable stable product
tag before calculating a new version. If only documentation, agent context, or the dedicated
Pages workflow changed, preparation fails without builds or publication. This applies to explicit
patch/minor/major choices as well as auto. A first release evaluates tracked product inputs.
Changes to shared build/security tooling remain releasable product inputs.
