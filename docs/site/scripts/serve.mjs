import {spawn} from 'node:child_process';
import {existsSync, mkdirSync, mkdtempSync, rmSync, symlinkSync} from 'node:fs';
import {createRequire} from 'node:module';
import os from 'node:os';
import path from 'node:path';
import {parseArgs} from 'node:util';
import {fileURLToPath} from 'node:url';

const site = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const build = path.join(site, 'build');
if (!existsSync(path.join(build, 'index.html'))) throw new Error('Build the documentation with npm run build first.');
const {values} = parseArgs({options: {
  port: {type: 'string', default: '3001'},
  host: {type: 'string', default: '0.0.0.0'},
}});
if (!/^\d+$/.test(values.port) || Number(values.port) < 1 || Number(values.port) > 65535) {
  throw new Error('Preview port must be an integer between 1 and 65535.');
}
const {baseUrl} = createRequire(import.meta.url)('../docusaurus.config.js');
if (!/^\/(?:[A-Za-z0-9_-]+\/)*$/.test(baseUrl)) throw new Error('Unsupported preview base path.');

// Docusaurus's serve-handler mistakes dotted version directories for files. Python's static
// server resolves their index.html correctly. Its entire document root contains only build output.
const temporaryRoot = mkdtempSync(path.join(os.tmpdir(), 'gamehub-docs-preview-'));
let server;
try {
  let publicRoot = build;
  if (baseUrl !== '/') {
    publicRoot = temporaryRoot;
    const mount = path.join(publicRoot, baseUrl.slice(1, -1));
    mkdirSync(path.dirname(mount), {recursive: true});
    symlinkSync(build, mount, 'dir');
  }
  server = spawn('python3', ['-m', 'http.server', values.port, '--bind', values.host,
    '--directory', publicRoot], {stdio: 'inherit'});
  const forwardInterrupt = () => server.kill('SIGINT');
  const forwardTermination = () => server.kill('SIGTERM');
  process.on('SIGINT', forwardInterrupt);
  process.on('SIGTERM', forwardTermination);
  try {
    console.log(`Documentation preview: http://localhost:${values.port}${baseUrl}`);
    const code = await new Promise((resolve, reject) => {
      server.once('error', reject);
      server.once('exit', (exitCode, signal) => resolve(signal ? 0 : exitCode ?? 1));
    });
    process.exitCode = code;
  } finally {
    process.off('SIGINT', forwardInterrupt);
    process.off('SIGTERM', forwardTermination);
  }
} finally {
  rmSync(temporaryRoot, {recursive: true, force: true});
}
