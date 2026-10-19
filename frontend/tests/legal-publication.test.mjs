import assert from 'node:assert/strict';
import {mkdtempSync, readFileSync, rmSync, writeFileSync} from 'node:fs';
import {tmpdir} from 'node:os';
import path from 'node:path';
import test from 'node:test';
import {prepareLegal} from '../scripts/prepare-legal.mjs';

function fixture(t) {
  const directory = mkdtempSync(path.join(tmpdir(), 'gamehub-legal-'));
  t.after(() => rmSync(directory, {recursive: true, force: true}));
  writeFileSync(path.join(directory, 'LICENSE'), 'License fixture');
  writeFileSync(path.join(directory, 'THIRD_PARTY_NOTICES.txt'), 'Original notices');
  return {legalDirectory: directory, output: path.join(directory, 'public')};
}

test('published source references use the exact build revision and repository', t => {
  const config = fixture(t);
  const revision = 'a'.repeat(40);
  prepareLegal({...config, repository: 'contributor/gamehub', revision, version: '1.2.3'});
  const page = readFileSync(path.join(config.output, 'index.html'), 'utf8');
  assert.ok(page.includes(`https://github.com/contributor/gamehub/tree/${revision}`));
  assert.ok(page.includes('/releases/download/v1.2.3/gamehub-source-1.2.3.tar.gz'));
  assert.ok(page.includes('without warranty'));
  assert.ok(page.includes('THIRD_PARTY_NOTICES.txt'));
  assert.equal(readFileSync(path.join(config.output, 'LICENSE.txt'), 'utf8'), 'License fixture');
  assert.equal(readFileSync(path.join(config.output, 'THIRD_PARTY_NOTICES.txt'), 'utf8'), 'Original notices');
});

test('development builds do not claim upstream source covers local changes', t => {
  const config = fixture(t);
  prepareLegal(config);
  const page = readFileSync(path.join(config.output, 'index.html'), 'utf8');
  assert.ok(page.includes('no pinned source revision'));
  assert.ok(!page.includes('/releases/download/'));
});

test('versioned builds cannot publish a missing or malformed source reference', t => {
  const config = fixture(t);
  for (const input of [{version: '1.2.3'}, {revision: 'main'}, {repository: '../other'},
    {repository: 'user/repo?token=value'}, {version: '<script>'}]) {
    assert.throws(() => prepareLegal({...config, ...input}));
  }
});
