import assert from 'node:assert/strict';
import {execFileSync, spawnSync} from 'node:child_process';
import {chmodSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync} from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {test} from 'node:test';

const root = path.resolve(import.meta.dirname, '../../..');
const workflow = readFileSync(path.join(root, '.github/workflows/docs.yml'), 'utf8');
const baseline = workflow.match(/- name: Prepare the stable handbook baseline\n[\s\S]*?run: \|\n([\s\S]*?)(?=\n      - name:)/)[1].replace(/^ {10}/gm, '');

function fixture(t, {pointer = true, expired = false, conclusion = 'success'} = {}) {
  const directory = mkdtempSync(path.join(os.tmpdir(), 'gamehub-publication-'));
  t.after(() => rmSync(directory, {recursive: true, force: true}));
  const write = (name, value) => {
    const file = path.join(directory, name);
    mkdirSync(path.dirname(file), {recursive: true});
    writeFileSync(file, value);
  };
  write('docs/site/build/index.html', 'Unpublished main content');
  write('bin/gh', [
    '#!/usr/bin/env node',
    "const fs = require('node:fs');",
    "const target = process.argv[3];",
    "if (target.includes('workflows/docs.yml/runs?')) process.stdout.write('7');",
    "else if (target.endsWith('/runs/7/artifacts')) process.stdout.write('11');",
    "else if (target.endsWith('/artifacts/11/zip')) process.stdout.write(fs.readFileSync(process.env.MOCK_STATE));",
    "else if (target.endsWith('/runs/5')) process.stdout.write(process.env.MOCK_CONCLUSION);",
    "else if (target.endsWith('/runs/5/artifacts')) process.stdout.write(process.env.MOCK_EXPIRED === 'true' ? '' : '22');",
    "else if (target.endsWith('/artifacts/22/zip')) process.stdout.write(fs.readFileSync(process.env.MOCK_SITE));",
    "else process.exit(1);",
  ].join('\n'));
  chmodSync(path.join(directory, 'bin/gh'), 0o755);
  const archive = (name, entries) => execFileSync('python3', ['-c',
    'import json,sys,zipfile\nwith zipfile.ZipFile(sys.argv[1], "w") as z:\n for name,value in json.loads(sys.argv[2]).items(): z.writestr(name,value)',
    path.join(directory, name), JSON.stringify(entries)]);
  archive('state.zip', pointer ? {'stable-run-id.txt': '5\n', 'fingerprint.txt': 'previous'} : {'fingerprint.txt': 'legacy'});
  archive('site.zip', {'site/index.html': 'Previously published stable content', 'fingerprint.txt': 'stable'});
  mkdirSync(path.join(directory, 'temporary'), {recursive: true});
  const result = spawnSync('bash', ['-euo', 'pipefail', '-c', baseline], {
    cwd: directory, encoding: 'utf8', env: {...process.env,
      PATH: path.join(directory, 'bin') + path.delimiter + process.env.PATH,
      MODE: 'preview', RUNNER_TEMP: path.join(directory, 'temporary'),
      GITHUB_REPOSITORY: 'owner/repo', MOCK_STATE: path.join(directory, 'state.zip'),
      MOCK_SITE: path.join(directory, 'site.zip'), MOCK_EXPIRED: String(expired),
      MOCK_CONCLUSION: conclusion,
    },
  });
  return {directory, result};
}

test('preview mode restores the last successful stable bytes instead of unpublished main content', t => {
  const f = fixture(t);
  assert.equal(f.result.status, 0, f.result.stderr);
  assert.equal(readFileSync(path.join(f.directory, 'temporary/docs-stable/site/index.html'), 'utf8'), 'Previously published stable content');
  assert.equal(readFileSync(path.join(f.directory, 'docs/site/build/index.html'), 'utf8'), 'Unpublished main content');
  assert.equal(readFileSync(path.join(f.directory, 'temporary/docs-stable-run-id.txt'), 'utf8'), '5\n');
});

test('a legacy deployment without a baseline cannot silently publish current main during preview', t => {
  const f = fixture(t, {pointer: false});
  assert.notEqual(f.result.status, 0);
  assert.match(f.result.stderr, /older deployment/);
  assert.equal(existsSync(path.join(f.directory, 'temporary/docs-stable/site/index.html')), false);
});

test('expired stable artifacts and failed baseline runs block preview before site composition', t => {
  for (const options of [{expired: true}, {conclusion: 'failure'}]) {
    const f = fixture(t, options);
    assert.notEqual(f.result.status, 0);
    assert.equal(existsSync(path.join(f.directory, 'temporary/docs-stable/site/index.html')), false);
    assert.equal(readFileSync(path.join(f.directory, 'docs/site/build/index.html'), 'utf8'), 'Unpublished main content');
  }
});
