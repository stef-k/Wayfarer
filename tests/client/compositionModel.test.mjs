import assert from 'node:assert/strict';
import test from 'node:test';
import { newChoice, buildAction, newReview, fieldValue, ordered, spatialContent } from '../../ClientApps/trip-editor/src/composition/compositionModel.ts';

const snapshot = () => ({ graph: { id: 'trip', name: 'C', regions: [
  { id: 'region', name: 'Taipei', displayOrder: 2, places: [{ id: 'cafe', name: 'Café', displayOrder: null, location: [121, 25] }], areas: [] }
], segments: [], credits: [], isPublic: false }, regionOrder: ['region'], placeOrder: { region: ['cafe'] }, areaOrder: { region: [] } });
const workspace = () => { const current = snapshot(); const b = snapshot(); b.graph.id = 'b'; b.graph.regions[0].places = [
  { id: 'beach', name: 'Beach', location: [122, 25] }, { id: 'castle', name: 'Castle', location: [122.1, 25] }];
  b.placeOrder.region = ['beach', 'castle']; return { compositionId: 'workspace', tripId: 'trip', sourceA: snapshot(), sourceB: b, current, mappings: [] }; };

test('two copied siblings carry a reviewed contiguous block, exact nullable order and no unrelated Trip fields', () => {
  const w = workspace(); const review = newReview(w, 'b');
  review.items = ['beach', 'castle'].map(id => { const c = newChoice(w, 'b', 'place', id); c.regionId = 'region'; c.position = 'after'; c.afterId = 'cafe'; return c; });
  const action = buildAction(w, review);
  assert.deepEqual(action.items.map(x => x.placement), [0, 1].map(() => ({ expected: [{ id: 'cafe', displayOrder: null }], position: 'after', afterId: 'cafe' })));
  assert.deepEqual(action.items.map(x => x.sourceId), ['beach', 'castle']);
  assert.equal(action.items[0].fields, undefined);
  assert.equal(action.items[0].anchors, undefined);
});

test('matched groups default Keep; selected originals are explicit nulls and independent of later current C', () => {
  const w = workspace(); const review = newReview(w, 'b'); const c = newChoice(w, 'b', 'place', 'beach');
  c.decision = 'reuse'; c.destinationId = 'cafe'; c.fields.notes = { operation: 'edit', value: '<p>New</p>' }; review.items = [c];
  w.current.graph.regions[0].places[0].notes = '<p>Other tab</p>';
  const item = buildAction(w, review).items[0];
  assert.deepEqual(item.fields, { notes: { operation: 'edit', value: '<p>New</p>', expected: null } });
  assert.equal(item.expectedParentId, 'region'); assert.equal(item.placement, undefined);
});

test('new B copies do not copy planning; foreign planning controls do not exist even as absence labels', () => {
  const w = workspace(); const choice = newChoice(w, 'b', 'place', 'beach');
  assert.equal('planning' in choice.fields, false);
  choice.fields.planning = { operation: 'useSource', value: null };
  const review = newReview(w, 'b'); review.items = [choice];
  assert.throws(() => buildAction(w, review), /unavailable/i);
});

test('Region hierarchy and Trip-level Segment placement are distinct; Areas never use canonical Unassigned', () => {
  const w = workspace(); w.current.graph.regions.push({ id: 'canonical', name: 'Unassigned Places', places: [], areas: [] });
  w.current.regionOrder.push('canonical'); w.sourceB.graph.regions[0].areas = [{ id: 'park', name: 'Park', geometryGeoJson: '{"type":"Polygon","coordinates":[]}' }];
  w.sourceB.areaOrder.region = ['park'];
  const review = newReview(w, 'b'); const c = newChoice(w, 'b', 'area', 'park'); c.regionId = 'canonical'; review.items = [c];
  assert.throws(() => buildAction(w, review), /normal Region/);
  c.regionId = 'region'; assert.equal(buildAction(w, review).items[0].regionId, 'region');
  assert.deepEqual(ordered(w.current, 'region').map(x => x.id), ['region', 'canonical']);
});

test('map extent includes complete polygon holes, route geometry/anchors, Region centre/children and no invented geography', () => {
  const s = snapshot(); s.graph.regions[0].center = [120, 24]; s.graph.regions[0].areas = [{ id: 'polygon', geometryGeoJson: JSON.stringify({ type: 'Polygon', coordinates: [
    [[120, 24], [122, 24], [122, 26], [120, 24]], [[121, 25], [121.1, 25], [121, 25.1], [121, 25]]
  ] }) }];
  s.areaOrder.region = ['polygon'];
  assert.equal(spatialContent(s, 'area', 'polygon').length, 8);
  assert.equal(spatialContent(s, 'region', 'region').length, 10);
  assert.deepEqual(spatialContent(s, 'place', 'missing'), []);
  assert.equal(fieldValue(s, 'place', 'cafe', 'notes'), null);
});
