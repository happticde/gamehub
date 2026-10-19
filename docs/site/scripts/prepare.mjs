import {execFileSync} from 'node:child_process';
import {createHash} from 'node:crypto';
import {copyFileSync, lstatSync, mkdirSync, readFileSync, rmSync, writeFileSync} from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {generateNotices} from '../../../licensing/generate.mjs';

const site = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const stableTag = /^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/;

function git(root, ...args) {
  return execFileSync('git', args, {cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe']}).trimEnd();
}

export function validateManifest(manifest) {
  if (manifest.schemaVersion !== 1 || !Array.isArray(manifest.sections) || !manifest.sections.length) {
    throw new Error('Unsupported or empty documentation manifest');
  }
  const seen = new Set();
  for (const section of manifest.sections) {
    if (typeof section.label !== 'string' || !section.label || !Array.isArray(section.items) || !section.items.length) {
      throw new Error('Every documentation section needs a label and pages');
    }
    for (const name of section.items) {
      // Explicit public Markdown allowlist; never enumerate or open arbitrary checkout files.
      if (typeof name !== 'string' || !/^(?:[A-Za-z0-9_-]+\/)*[A-Za-z0-9_-]+\.md$/.test(name)
          || name.startsWith('agents/') || name.startsWith('site/') || name.startsWith('source-review-') || seen.has(name)) {
        throw new Error(`Invalid, private, or duplicate documentation path: ${name}`);
      }
      seen.add(name);
    }
  }
  if (!seen.has('index.md')) throw new Error('The documentation needs index.md');
  return seen;
}

export function publishedTags(releases) {
  if (!Array.isArray(releases)) throw new Error('Expected a GitHub release list');
  const tags = releases.filter(release => release.draft === false && release.prerelease === false
    && typeof release.published_at === 'string' && stableTag.test(release.tag_name)).map(release => release.tag_name);
  if (new Set(tags).size !== tags.length) throw new Error('Duplicate published release');
  return tags.sort((a, b) => {
    const left = a.slice(1).split('.').map(BigInt);
    const right = b.slice(1).split('.').map(BigInt);
    for (let i = 0; i < 3; i++) if (left[i] !== right[i]) return left[i] > right[i] ? -1 : 1;
    return 0;
  });
}

function readSource(root, ref, name) {
  if (ref) {
    const entry = git(root, 'ls-tree', ref, '--', name);
    if (!entry.startsWith('100644 blob ') && !entry.startsWith('100755 blob ')) {
      throw new Error(`Missing or non-regular documentation input: ${ref}:${name}`);
    }
    return git(root, 'show', `${ref}:${name}`) + '\n';
  }
  const fullPath = path.join(root, name);
  // Check every parent as well: a symlinked guides directory must not escape the allowlist.
  let current = root;
  for (const part of name.split('/')) {
    current = path.join(current, part);
    if (lstatSync(current).isSymbolicLink()) throw new Error(`Symlink documentation input: ${name}`);
  }
  return readFileSync(fullPath, 'utf8');
}

export function rewriteSourceLinks(markdown, name, included, repository, ref) {
  // Keep code examples byte-for-byte. Only Markdown destinations outside fences are rewritten.
  let fence = null;
  return markdown.split('\n').map(line => {
    const marker = line.match(/^\s*(`{3,}|~{3,})/);
    if (marker) {
      if (!fence) fence = marker[1][0];
      else if (marker[1][0] === fence) fence = null;
      return line;
    }
    if (fence) return line;
    return line.replace(/\]\(([^\s)]+)\)/g, (match, target) => {
      if (/^(?:[a-z][a-z\d+.-]*:|#|\/)/i.test(target)) {
        if (target.startsWith('file:')) throw new Error(`Local file URL in public documentation: ${name}`);
        return match;
      }
      const [destination, suffix = ''] = target.split(/(?=[#?])/s, 2);
      const resolved = path.posix.normalize(path.posix.join(path.posix.dirname(name), destination));
      if (included.has(resolved)) return match;
      // A typo in a docs link must fail the Docusaurus build rather than become a silent GitHub 404.
      if (!resolved.startsWith('../')) return match;
      const repoPath = path.posix.normalize(path.posix.join('docs', resolved));
      if (repoPath.startsWith('../') || repoPath.split('/').some(part => part === '.env' || part.startsWith('.env.'))) {
        throw new Error(`Disallowed source link in ${name}`);
      }
      return `](https://github.com/${repository}/blob/${encodeURIComponent(ref)}/${repoPath}${suffix})`;
    });
  }).join('\n');
}

function writeJson(destination, value) {
  mkdirSync(path.dirname(destination), {recursive: true});
  writeFileSync(destination, JSON.stringify(value, null, 2) + '\n');
}

export function prepare({root, output, releases = [], repository = 'jfk1987/gamehub', candidate = ''}) {
  if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repository)) throw new Error('Invalid GitHub repository');
  if (candidate && !stableTag.test(`v${candidate}`)) throw new Error('Invalid candidate version');
  const currentManifest = JSON.parse(readSource(root, null, 'docs/site/content.json'));
  validateManifest(currentManifest);
  const tags = publishedTags(releases);
  const snapshots = [];
  const skipped = [];
  for (const tag of tags) {
    // Resolve to the immutable commit once, including annotated tags. Never render old docs from main.
    const sha = git(root, 'rev-parse', '--verify', `${tag}^{commit}`);
    if (!git(root, 'ls-tree', sha, '--', 'docs/site/content.json')) {
      skipped.push(tag);
      continue;
    }
    const manifest = JSON.parse(readSource(root, sha, 'docs/site/content.json'));
    validateManifest(manifest);
    snapshots.push({version: tag.slice(1), ref: sha, manifest});
  }
  if (candidate) {
    if (snapshots.some(snapshot => snapshot.version === candidate)) throw new Error('Candidate version already published');
    snapshots.unshift({version: candidate, ref: null, manifest: currentManifest});
  }
  for (const name of ['.generated', 'versioned_docs', 'versioned_sidebars']) {
    rmSync(path.join(output, name), {recursive: true, force: true});
  }
  const digest = createHash('sha256');
  const metadata = [];
  const currentSha = git(root, 'rev-parse', 'HEAD');
  function render({version, ref, manifest}) {
    const included = validateManifest(manifest);
    const destination = path.join(output, version === 'current' ? '.generated/current' : `versioned_docs/version-${version}`);
    const sourceRef = ref || (version === 'current' ? 'main' : currentSha);
    digest.update(JSON.stringify([version, manifest]));
    const sidebar = manifest.sections.map(section => ({
      type: 'category', label: section.label, collapsed: section.label !== 'Start here',
      items: section.items.map(name => name.slice(0, -3)),
    }));
    for (const name of included) {
      const raw = readSource(root, ref, `docs/${name}`);
      const markdown = rewriteSourceLinks(raw, name, included, repository, sourceRef);
      const frontmatter = [
        '---', `id: ${path.posix.basename(name, '.md')}`,
        ...(name === 'index.md' ? ['slug: /', 'title: GameHub handbook'] : []),
        `custom_edit_url: https://github.com/${repository}/blob/${sourceRef}/docs/${name}`, '---', '',
      ].join('\n');
      const target = path.join(destination, name);
      mkdirSync(path.dirname(target), {recursive: true});
      writeFileSync(target, frontmatter + markdown);
      digest.update(JSON.stringify([version, name, markdown]));
    }
    if (version === 'current') writeJson(path.join(output, '.generated/sidebar.json'), {handbook: sidebar});
    else writeJson(path.join(output, `versioned_sidebars/version-${version}-sidebars.json`), {handbook: sidebar});
    metadata.push({version, commit: ref || currentSha, pages: included.size});
  }
  render({version: 'current', ref: null, manifest: currentManifest});
  snapshots.forEach(render);
  writeJson(path.join(output, 'versions.json'), snapshots.map(snapshot => snapshot.version));
  const result = {schemaVersion: 1, repository, snapshots: metadata, skipped, contentHash: digest.digest('hex')};
  writeJson(path.join(output, '.generated/metadata.json'), result);
  console.log(`Prepared ${metadata.length} documentation stand(s); ${skipped.length} older release(s) have no documentation manifest.`);
  return result;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const releases = process.env.DOCS_RELEASES_FILE ? JSON.parse(readFileSync(process.env.DOCS_RELEASES_FILE, 'utf8')) : [];
  prepare({root: path.resolve(site, '../..'), output: site, releases,
    repository: process.env.GITHUB_REPOSITORY || 'jfk1987/gamehub',
    candidate: process.env.DOCS_CANDIDATE_VERSION || ''});
  const legal = path.join(site, 'static/legal');
  mkdirSync(legal, {recursive: true});
  const generated = path.join(site, '.generated/legal');
  generateNotices({input: site, output: generated});
  copyFileSync(path.join(generated, 'THIRD_PARTY_NOTICES.txt'), path.join(legal, 'THIRD_PARTY_NOTICES.txt'));
  copyFileSync(path.resolve(site, '../../LICENSE'), path.join(legal, 'AGPL-3.0-only.txt'));
  copyFileSync(path.join(site, 'LICENSE'), path.join(legal, 'MIT.txt'));

}
