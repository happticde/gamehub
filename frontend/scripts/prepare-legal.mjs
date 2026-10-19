// SPDX-FileCopyrightText: 2026 jfk1987 and GameHub contributors
// SPDX-License-Identifier: AGPL-3.0-only
import {mkdirSync, readFileSync, writeFileSync} from 'node:fs';
import path from 'node:path';
import {fileURLToPath, pathToFileURL} from 'node:url';

const frontend = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const escapeHtml = value => value.replace(/[&<>"']/g, character => ({
  '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
})[character]);

export function prepareLegal({legalDirectory, output, repository = 'jfk1987/gamehub', revision = 'unknown', version = 'dev'}) {
  if (!/^[A-Za-z0-9_-]+\/[A-Za-z0-9_.-]+$/.test(repository) || repository.endsWith('/..')) {
    throw new Error('Invalid source repository');
  }
  if (revision !== 'unknown' && !/^[0-9a-f]{40}$/.test(revision)) throw new Error('Invalid source revision');
  if (!/^(?:dev|[0-9]+\.[0-9]+\.[0-9]+)$/.test(version)) throw new Error('Invalid build version');
  if (version !== 'dev' && version !== '0.0.0' && revision === 'unknown') {
    throw new Error('Versioned builds require their exact source revision');
  }
  const license = readFileSync(path.join(legalDirectory, 'LICENSE'), 'utf8');
  const notices = readFileSync(path.join(legalDirectory, 'THIRD_PARTY_NOTICES.txt'), 'utf8');
  const source = `https://github.com/${repository}`;
  const sourceUrl = `${source}/tree/${revision === 'unknown' ? 'main' : revision}`;
  const isRelease = version !== 'dev' && version !== '0.0.0';
  const archive = isRelease ? `${source}/releases/download/v${version}/gamehub-source-${version}.tar.gz` : '';
  mkdirSync(output, {recursive: true});
  writeFileSync(path.join(output, 'LICENSE.txt'), license);
  writeFileSync(path.join(output, 'THIRD_PARTY_NOTICES.txt'), notices);
  writeFileSync(path.join(output, 'build.json'), JSON.stringify({license: 'AGPL-3.0-only', repository, revision, version}, null, 2) + '\n');
  writeFileSync(path.join(output, 'index.html'), `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Source and license — GameHub</title>
<style>:root{color-scheme:light dark}body{font:1rem/1.65 system-ui,sans-serif;margin:0;padding:2rem;overflow-wrap:anywhere}main{max-width:48rem;margin:auto}a{color:LinkText;text-underline-offset:.2em}a:focus-visible{outline:2px solid currentColor;outline-offset:4px}li{margin:.6rem 0}code{font-size:.9em}@media(max-width:400px){body{padding:1rem}}</style></head>
<body><main><a href="/">Back to GameHub</a><h1>Source and license</h1>
<p>Copyright © 2026 jfk1987 and GameHub contributors.</p>
<p>GameHub is licensed under <strong>GNU Affero General Public License version 3 only</strong>.
You may redistribute and modify it under that license. It is provided without warranty,
to the extent permitted by applicable law.</p>
<ul><li><a href="LICENSE.txt">Read the AGPLv3 license</a></li>
<li><a href="${escapeHtml(sourceUrl)}">Browse GameHub source${revision === 'unknown' ? ' (upstream)' : ' for this build'}</a></li>
${archive ? `<li><a href="${escapeHtml(archive)}">Download release source (v${escapeHtml(version)})</a></li>` : ''}
<li><a href="THIRD_PARTY_NOTICES.txt">Third-party licenses and notices</a></li></ul>
<p>Version: <code>${escapeHtml(version)}</code><br>Source revision: <code>${escapeHtml(revision)}</code></p>
${revision === 'unknown' ? '<p>This development build has no pinned source revision. Its operator must provide the complete source for any local changes; the upstream link alone does not cover them.</p>' : ''}
<p>Third-party components retain their original licenses. Modified deployments must keep this
source offer accurate for the version they run.</p></main></body></html>
`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const legal = process.env.GAMEHUB_LICENSE_DIR || path.resolve(frontend, '../licensing');
  const generated = path.join(frontend, '.generated/legal');
  const {generateNotices} = await import(pathToFileURL(path.join(legal, 'generate.mjs')).href);
  generateNotices({input: frontend, output: generated, production: true, frontend: true});
  prepareLegal({legalDirectory: generated,
    output: path.join(frontend, 'public/legal'), repository: process.env.GAMEHUB_SOURCE_REPOSITORY || 'jfk1987/gamehub',
    revision: process.env.GAMEHUB_SOURCE_REVISION || 'unknown', version: process.env.GAMEHUB_BUILD_VERSION || 'dev'});
}
