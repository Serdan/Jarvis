import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { execFileSync, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import crypto from 'node:crypto';

const sourceRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const realDotnet = execFileSync('which', ['dotnet'], { encoding: 'utf8' }).trim();
const server = 'https://jarvis2.kehlet.dev/client';

function fixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'jarvis-packaging-neg-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  fs.mkdirSync(path.join(root, 'scripts'));
  fs.mkdirSync(path.join(root, 'bin'));
  fs.writeFileSync(path.join(root, 'Jarvis.slnx'), '<Solution />\n');
  for (const name of ['build.cs', 'publish-client-downloads.cs'])
    fs.copyFileSync(path.join(sourceRoot, 'scripts', name), path.join(root, 'scripts', name));

  const fake = String.raw`#!/usr/bin/env node
const fs = require('node:fs');
const path = require('node:path');
const args = process.argv.slice(2);
fs.appendFileSync(process.env.JARVIS_FAKE_LOG, JSON.stringify(args) + '\n');
const flag = name => args[args.indexOf(name) + 1];
if (args[0] === 'scripts/build.cs' && args[1] === 'publish-client') {
  const rid = flag('--rid');
  if (process.env.JARVIS_FAKE_MODE === 'fail-osx' && rid === 'osx-arm64')
    process.exit(47);
  if (process.env.JARVIS_FAKE_MODE === 'missing-osx' && rid === 'osx-arm64')
    process.exit(0);
  const output = flag('--output');
  fs.mkdirSync(output, { recursive: true });
  fs.writeFileSync(path.join(output, 'JarvisClient'), 'FAKE-CLIENT-' + rid);
  process.exit(0);
}
process.exit(0);
`;
  const fakePath = path.join(root, 'bin', 'dotnet');
  fs.writeFileSync(fakePath, fake, { mode: 0o755 });
  const log = path.join(root, 'invocations.jsonl');
  const env = {
    ...process.env,
    PATH: path.join(root, 'bin') + path.delimiter + process.env.PATH,
    JARVIS_FAKE_LOG: log,
    DOTNET_CLI_TELEMETRY_OPTOUT: '1',
  };
  const run = (script, args = [], mode = 'ok') => {
    const result = spawnSync(realDotnet, [path.join(root, 'scripts', script), ...args], {
      cwd: root, env: { ...env, JARVIS_FAKE_MODE: mode }, encoding: 'utf8',
      timeout: 180000,
    });
    if (result.error) throw result.error;
    return result;
  };
  const calls = () => fs.existsSync(log)
    ? fs.readFileSync(log, 'utf8').trim().split('\n').filter(Boolean).map(JSON.parse)
    : [];
  return { root, run, calls };
}

const names = ['JarvisClient-linux-x64', 'JarvisClient-osx-arm64', 'SHA256SUMS'];
const downloads = root => path.join(root, 'artifacts', 'downloads');
const entries = dir => fs.readdirSync(dir).sort();
const snapshot = dir => Object.fromEntries(entries(dir).map(n => [n, fs.readFileSync(path.join(dir, n)).toString('base64')]));
const noStaging = root => {
  const artifacts = path.join(root, 'artifacts');
  assert.deepEqual(fs.readdirSync(artifacts).filter(name => name.startsWith('.downloads-')), []);
};
function seedOld(root) {
  const dir = downloads(root);
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(path.join(dir, 'JarvisClient-linux-x64'), 'previous-linux');
  fs.writeFileSync(path.join(dir, 'JarvisClient-win-x64.exe'), 'retired-windows');
  fs.writeFileSync(path.join(dir, 'SHA256SUMS'), 'old-hashes');
  return snapshot(dir);
}

test('publishes only Linux/macOS and exactly matching checksums, removing stale Windows downloads', t => {
  const x = fixture(t);
  seedOld(x.root);
  const result = x.run('publish-client-downloads.cs');
  assert.equal(result.status, 0, result.stderr + result.stdout);
  const dir = downloads(x.root);
  assert.deepEqual(entries(dir), [...names].sort());
  const hashes = fs.readFileSync(path.join(dir, 'SHA256SUMS'), 'utf8').trim().split('\n');
  assert.equal(hashes.length, 2);
  for (const line of hashes) {
    const match = /^([a-f0-9]{64})  (JarvisClient-(?:linux-x64|osx-arm64))$/.exec(line);
    assert.ok(match, 'Unexpected checksum entry: ' + line);
    assert.equal(match[1], crypto.createHash('sha256').update(fs.readFileSync(path.join(dir, match[2]))).digest('hex'));
  }
  const calls = x.calls();
  assert.deepEqual(calls.map(args => args[args.indexOf('--rid') + 1]), ['linux-x64', 'osx-arm64']);
  assert.ok(calls.every(args => args.includes('Release')));
  noStaging(x.root);
});

for (const mode of ['fail-osx', 'missing-osx']) {
  test('failed ' + mode + ' publish preserves old downloads without partial release', t => {
    const x = fixture(t);
    const prior = seedOld(x.root);
    const result = x.run('publish-client-downloads.cs', [], mode);
    assert.notEqual(result.status, 0, result.stdout + result.stderr);
    assert.deepEqual(snapshot(downloads(x.root)), prior);
    noStaging(x.root);
  });
}

test('unknown existing user file fails closed, preserving user data and making no child calls', t => {
  const x = fixture(t);
  const previous = seedOld(x.root);
  fs.writeFileSync(path.join(downloads(x.root), 'personal-notes.txt'), 'keep this');
  const result = x.run('publish-client-downloads.cs');
  assert.notEqual(result.status, 0);
  assert.deepEqual(snapshot(downloads(x.root)), { ...previous, 'personal-notes.txt': Buffer.from('keep this').toString('base64') });
  assert.deepEqual(x.calls(), []);
  noStaging(x.root);
});

test('symlinked public download directory is rejected without touching target', t => {
  const x = fixture(t);
  const target = path.join(x.root, 'protected');
  fs.mkdirSync(target);
  fs.writeFileSync(path.join(target, 'sentinel'), 'unchanged');
  fs.mkdirSync(path.join(x.root, 'artifacts'));
  fs.symlinkSync(target, downloads(x.root), 'dir');
  const result = x.run('publish-client-downloads.cs');
  assert.notEqual(result.status, 0);
  assert.equal(fs.readFileSync(path.join(target, 'sentinel'), 'utf8'), 'unchanged');
  assert.deepEqual(x.calls(), []);
  noStaging(x.root);
});

test('build rejects native Windows and unsupported client RID before spawning dotnet', t => {
  const x = fixture(t);
  for (const rid of ['win-x64', 'win-arm64', 'linux-arm64', 'osx-x64', '../linux-x64']) {
    const result = x.run('build.cs', ['publish-client', '--rid', rid, '--server', server]);
    assert.notEqual(result.status, 0, rid + ': ' + result.stdout + result.stderr);
  }
  assert.deepEqual(x.calls(), []);
});

test('mixed client RID batch rejects before any publish; generic server win-x64 remains permitted', t => {
  const x = fixture(t);
  const mixed = x.run('build.cs', ['publish-clients', '--rid', 'linux-x64', '--rid', 'win-x64', '--server', server]);
  assert.notEqual(mixed.status, 0, mixed.stdout + mixed.stderr);
  assert.deepEqual(x.calls(), []);
  const serverPublish = x.run('build.cs', ['publish-server', '--rid', 'win-x64']);
  assert.equal(serverPublish.status, 0, serverPublish.stdout + serverPublish.stderr);
  assert.ok(x.calls().some(args => args[0] === 'publish' && args.includes('Server/Server.fsproj') && args.includes('win-x64')));
});


test('negative mutant: disabling the client RID guard is detected by the unsupported-RID gate', t => {
  const x = fixture(t);
  const script = path.join(x.root, 'scripts', 'build.cs');
  const original = fs.readFileSync(script, 'utf8');
  const guard = 'if (!supportedClientRids.Contains(rid))';
  assert.ok(original.includes(guard));
  fs.writeFileSync(script, original.replace(guard, 'if (false)'));
  const result = x.run('build.cs', ['publish-client', '--rid', 'win-x64', '--server', server]);
  assert.equal(result.status, 0, result.stdout + result.stderr);
  assert.equal(x.calls().length, 1, 'The deliberate bad mutant must reach the forbidden Windows publish');
});

test('negative mutant: removing download preflight visibly destroys a disposable user sentinel', t => {
  const x = fixture(t);
  const script = path.join(x.root, 'scripts', 'publish-client-downloads.cs');
  const original = fs.readFileSync(script, 'utf8');
  const guard = '    ValidateExistingDownloads(downloads);';
  assert.ok(original.includes(guard));
  fs.writeFileSync(script, original.replace(guard, '    // MUTANT: validation deliberately bypassed'));
  seedOld(x.root);
  fs.writeFileSync(path.join(downloads(x.root), 'personal-notes.txt'), 'disposable sentinel');
  const result = x.run('publish-client-downloads.cs');
  assert.equal(result.status, 0, result.stdout + result.stderr);
  assert.ok(!fs.existsSync(path.join(downloads(x.root), 'personal-notes.txt')),
    'The mutant must violate the user-data preservation property');
});


test('stale prior artifacts/client output cannot satisfy a missing new macOS release', t => {
  const x = fixture(t);
  const prior = seedOld(x.root);
  const obsoleteOutput = path.join(x.root, 'artifacts', 'client', 'osx-arm64');
  fs.mkdirSync(obsoleteOutput, { recursive: true });
  fs.writeFileSync(path.join(obsoleteOutput, 'JarvisClient'), 'stale-previous-build');
  const result = x.run('publish-client-downloads.cs', [], 'missing-osx');
  assert.notEqual(result.status, 0, result.stdout + result.stderr);
  assert.deepEqual(snapshot(downloads(x.root)), prior);
  assert.equal(fs.readFileSync(path.join(obsoleteOutput, 'JarvisClient'), 'utf8'), 'stale-previous-build');
  noStaging(x.root);
});
