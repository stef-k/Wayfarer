#!/usr/bin/env node
/** Explicit maintenance only: normal producers must finalize their own exact artifacts. */
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { assertOrdinaryPath, inspectTree, removeOwnedDirectory } from './test-artifact-paths.mjs';

export const staleMilliseconds = 24 * 60 * 60 * 1000;
const guid = '[0-9a-f]{32}';
const retainedRun = new RegExp(`^407-waypoint-${guid}$`);
// These names correspond to concrete test/launcher producers, never general runtime caches.
const tempRun = new RegExp(`^(?:wayfarer-(?:import-tests|browser-tests|version-tests|attribution-render|attribution-snapshot|attribution-pdf|itinerary-render|waypoint-render|foreign-waypoint-render|waypoint-pdf-render|rich-notes-render|foreign-waypoint-pdf|coverage-safety|cleanup-safety)-${guid}|wayfarer_imgcache_test_${guid}|wayfarer-shared-layout-e2e-[1-9][0-9]*)$`);
const ephemeral = [
  'tests/Wayfarer.Tests/TestResults', 'playwright-report', '.local/playwright/test-output',
  '.local/playwright/shared-layout-output', '.local/playwright/shared-layout-report',
  '.local/publish-smoke', '.local/asset-smoke', '.local/asset-smoke-cache'
];

/** Lists only ordinary direct children of a known parent; a linked ancestor fails closed. */
function matchingChildren(root, relative, pattern) {
  const parent = relative ? assertOrdinaryPath(root, path.join(root, relative)) : root;
  if (!fs.existsSync(parent)) return [];
  return fs.readdirSync(parent).filter(name => pattern.test(name)).map(name => path.join(parent, name));
}

/** Injectable roots are for synthetic tests only; the CLI has no path/age overrides. */
export function cleanup({ repository, temporary = os.tmpdir(), dryRun = false, now = Date.now(), report = console.log }) {
  const candidates = ephemeral.map(name => ({ root: repository, target: path.join(repository, name), kind: 'ephemeral', stale: false }));
  const add = (root, relative, pattern, kind) => {
    try {
      candidates.push(...matchingChildren(root, relative, pattern).map(target => ({ root, target, kind, stale: true })));
    } catch (error) { report(`SKIP ${kind}: ${error.message}`); }
  };
  add(repository, '.local', retainedRun, 'retained #407 evidence');
  add(temporary, '', tempRun, 'OS-temp');
  add(temporary, 'wayfarer-tile-tests', new RegExp(`^${guid}$`), 'OS-temp tiles');
  add(temporary, 'wayfarer-trip-editor-place-tests', new RegExp(`^${guid}$`), 'OS-temp places');
  for (const { root, target, kind, stale } of candidates) {
    try {
      assertOrdinaryPath(root, target);
      if (!fs.existsSync(target)) continue;
      const { bytes, newest } = inspectTree(target);
      const recent = stale && now - newest < staleMilliseconds;
      report(`${recent ? 'KEEP recent' : dryRun ? 'WOULD REMOVE' : 'REMOVE'} [${kind}] ${target} (~${bytes} bytes)`);
      if (!recent && !dryRun) removeOwnedDirectory(root, target);
    } catch (error) { report(`SKIP ${target}: ${error.message}`); }
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  if (process.argv.slice(2).some(arg => arg !== '--dry-run')) {
    console.error('Usage: npm run test:cleanup -- [--dry-run] (no deletion paths accepted)');
    process.exitCode = 1;
  } else {
    cleanup({ repository: path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..'), dryRun: process.argv.includes('--dry-run') });
  }
}
