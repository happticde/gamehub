import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, statSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const installer = fileURLToPath(new URL('../helper-distribution/install.command', import.meta.url));

function fixture(t) {
    const root = mkdtempSync(join(tmpdir(), 'gamehub-installer-test-'));
    t.after(() => rmSync(root, { recursive: true, force: true }));
    const userDirectory = join(root, 'test user');
    const commands = join(root, 'commands');
    const bundle = join(root, 'GameHub Helper');
    mkdirSync(commands);
    mkdirSync(bundle);
    writeFileSync(join(commands, 'uname'), '#!/bin/bash\nprintf "%s\\n" "$INSTALLER_TEST_OS"\n', { mode: 0o755 });
    const source = join(bundle, 'gamehub-cli');
    const installed = join(userDirectory, 'Library/Application Support/GameHub/Helper/gamehub-cli');
    const approval = join(root, 'approved');
    const log = join(root, 'executions');
    const fakeCli = `#!/bin/bash
printf '%s\\n' "$0" "$*" >> "$INSTALLER_TEST_LOG"
if [[ ! -f "$INSTALLER_TEST_APPROVAL" ]]; then
    echo 'Simulated macOS execution block' >&2
    exit 1
fi
exit 0
`;
    writeFileSync(source, fakeCli, { mode: 0o755 });
    for (const name of ['LICENSE.txt', 'THIRD_PARTY_NOTICES.txt', 'SOURCE.txt']) {
        writeFileSync(join(bundle, name), `Original ${name}\n`);
    }
    const script = join(bundle, 'Install GameHub Helper.command');
    writeFileSync(script, readFileSync(installer));
    const run = (os = 'Darwin') => spawnSync('bash', [script], {
        encoding: 'utf8',
        env: {
            ...process.env,
            // Only the child installer uses this isolated simulated user's home.
            HOME: userDirectory,
            PATH: `${commands}:${process.env.PATH}`,
            INSTALLER_TEST_OS: os,
            INSTALLER_TEST_APPROVAL: approval,
            INSTALLER_TEST_LOG: log,
        },
    });
    return { root, source, installed, approval, log, fakeCli, run };
}

test('blocked execution keeps a stable file for approval and subsequent retries', (t) => {
    const f = fixture(t);
    const blocked = f.run();
    assert.equal(blocked.status, 1);
    assert.match(blocked.stdout, /Open Anyway for gamehub-cli/);
    assert.ok(blocked.stdout.includes(f.installed));
    const firstInode = statSync(f.installed).ino;
    assert.deepEqual(readdirSync(join(f.installed, '..')).sort(), ['LICENSE.txt', 'SOURCE.txt', 'THIRD_PARTY_NOTICES.txt', 'gamehub-cli']);
    for (const name of ['LICENSE.txt', 'THIRD_PARTY_NOTICES.txt', 'SOURCE.txt']) {
        assert.equal(readFileSync(join(f.installed, '..', name), 'utf8'), `Original ${name}\n`);
    }
    assert.equal(readFileSync(f.log, 'utf8'), `${f.installed}\n--help\n`);

    writeFileSync(f.approval, 'approved');
    assert.equal(f.run().status, 0);
    assert.equal(statSync(f.installed).ino, firstInode);
    assert.equal(f.run().status, 0);
    assert.equal(statSync(f.installed).ino, firstInode);
    const calls = readFileSync(f.log, 'utf8').trimEnd().split('\n');
    assert.deepEqual(calls.filter((_, index) => index % 2 === 0), Array(5).fill(f.installed));
    assert.equal(calls[5], `helper install-protocol-handler --cli-path ${f.installed}`);
    assert.equal(statSync(f.installed).mode & 0o777, 0o755);
});

test('an update is installed once and a failed update remains for approval', (t) => {
    const f = fixture(t);
    writeFileSync(f.approval, 'approved');
    assert.equal(f.run().status, 0);
    const previousInode = statSync(f.installed).ino;
    rmSync(f.approval);
    const updatedCli = `${f.fakeCli}# New helper version\n`;
    writeFileSync(f.source, updatedCli);
    assert.equal(f.run().status, 1);
    assert.equal(readFileSync(f.installed, 'utf8'), updatedCli);
    const updatedInode = statSync(f.installed).ino;
    assert.notEqual(updatedInode, previousInode);
    writeFileSync(f.approval, 'approved');
    assert.equal(f.run().status, 0);
    assert.equal(statSync(f.installed).ino, updatedInode);
});

test('unsupported operating systems do not install or execute the CLI', (t) => {
    const f = fixture(t);
    const result = f.run('Linux');
    assert.equal(result.status, 1);
    assert.match(result.stdout, /supports installation on macOS only/);
    assert.deepEqual(readdirSync(f.root).sort(), ['GameHub Helper', 'commands']);
});

test('installer has valid bash syntax', () => {
    execFileSync('bash', ['-n', installer]);
});
