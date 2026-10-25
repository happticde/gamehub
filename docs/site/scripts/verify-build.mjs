import assert from 'node:assert/strict';
import {existsSync, readFileSync} from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';

const site = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const versions = JSON.parse(readFileSync(path.join(site, 'versions.json'), 'utf8'));
const metadata = JSON.parse(readFileSync(path.join(site, '.generated/metadata.json'), 'utf8'));
const repository = process.env.GITHUB_REPOSITORY || 'happticde/gamehub';
const base = process.env.DOCS_BASE_URL || '/' + repository.split('/')[1] + '/';
for (const snapshot of metadata.snapshots) {
  const version = snapshot.version === 'current' ? 'next' : snapshot.version;
  const isLatest = versions.length ? version === versions[0] : snapshot.version === 'current';
  const indexPath = path.join(site, 'build', isLatest ? '' : version, 'search-index.json');
  const titles = JSON.parse(readFileSync(indexPath, 'utf8'))[0].documents;
  assert.ok(titles.length >= snapshot.pages, 'Search index omitted handbook pages: ' + version);
  assert.ok(titles.every(document => document.u.startsWith(base + version + '/')),
    'Search results escape their documentation version: ' + version);
}
const policyPath = path.join(site, 'publication.json');
if (existsSync(policyPath)) {
  for (const version of JSON.parse(readFileSync(policyPath, 'utf8')).withdrawnVersions) {
    assert.equal(existsSync(path.join(site, 'build', version)), false, 'Withdrawn edition remains in the output');
  }
}
if (process.env.DOCS_PREVIEW_SHA) {
  const homepage = readFileSync(path.join(site, 'build/index.html'), 'utf8');
  assert.match(homepage, /Dev preview/);
  assert.match(homepage, /noindex, nofollow/);
}
console.log('Verified complete, version-scoped local search indexes and publication boundaries.');
