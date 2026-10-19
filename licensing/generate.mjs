// SPDX-FileCopyrightText: 2026 jfk1987 and GameHub contributors
// SPDX-License-Identifier: AGPL-3.0-only
import {execFileSync} from 'node:child_process';
import path from 'node:path';
import {fileURLToPath} from 'node:url';

export function generateNotices({input, output, production = false, frontend = false}) {
  const legal = path.dirname(fileURLToPath(import.meta.url));
  const args = ['-kind', 'npm', '-input', input, '-output', output, '-legal', legal];
  if (production) args.push('-production');
  if (frontend) args.push('-extra', 'frontend');
  const binary = process.env.GAMEHUB_NOTICE_GENERATOR;
  execFileSync(binary || 'go', binary ? args : ['run',
    path.resolve(legal, '../services/cli/tools/license-notices/main.go'), ...args], {stdio: 'inherit'});
}
