import {execFileSync} from 'node:child_process';
import {createHash} from 'node:crypto';
import {lstatSync, mkdirSync, readFileSync, writeFileSync} from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';

const site = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const root = path.resolve(site, '../..');
const metadata = JSON.parse(readFileSync(path.join(site, '.generated/metadata.json'), 'utf8'));
const hash = createHash('sha256');
hash.update(JSON.stringify({content: metadata.contentHash,
  versions: metadata.snapshots.filter(snapshot => snapshot.version !== 'current'),
  repository: metadata.repository, url: process.env.DOCS_URL || '', baseUrl: process.env.DOCS_BASE_URL || '',
  previewCommit: process.env.DOCS_PREVIEW_SHA || ''}));
const inputs = execFileSync('git', ['ls-files', '-z', '--', 'docs/site', 'licensing', 'services/cli/tools/license-notices', 'LICENSE', 'COPYRIGHT', '.github/workflows/docs.yml'], {cwd: root, encoding: 'utf8'});
for (const name of inputs.split('\0').filter(Boolean).sort()) {
  if (name.split('/').some(part => part === '.env' || part.startsWith('.env.'))) continue;
  const input = path.join(root, name);
  if (!lstatSync(input).isFile()) throw new Error(`Non-regular documentation build input: ${name}`);
  hash.update(name);
  hash.update(readFileSync(input));
}
const fingerprint = hash.digest('hex');
const destination = path.join(site, '.generated/publish-state');
mkdirSync(destination, {recursive: true});
writeFileSync(path.join(destination, 'fingerprint.txt'), fingerprint + '\n');
console.log(fingerprint);
