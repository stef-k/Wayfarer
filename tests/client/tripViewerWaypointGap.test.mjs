import assert from 'node:assert/strict';
import test from 'node:test';

/** Supplies the minimal Leaflet surface required by the production helper module. */
const prepareLeaflet = (layer = () => ({})) => {
  globalThis.location = { search: '' };
  globalThis.window = { wayfarer: {}, wayfarerTileConfig: {} };
  const extensible = { extend: () => class { addTo() { return this; } } };
  globalThis.L = {
    Control: extensible, TileLayer: extensible, canvas: () => ({}), polyline: () => layer(),
    marker: () => layer(), icon: options => options, divIcon: options => options
  };
};

/** Proves resolver rejection cannot be replaced by an endpoint-only client route. */
test('Segment route coordinates require resolver-approved WKT', async () => {
  prepareLeaflet();
  const helpers = await import(`../../wwwroot/js/Trip/tripViewerHelpers.js?route=${Date.now()}`);

  assert.deepEqual(helpers.segmentRouteCoords(undefined), []);
  assert.deepEqual(helpers.segmentRouteCoords(''), []);
  assert.deepEqual(helpers.segmentRouteCoords('not route geometry'), []);
  assert.deepEqual(helpers.segmentRouteCoords('LINESTRING (1 1, 2 2)'), [[1, 1], [2, 2]]);

  const map = { removeLayer() {} };
  assert.equal(helpers.addSegmentFromRouteWkt(map, 'missing-route', undefined), null);
  assert.equal(helpers.getSegmentPolyline('missing-route'), null);
  assert.equal(helpers.addSegmentFromRouteWkt(map, 'isolated-missing-route', ''), null);
  assert.equal(helpers.getSegmentPolyline('isolated-missing-route'), null);
});

/** Exercises replace-only ownership without introducing browser infrastructure. */
test('Segment and Place layers replace existing canonical registry entries', async () => {
  const removed = [];
  const map = { removeLayer: layer => removed.push(layer) };
  const layer = () => ({
    addTo() { return this; },
    bindTooltip() { return this; },
    unbindTooltip() { return this; },
    on() { return this; },
    off() { return this; }
  });
  prepareLeaflet(layer);
  const helpers = await import(`../../wwwroot/js/Trip/tripViewerHelpers.js?replace=${Date.now()}`);

  helpers.addSegment(map, 'segment-1', [[1, 1], [2, 2]]);
  const firstSegment = helpers.getSegmentPolyline('segment-1');
  helpers.addSegment(map, 'segment-1', [[1, 1], [3, 3]]);
  helpers.addPlaceMarker(map, 'place-1', [1, 1], { name: 'A' });
  const firstPlace = helpers.getPlaceMarker('place-1');
  helpers.addPlaceMarker(map, 'place-1', [2, 2], { name: 'A' });

  assert.deepEqual(removed, [firstSegment, firstPlace]);
  assert.notEqual(helpers.getSegmentPolyline('segment-1'), firstSegment);
  assert.notEqual(helpers.getPlaceMarker('place-1'), firstPlace);
});
