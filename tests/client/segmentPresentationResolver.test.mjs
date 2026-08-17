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
  const anchors = [
    anchor(0, 'athens', 'Athens', 'start'),
    anchor(1, 'delphi', 'Delphi', 'via'),
    anchor(2, 'athens', 'Athens', 'end')
  ];
  const result = resolveSegmentAnchors(anchors);

  assert.deepEqual(result.anchors.map(item => [item.label, item.roleText]), [
    ['A', 'Start'], ['B', 'Via 1'], ['C', 'End']
  ]);
  assert.deepEqual(result.badges.map(item => [item.placeId, item.label]), [
    ['athens', 'A/C'], ['delphi', 'B']
  ]);

  const located = anchors.map((item, index) => ({
    ...item,
    location: index === 1 ? [11, 21] : [10, 20],
    routeVertexIndex: index
  }));
  assert.equal(classifySegmentOrientation(located, [[10, 20], [11, 21], [10, 20]], true), 'forward');
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

/** Proves editor badge placement avoids controls, prior badges, and unusable map space deterministically. */
test('places editor route badges with bounded collision avoidance', async () => {
  const module = await import('../../ClientApps/trip-editor/src/segments/segmentPresentationResolver.ts');
  const bounds = { left: 0, top: 0, right: 200, bottom: 160 };
  const badge = { width: 24, height: 24 };

  assert.deepEqual(module.placeRouteBadge([100, 80], badge, bounds, [], []),
    { left: 110, top: 62, width: 24, height: 24, offsetIndex: 0, fallback: false });
  assert.equal(module.placeRouteBadge([100, 80], badge, bounds,
    [{ left: 109, top: 61, right: 140, bottom: 87 }], []).offsetIndex, 1);
  assert.equal(module.placeRouteBadge([100, 80], badge, bounds, [],
    [{ left: 109, top: 61, right: 140, bottom: 87 }]).offsetIndex, 1);
  assert.notEqual(module.placeRouteBadge([190, 150], badge, bounds, [], []).offsetIndex, 0);
  assert.deepEqual(module.placeRouteBadge([100, 80], badge, bounds, [bounds], []),
    { left: 110, top: 62, width: 24, height: 24, offsetIndex: -1, fallback: true });

  const combined = module.placeCombinedRouteBadge([[190, 150], [100, 80]], { width: 50, height: 24 }, bounds, [bounds], []);
  assert.deepEqual(combined, { left: 146, top: 132, width: 50, height: 24, offsetIndex: -1, fallback: true });
  assert.ok(combined.left >= bounds.left && combined.top >= bounds.top);
  assert.ok(combined.left + combined.width <= bounds.right && combined.top + combined.height <= bounds.bottom);
  const viewer = await import(`../../wwwroot/js/Trip/segmentPresentation.js?editorParity=${Date.now()}`);
  assert.deepEqual(viewer.placeCombinedRouteBadge([[190, 150], [100, 80]], { width: 50, height: 24 }, bounds, [bounds], []), combined);
});

/** Proves the combined fallback searches bounded clear space after every anchor-relative offset is blocked. */
test('moves the combined fallback away from controls and an already placed clear badge', async () => {
  const editor = await import('../../ClientApps/trip-editor/src/segments/segmentPresentationResolver.ts');
  const viewer = await import(`../../wwwroot/js/Trip/segmentPresentation.js?fallbackSearch=${Date.now()}`);
  const bounds = { left: 0, top: 0, right: 200, bottom: 160 };
  const placed = [{ left: 109, top: 61, right: 161, bottom: 87 }];
  const controls = [
    { left: 65, top: 61, right: 117, bottom: 87 },
    { left: 109, top: 31, right: 161, bottom: 57 },
    { left: 65, top: 31, right: 117, bottom: 57 },
    { left: 117, top: 45, right: 169, bottom: 71 },
    { left: 57, top: 45, right: 109, bottom: 71 }
  ];
  const expected = { left: 4, top: 4, width: 50, height: 24, offsetIndex: -1, fallback: true };

  assert.deepEqual(editor.placeCombinedRouteBadge([[100, 80]], { width: 50, height: 24 }, bounds, controls, placed), expected);
  assert.deepEqual(viewer.placeCombinedRouteBadge([[100, 80]], { width: 50, height: 24 }, bounds, controls, placed), expected);
});

/** Proves combined labels fit losslessly and identically without splitting the atomic closed-loop token. */
test('fits over-wide combined labels into deterministic lossless lines', async () => {
  const editor = await import('../../ClientApps/trip-editor/src/segments/segmentPresentationResolver.ts');
  const viewer = await import(`../../wwwroot/js/Trip/segmentPresentation.js?fallbackFit=${Date.now()}`);
  const labels = ['A/C', 'B', 'AA', 'ZZ', 'AAA'];
  const expected = { labels, lines: ['A/C/B', 'AA/ZZ', 'AAA'], width: 60, height: 52 };

  assert.deepEqual(editor.fitCombinedRouteBadgeLabels(labels, 60), expected);
  assert.deepEqual(viewer.fitCombinedRouteBadgeLabels(labels, 60), expected);
  assert.deepEqual(expected.lines, ['A/C/B', 'AA/ZZ', 'AAA']);
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
