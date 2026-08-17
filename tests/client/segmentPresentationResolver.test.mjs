import assert from 'node:assert/strict';
import test from 'node:test';
import {
  alphabeticAnchorLabel,
  classifySegmentOrientation,
  placeProjectedChevrons,
  reverseSegmentDraftRoute,
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

/** Proves classification is deterministic and never mutates legacy geometry. */
test('classifies forward, reversed, and ambiguous legacy routes from semantic endpoints', () => {
  const anchors = [
    anchor(0, 'athens', 'Athens', 'start', [23.7275, 37.9838]),
    anchor(1, 'delphi', 'Delphi', 'end', [22.501, 38.4824])
  ];
  const forward = [[23.7275, 37.9838], [23, 38.2], [22.501, 38.4824]];
  const reversed = forward.map(point => [...point]).reverse();
  const before = structuredClone(reversed);

  assert.equal(classifySegmentOrientation(anchors, forward, false), 'forward');
  assert.equal(classifySegmentOrientation(anchors, reversed, false), 'reversed');
  assert.deepEqual(reversed, before);
  assert.equal(classifySegmentOrientation(anchors, [[0, 0], [1, 1]], false), 'ambiguous');
});

/** Proves strict waypoint mapping rejects coordinate or semantic-order drift. */
test('classifies waypoint routes using strict anchor vertex mappings', () => {
  const anchors = [
    { ...anchor(0, 'a', 'A', 'start', [10, 20]), routeVertexIndex: 0 },
    { ...anchor(1, 'b', 'B', 'via', [11, 21]), routeVertexIndex: 2 },
    { ...anchor(2, 'c', 'C', 'end', [12, 22]), routeVertexIndex: 3 }
  ];
  const forward = [[10, 20], [10.5, 20.5], [11, 21], [12, 22]];
  const reversedAnchors = anchors.map(item => ({
    ...item,
    routeVertexIndex: item.role === 'start' ? 3 : item.role === 'end' ? 0 : 1
  }));

  assert.equal(classifySegmentOrientation(anchors, forward, true), 'forward');
  assert.equal(classifySegmentOrientation(reversedAnchors, [...forward].reverse(), true), 'reversed');
  assert.equal(classifySegmentOrientation(anchors, [[10, 20], [10.5, 20.5], [11.00001, 21], [12, 22]], true), 'ambiguous');
});

/** Proves projected placement follows the issue's exact clutter thresholds. */
test('places deterministic active and inactive chevrons from projected points', () => {
  assert.deepEqual(placeProjectedChevrons([[0, 0], [23, 0]], true), []);
  assert.deepEqual(placeProjectedChevrons([[0, 0], [30, 0]], false), []);
  assert.deepEqual(placeProjectedChevrons([[0, 0], [30, 0]], true), [{ x: 15, y: 0, angle: 0 }]);
  assert.deepEqual(placeProjectedChevrons([[0, 0], [120, 0]], false), [{ x: 60, y: 0, angle: 0 }]);
  assert.equal(placeProjectedChevrons([[0, 0], [1000, 0]], false).length, 4);
  assert.equal(placeProjectedChevrons([[0, 0], [1000, 0]], true).length, 8);
});

/** Proves Reverse route changes only the supplied draft and retains waypoint identity. */
test('reverses unsaved draft geometry and remaps waypoint indices atomically', () => {
  const draft = {
    route: { type: 'LineString', coordinates: [[12, 22], [11.5, 21.5], [11, 21], [10, 20]] },
    waypointRouteVertexIndices: [2],
    waypointRows: [{ clientId: 'via-b', placeId: 'b', routeVertexIndex: 2 }]
  };
  const before = structuredClone(draft);

  assert.equal(reverseSegmentDraftRoute(draft), true);
  assert.deepEqual(draft.route.coordinates, [[10, 20], [11, 21], [11.5, 21.5], [12, 22]]);
  assert.deepEqual(draft.waypointRouteVertexIndices, [1]);
  assert.deepEqual(draft.waypointRows, [{ clientId: 'via-b', placeId: 'b', routeVertexIndex: 1 }]);
  assert.equal(before.waypointRows[0].clientId, draft.waypointRows[0].clientId);
});
