/** Synthetic ownership tests never invoke maintenance against the maintainer's checkout/temp. */
import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { cleanup, staleMilliseconds } from '../../tools/test-cleanup.mjs';
import { removeOwnedDirectory } from '../../tools/test-artifact-paths.mjs';

const id = '0123456789abcdef0123456789abcdef';
/** Each scenario owns and finalizes its entire synthetic installation. */
function fixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'wayfarer-cleanup-safety-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const repository = path.join(root, 'repo');
  const temporary = path.join(root, 'temp');
  fs.mkdirSync(repository); fs.mkdirSync(temporary);
  const now = Date.now();
  const make = (relative, base = repository, stale = false) => {
    const directory = path.join(base, relative);
    fs.mkdirSync(directory, { recursive: true });
    fs.writeFileSync(path.join(directory, 'sentinel'), 'keep');
    if (stale) {
      const date = new Date(now - staleMilliseconds - 1000);
      fs.utimesSync(path.join(directory, 'sentinel'), date, date);
      fs.utimesSync(directory, date, date);
    }
    return directory;
  };
  const messages = [];
  return { repository, temporary, now, make, messages, report: line => messages.push(line) };
}

test('removes allowlisted outputs and stale owned roots; preserves protected state and recent evidence', t => {
  const f = fixture(t);
  const removable = [f.make('.local/asset-smoke'), f.make('tests/Wayfarer.Tests/TestResults'),
    f.make(`.local/407-waypoint-${id}`, f.repository, true),
    f.make(`wayfarer-import-tests-${id}`, f.temporary, true),
    f.make(`wayfarer-tile-tests/${id}`, f.temporary, true)];
  const preserved = ['.local/test-results', '.local/playwright/dotnet-browsers', '.local/playwright/js-browsers',
    'ChromeCache', 'node_modules', 'bin', 'obj', 'Uploads', 'TileCache', '.local/unrelated',
    '.local/407-waypoint-invalid'].map(name => f.make(name));
  preserved.push(f.make(`wayfarer-import-tests-${'a'.repeat(32)}`, f.temporary));
  preserved.push(f.make(`.local/407-waypoint-${'b'.repeat(32)}`));
  preserved.push(f.make('postgres', f.temporary, true));
  fs.writeFileSync(path.join(f.repository, '.local/manual-verification.md'), 'protected');
  cleanup({ ...f, dryRun: true });
  for (const name of removable) assert.ok(fs.existsSync(name));
  assert.ok(f.messages.some(line => line.includes('retained #407 evidence') && line.includes('bytes')));
  cleanup(f);
  for (const name of removable) assert.ok(!fs.existsSync(name), name);
  for (const name of preserved) assert.ok(fs.existsSync(path.join(name, 'sentinel')), name);
  assert.equal(fs.readFileSync(path.join(f.repository, '.local/manual-verification.md'), 'utf8'), 'protected');
});

test('linked candidates, ancestors and nested junctions are skipped without following them', t => {
  const f = fixture(t);
  const outside = f.make('outside', f.temporary);
  fs.mkdirSync(path.join(f.repository, '.local'));
  fs.symlinkSync(outside, path.join(f.repository, '.local/asset-smoke'), 'junction');
  const cache = f.make('.local/asset-smoke-cache');
  fs.symlinkSync(outside, path.join(cache, 'nested'), 'junction');
  fs.symlinkSync(outside, path.join(f.repository, 'tests'), 'junction');
  cleanup(f);
  assert.equal(fs.readFileSync(path.join(outside, 'sentinel'), 'utf8'), 'keep');
  assert.ok(fs.lstatSync(path.join(f.repository, '.local/asset-smoke')).isSymbolicLink());
  assert.ok(fs.existsSync(path.join(cache, 'sentinel')));
  assert.throws(() => removeOwnedDirectory(f.repository, outside), /Not a child/);
});

test('old directory with recent contents is preserved; arbitrary CLI targets are rejected', t => {
  const f = fixture(t);
  const active = f.make(`wayfarer-import-tests-${id}`, f.temporary);
  const old = new Date(f.now - staleMilliseconds * 2);
  fs.utimesSync(active, old, old);
  cleanup(f);
  assert.ok(fs.existsSync(active));
  const result = spawnSync(process.execPath, ['tools/test-cleanup.mjs', active], { encoding: 'utf8' });
  assert.equal(result.status, 1);
  assert.match(result.stderr, /no deletion paths/);
});

test('asset smoke wires setup and finalization to its exact three guarded outputs', () => {
  const source = fs.readFileSync(new URL('../../tools/trip-editor-asset-smoke.mjs', import.meta.url), 'utf8');
  assert.match(source, /safeRemoveDirectory\(logsDir\);\s+safeRemoveDirectory\(cacheDir\);\s+fs.mkdirSync\(logsDir/);
  assert.match(source, /await stopStartedProcesses\(\);\s+if \(ownsSmokeState\)/);
  assert.match(source, /if \(ownsPublishState\) safeRemoveDirectory\(publishDir\)/);
  assert.match(source, /if \(succeeded\) safeRemoveDirectory\(logsDir\)/);
  assert.match(source, /if \(!\[publishDir, logsDir, cacheDir\].includes\(targetDir\)\) throw/);
  assert.match(source, /removeOwnedDirectory\(rootDir, targetDir\)/);
});
