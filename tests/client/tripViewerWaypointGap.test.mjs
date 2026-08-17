import assert from 'node:assert/strict';
import test from 'node:test';

/** Supplies the minimal Leaflet surface required by the production helper module. */
const prepareLeaflet = (layer = () => ({})) => {
  globalThis.location = { search: '' };
  globalThis.window = { wayfarer: {}, wayfarerTileConfig: {} };
  globalThis.document = { createElement: () => ({
    width: 0, height: 0, toDataURL: () => 'data:image/png;base64,badge',
    getContext: () => ({ scale() {}, beginPath() {}, roundRect() {}, fill() {}, stroke() {}, fillText() {} })
  }) };
  const extensible = { extend: () => class { addTo() { return this; } } };
  const makeLayer = () => ({
    _layers: [],
    addTo(target) { target?._layers?.push(this); return this; },
    bindTooltip() { return this; }, unbindTooltip() { return this; }, on() { return this; }, off() { return this; },
    remove() { this.removed = true; return this; }, clearLayers() { this._layers = []; return this; },
    getLayers() { return this._layers; }, setStyle(style) { this.style = style; return this; }, getElement() { return null; }
  });
  globalThis.L = {
    Control: extensible, TileLayer: extensible, canvas: () => ({}), polyline: () => layer(),
    marker: () => layer(), layerGroup: () => makeLayer(), icon: options => options, divIcon: options => options
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
  const map = {
    _layers: [], removeLayer: layer => removed.push(layer),
    latLngToLayerPoint: ([latitude, longitude]) => ({ x: longitude, y: latitude }),
    layerPointToLatLng: ([x, y]) => [y, x]
  };
  const layer = () => ({
    addTo(target) { target?._layers?.push(this); return this; },
    bindTooltip() { return this; },
    unbindTooltip() { return this; },
    on() { return this; },
    off() { return this; },
    remove() { return this; },
    setStyle() { return this; }
  });
  prepareLeaflet(layer);
  const helpers = await import(`../../wwwroot/js/Trip/tripViewerHelpers.js?replace=${Date.now()}`);

  helpers.addSegment(map, 'segment-1', [[1, 1], [2, 2]]);
  const firstSegment = helpers.getSegmentPolyline('segment-1');
  helpers.addSegment(map, 'segment-1', [[1, 1], [3, 3]]);
  helpers.addPlaceMarker(map, 'place-1', [1, 1], { name: 'A' });
  const firstPlace = helpers.getPlaceMarker('place-1');
  helpers.addPlaceMarker(map, 'place-1', [2, 2], { name: 'A' });

  assert.deepEqual(removed, [firstPlace]);
  assert.notEqual(helpers.getSegmentPolyline('segment-1'), firstSegment);
  assert.notEqual(helpers.getPlaceMarker('place-1'), firstPlace);
  assert.equal(helpers.getSegmentPresentationSnapshot().segments.filter(item => item.id === 'segment-1').length, 1);
});

/** Proves independent A-sequences and active-only closed-loop badges transfer cleanly. */
test('multiple Segment presentations remain independent and transfer active badges', async () => {
  const map = {
    _layers: [], removeLayer() {},
    latLngToLayerPoint: ([latitude, longitude]) => ({ x: longitude * 20, y: latitude * 20 }),
    layerPointToLatLng: ([x, y]) => [y / 20, x / 20]
  };
  const layer = () => ({
    addTo(target) { target?._layers?.push(this); return this; }, bindTooltip() { return this; }, unbindTooltip() { return this; },
    on() { return this; }, off() { return this; }, remove() { return this; }, setStyle() { return this; }
  });
  prepareLeaflet(layer);
  const helpers = await import(`../../wwwroot/js/Trip/tripViewerHelpers.js?multi=${Date.now()}`);
  const anchors = (start, via, end) => [
    { position: 0, placeId: start, name: start, role: 'Start', longitude: 0, latitude: 0 },
    { position: 1, placeId: via, name: via, role: 'Via 1', longitude: 5, latitude: 0 },
    { position: 2, placeId: end, name: end, role: 'End', longitude: 10, latitude: 0 }
  ];
  helpers.addSegment(map, 'one', [[0, 0], [0, 10]], '', { anchors: anchors('a', 'b', 'a'), orientation: 'forward' });
  helpers.addSegment(map, 'two', [[1, 0], [1, 10]], '', { anchors: anchors('c', 'd', 'e'), orientation: 'forward' });

  helpers.setActiveSegment(map, 'one');
  let snapshot = helpers.getSegmentPresentationSnapshot();
  assert.deepEqual(snapshot.segments.map(item => item.anchorLabels), [['A', 'B', 'C'], ['A', 'B', 'C']]);
  assert.equal(snapshot.routeBadgeCount, 2);
  assert.equal(snapshot.segments.find(item => item.id === 'one').active, true);

  helpers.setActiveSegment(map, 'two');
  snapshot = helpers.getSegmentPresentationSnapshot();
  assert.equal(snapshot.routeBadgeCount, 3);
  assert.equal(snapshot.segments.find(item => item.id === 'one').active, false);
  assert.equal(snapshot.segments.find(item => item.id === 'two').active, true);
});
