import assert from 'node:assert/strict';
import test from 'node:test';
import {
  alphabeticAnchorLabel,
  resolveSegmentAnchors
} from '../../ClientApps/trip-editor/src/segments/segmentPresentationResolver.ts';

const anchor = (position, placeId, name, role, location = [position + 10, position + 20]) => ({
  position,
  placeId,
  name,
  role,
  location
});

/** Proves the issue's locale-independent bijective alphabetic grammar. */
test('derives alphabetic anchor labels without persisting them', () => {
  assert.deepEqual(
    [0, 25, 26, 51, 52, 701, 702].map(alphabeticAnchorLabel),
    ['A', 'Z', 'AA', 'AZ', 'BA', 'ZZ', 'AAA']
  );
  for (const invalid of [-1, 1.5, Number.NaN, null, undefined]) {
    assert.throws(() => alphabeticAnchorLabel(invalid), /position/i);
  }
});

/** Proves each resolution derives a fresh sequence from current semantic order. */
test('recalculates complete labels after anchor changes and independently per Segment', () => {
  const first = resolveSegmentAnchors([
    anchor(0, 'athens', 'Athens', 'start'),
    anchor(1, 'delphi', 'Delphi', 'via'),
    anchor(2, 'patras', 'Patras', 'end')
  ]);
  const reordered = resolveSegmentAnchors([
    anchor(0, 'patras', 'Patras', 'start'),
    anchor(1, 'athens', 'Athens', 'end')
  ]);
  const anotherSegment = resolveSegmentAnchors([
    anchor(0, 'corinth', 'Corinth', 'start'),
    anchor(1, 'nafplio', 'Nafplio', 'end')
  ]);

  assert.deepEqual(first.anchors.map(item => [item.label, item.placeId]), [
    ['A', 'athens'], ['B', 'delphi'], ['C', 'patras']
  ]);
  assert.deepEqual(reordered.anchors.map(item => [item.label, item.placeId]), [
    ['A', 'patras'], ['B', 'athens']
  ]);
  assert.deepEqual(anotherSegment.anchors.map(item => item.label), ['A', 'B']);
  assert.equal(first.anchors[0].label, 'A');
});

/** Proves a loop retains semantic roles while producing one canonical marker badge. */
test('combines closed-loop badge labels without duplicating the canonical Place', () => {
  const result = resolveSegmentAnchors([
    anchor(0, 'athens', 'Athens', 'start'),
    anchor(1, 'delphi', 'Delphi', 'via'),
    anchor(2, 'athens', 'Athens', 'end')
  ]);

  assert.deepEqual(result.anchors.map(item => [item.label, item.roleText]), [
    ['A', 'Start'], ['B', 'Via 1'], ['C', 'End']
  ]);
  assert.deepEqual(result.badges.map(item => [item.placeId, item.label]), [
    ['athens', 'A/C'], ['delphi', 'B']
  ]);
});
