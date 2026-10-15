import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {copyFileSync, mkdirSync, mkdtempSync, readdirSync, rmSync, writeFileSync} from 'node:fs';
import {createServer} from 'node:net';
import os from 'node:os';
import path from 'node:path';
import {test} from 'node:test';

test('production preview serves dotted version indexes and cleans up its isolated document root', async t => {
  const root = mkdtempSync(path.join(os.tmpdir(), 'gamehub-preview-contract-'));
  t.after(() => rmSync(root, {recursive: true, force: true}));
  for (const directory of ['scripts', 'build/0.1.0', 'build/next', 'tmp']) mkdirSync(path.join(root, directory), {recursive: true});
  copyFileSync(new URL('../scripts/serve.mjs', import.meta.url), path.join(root, 'scripts/serve.mjs'));
  writeFileSync(path.join(root, 'docusaurus.config.js'), 'module.exports = {baseUrl: "/gamehub/"};');
  writeFileSync(path.join(root, 'private-source.txt'), 'Source must not be served');
  for (const version of ['', '0.1.0/', 'next/']) writeFileSync(path.join(root, 'build', version, 'index.html'), '<h1>Handbook ' + version + '</h1>');
  const socket = createServer();
  await new Promise(resolve => socket.listen(0, '127.0.0.1', resolve));
  const port = socket.address().port;
  await new Promise(resolve => socket.close(resolve));
  const child = spawn(process.execPath, [path.join(root, 'scripts/serve.mjs'), '--host', '127.0.0.1', '--port', String(port)], {
    env: {...process.env, TMPDIR: path.join(root, 'tmp')}, stdio: ['ignore', 'pipe', 'pipe'],
  });
  let logs = '';
  child.stdout.on('data', chunk => { logs += chunk; });
  child.stderr.on('data', chunk => { logs += chunk; });
  const exited = new Promise(resolve => child.once('exit', resolve));
  try {
    let ready = false;
    for (let attempt = 0; attempt < 100 && child.exitCode === null; attempt++) {
      try { ready = (await fetch('http://127.0.0.1:' + port + '/gamehub/')).ok; } catch {}
      if (ready) break;
      await new Promise(resolve => setTimeout(resolve, 100));
    }
    assert.ok(ready, logs);
    for (const version of ['', '0.1.0/', 'next/']) {
      const response = await fetch('http://127.0.0.1:' + port + '/gamehub/' + version);
      assert.equal(response.status, 200, version);
      assert.equal(await response.text(), '<h1>Handbook ' + version + '</h1>');
    }
    for (const missing of ['/private-source.txt', '/gamehub/private-source.txt', '/gamehub/missing/']) {
      assert.equal((await fetch('http://127.0.0.1:' + port + missing)).status, 404);
    }
  } finally {
    child.kill('SIGTERM');
    await exited;
  }
  assert.deepEqual(readdirSync(path.join(root, 'tmp')), []);
});
