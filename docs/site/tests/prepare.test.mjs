import assert from 'node:assert/strict';
import {execFileSync} from 'node:child_process';
import {mkdtempSync, mkdirSync, readFileSync, rmSync, symlinkSync, writeFileSync} from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {test} from 'node:test';
import {prepare, publishedTags, rewriteSourceLinks, validateManifest, validatePublication} from '../scripts/prepare.mjs';

const manifest = {schemaVersion: 1, sections: [{label: 'Start', items: ['index.md', 'guides/install.md']}]};
const release = tag => ({tag_name: tag, draft: false, prerelease: false, published_at: '2026-10-15T10:00:00Z'});

function fixture(t) {
  const root = mkdtempSync(path.join(os.tmpdir(), 'gamehub-docs-'));
  t.after(() => rmSync(root, {recursive: true, force: true}));
  const git = (...args) => execFileSync('git', args, {cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe']}).trim();
  const write = (name, text) => { mkdirSync(path.dirname(path.join(root, name)), {recursive: true}); writeFileSync(path.join(root, name), text); };
  git('init');
  git('config', 'user.name', 'Documentation tests');
  git('config', 'user.email', 'docs-test@example.invalid');
  write('README.md', 'Historical product');
  git('add', '.'); git('commit', '-m', 'Initial product'); git('tag', 'v0.1.0');
  write('docs/site/content.json', JSON.stringify(manifest));
  write('docs/index.md', '# Handbook\n\nOriginal version');
  write('docs/guides/install.md', '# Install\n\n[Home](../index.md)\n[Source](../../README.md)');
  git('add', '.'); git('commit', '-m', 'Add handbook');
  git('tag', '-a', 'v0.2.0', '-m', 'Annotated product release');
  const releaseSha = git('rev-parse', 'HEAD');
  write('docs/index.md', '# Handbook\n\nUnreleased change');
  git('add', '.'); git('commit', '-m', 'Change next');
  return {root, output: path.join(root, 'docs/site'), git, write, releaseSha};
}

test('only published stable releases are eligible, in numeric semantic order', () => {
  assert.deepEqual(publishedTags([release('v0.9.0'), release('v0.10.0'), release('v1.0.0'),
    {...release('v2.0.0'), draft: true}, {...release('v3.0.0'), prerelease: true},
    {...release('v4.0.0'), published_at: null}, release('v9.0.0-rc.1'), release('../main')]),
  ['v1.0.0', 'v0.10.0', 'v0.9.0']);
  assert.throws(() => publishedTags([release('v1.0.0'), release('v1.0.0')]), /Duplicate/);
  assert.throws(() => publishedTags({}), /release list/);
});

test('public manifest rejects traversal, private context, environment names, duplicates, and missing home', () => {
  for (const name of ['../README.md', '/index.md', 'agents/history.md', 'site/README.md', 'source-review-internal.md', '.env.md', 'guides/../../private.md']) {
    assert.throws(() => validateManifest({schemaVersion: 1, sections: [{label: 'Docs', items: ['index.md', name]}]}));
  }
  assert.throws(() => validateManifest({schemaVersion: 1, sections: [{label: 'Docs', items: ['index.md', 'index.md']}]}), /duplicate/);
  assert.throws(() => validateManifest({schemaVersion: 1, sections: [{label: 'Docs', items: ['guide.md']}]}), /index.md/);
  assert.throws(() => validateManifest({...manifest, schemaVersion: 2}), /Unsupported/);
});

test('historical snapshots use exact annotated-tag contents while current stays independent', t => {
  const f = fixture(t);
  const result = prepare({...f, releases: [release('v0.1.0'), release('v0.2.0')]});
  assert.deepEqual(result.skipped, ['v0.1.0']);
  assert.equal(result.snapshots[1].commit, f.releaseSha);
  assert.match(readFileSync(path.join(f.output, 'versioned_docs/version-0.2.0/index.md'), 'utf8'), /Original version/);
  assert.doesNotMatch(readFileSync(path.join(f.output, 'versioned_docs/version-0.2.0/index.md'), 'utf8'), /Unreleased change/);
  assert.match(readFileSync(path.join(f.output, '.generated/current/index.md'), 'utf8'), /Unreleased change/);
  assert.match(readFileSync(path.join(f.output, 'versioned_docs/version-0.2.0/guides/install.md'), 'utf8'), new RegExp(`/blob/${f.releaseSha}/README.md`));
  assert.deepEqual(JSON.parse(readFileSync(path.join(f.output, 'versions.json'), 'utf8')), ['0.2.0']);
  const next = prepare({...f, candidate: '0.3.0'});
  assert.deepEqual(next.snapshots.map(snapshot => snapshot.version), ['current', '0.3.0']);
  assert.throws(() => readFileSync(path.join(f.output, 'versioned_docs/version-0.2.0/index.md')), /ENOENT/);
});

test('a missing tag or historical page fails instead of falling back to current', t => {
  const f = fixture(t);
  assert.throws(() => prepare({...f, releases: [release('v9.0.0')]}));
  f.git('rm', 'docs/guides/install.md'); f.git('commit', '-m', 'Broken historical manifest'); f.git('tag', 'v0.4.0');
  f.write('docs/guides/install.md', 'Current page cannot repair historical release');
  assert.throws(() => prepare({...f, releases: [release('v0.4.0')]}), /Missing or non-regular/);
});

test('a branch preview reads only the selected immutable Markdown snapshot with the trusted renderer', t => {
  const f = fixture(t);
  const result = prepare({...f, currentRef: f.releaseSha});
  assert.deepEqual(result.snapshots.map(snapshot => snapshot.version), ['current']);
  assert.equal(result.snapshots[0].commit, f.releaseSha);
  assert.match(readFileSync(path.join(f.output, '.generated/current/index.md'), 'utf8'), /Original version/);
  assert.doesNotMatch(readFileSync(path.join(f.output, '.generated/current/index.md'), 'utf8'), /Unreleased change/);
  assert.throws(() => prepare({...f, currentRef: 'main'}), /immutable/);
});

test('withdrawn editions disappear and corrections use a pinned commit without moving product tags', t => {
  const f = fixture(t);
  const originalTag = f.git('rev-parse', 'v0.2.0^{commit}');
  const correction = f.git('rev-parse', 'HEAD');
  f.write('docs/site/publication.json', JSON.stringify({
    schemaVersion: 1, withdrawnVersions: ['0.1.0'], sourceOverrides: {'0.2.0': correction},
  }));
  f.write('docs/index.md', '# New Next content after the correction');
  const result = prepare({...f, releases: [release('v0.1.0'), release('v0.2.0')]});
  assert.deepEqual(result.snapshots.map(snapshot => snapshot.version), ['current', '0.2.0']);
  assert.equal(result.snapshots[1].commit, correction);
  assert.equal(f.git('rev-parse', 'v0.2.0^{commit}'), originalTag);
  assert.match(readFileSync(path.join(f.output, 'versioned_docs/version-0.2.0/index.md'), 'utf8'), /Unreleased change/);
  assert.doesNotMatch(readFileSync(path.join(f.output, 'versioned_docs/version-0.2.0/index.md'), 'utf8'), /New Next content/);
  assert.throws(() => readFileSync(path.join(f.output, 'versioned_docs/version-0.1.0/index.md')), /ENOENT/);
  assert.throws(() => prepare({...f, candidate: '0.1.0'}), /withdrawn/);
  f.write('docs/site/publication.json', JSON.stringify({
    schemaVersion: 1, withdrawnVersions: [], sourceOverrides: {'0.2.0': 'a'.repeat(40)},
  }));
  assert.throws(() => prepare({...f, releases: [release('v0.2.0')]}));
});

test('publication policy requires explicit versions and immutable non-conflicting source commits', () => {
  for (const policy of [
    {schemaVersion: 2, withdrawnVersions: [], sourceOverrides: {}},
    {schemaVersion: 1, withdrawnVersions: ['../private'], sourceOverrides: {}},
    {schemaVersion: 1, withdrawnVersions: ['0.1.0', '0.1.0'], sourceOverrides: {}},
    {schemaVersion: 1, withdrawnVersions: [], sourceOverrides: {'0.2.0': 'main'}},
    {schemaVersion: 1, withdrawnVersions: ['0.2.0'], sourceOverrides: {'0.2.0': 'a'.repeat(40)}},
  ]) assert.throws(() => validatePublication(policy));
});

test('the public handbook contains only user guides and no links to internal project documents', () => {
  const root = path.resolve(import.meta.dirname, '../../..');
  const manifest = JSON.parse(readFileSync(path.join(root, 'docs/site/content.json'), 'utf8'));
  const included = validateManifest(manifest);
  for (const name of included) {
    assert.ok(name === 'index.md' || /^guides\//.test(name), name);
    assert.doesNotMatch(name, /(?:architecture|development)/);
    const markdown = readFileSync(path.join(root, 'docs', name), 'utf8');
    for (const [, target] of markdown.matchAll(/\]\(([^\s)]+)\)/g)) {
      if (/^(?:[a-z][a-z\d+.-]*:|#|\/)/i.test(target)) continue;
      const destination = target.split(/[?#]/)[0];
      if (!destination.endsWith('.md')) continue;
      const resolved = path.posix.normalize(path.posix.join(path.posix.dirname(name), destination));
      assert.ok(included.has(resolved), `Link outside the public handbook in ${name}: ${target}`);
    }
  }
});

test('symlinked files and parent directories cannot publish files outside the public input tree', t => {
  const f = fixture(t);
  rmSync(path.join(f.root, 'docs/index.md'));
  symlinkSync(path.join(f.root, 'README.md'), path.join(f.root, 'docs/index.md'));
  assert.throws(() => prepare(f), /Symlink/);
  rmSync(path.join(f.root, 'docs/index.md')); f.write('docs/index.md', '# Home');
  rmSync(path.join(f.root, 'docs/guides'), {recursive: true});
  symlinkSync(f.root, path.join(f.root, 'docs/guides'));
  assert.throws(() => prepare(f), /Symlink/);
});

test('content and navigation fingerprints change independently of unrelated product commits', t => {
  const f = fixture(t);
  const before = prepare(f).contentHash;
  f.write('application.cs', 'New code'); f.git('add', 'application.cs'); f.git('commit', '-m', 'Code only');
  assert.equal(prepare(f).contentHash, before);
  f.write('docs/site/content.json', JSON.stringify({...manifest, sections: [{...manifest.sections[0], label: 'New navigation'}]}));
  assert.notEqual(prepare(f).contentHash, before);
});

test('source links preserve fences, internal links, fragments, and reject local files', () => {
  const source = '[Page](../index.md#start)\n[Code](../../services/core/Program.cs#L10)\n```md\n[Example](../../README.md)\n```';
  const result = rewriteSourceLinks(source, 'guides/install.md', new Set(['index.md']), 'owner/repo', 'abcdef');
  assert.match(result, /\[Page\]\(\.\.\/index.md#start\)/);
  assert.match(result, /https:\/\/github.com\/owner\/repo\/blob\/abcdef\/services\/core\/Program.cs#L10/);
  assert.match(result, /\[Example\]\(\.\.\/\.\.\/README.md\)/);
  assert.throws(() => rewriteSourceLinks('[Local](file:///private/path)', 'index.md', new Set(), 'owner/repo', 'main'), /Local file/);
  assert.throws(() => rewriteSourceLinks('[Unsafe](../../.env)', 'guides/install.md', new Set(), 'owner/repo', 'main'), /Disallowed/);
});

test('manual publication workflow has no automatic trigger or product release coupling', () => {
  const root = path.resolve(import.meta.dirname, '../../..');
  const workflow = readFileSync(path.join(root, '.github/workflows/docs.yml'), 'utf8');
  const triggers = workflow.split('\non:\n')[1].split('\npermissions:')[0];
  assert.match(triggers, /^  workflow_dispatch:/m);
  assert.doesNotMatch(triggers, /^  (?:push|pull_request|release|schedule|workflow_run|workflow_call):/m);
  assert.match(workflow, /SOURCE_REF.*github.ref/);
  assert.match(workflow, /test "\$SOURCE_REF" = refs\/heads\/main/);
  assert.match(workflow, /if: needs.build.outputs.changed == 'true'/);
  assert.match(workflow, /force:/);
  assert.doesNotMatch(readFileSync(path.join(root, '.github/workflows/release.yml'), 'utf8'), /uses:.*docs\.yml|gh workflow run.*docs/);
});
