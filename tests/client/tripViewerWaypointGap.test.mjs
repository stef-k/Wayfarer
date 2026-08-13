import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import test from 'node:test';

const viewerPath = path.resolve('wwwroot/js/Trip/Viewer.js');

/** Protects the isolated Segment snapshot from a second append path. */
test('viewer owns one Segment add path for normal and isolated rendering', () => {
  const source = fs.readFileSync(viewerPath, 'utf8');
  const addCalls = source.match(/addSegment\(map,/g) ?? [];

  assert.equal(addCalls.length, 1);
});
