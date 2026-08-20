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

/** Proves decoration refresh replaces owning-group membership, not only the public generation array. */
test('Viewer refresh and hide-show retain only the current owned chevron generation', async () => {
  const groups = [];
  const map = {
    _layers: [],
    latLngToLayerPoint: ([latitude, longitude]) => ({ x: longitude * 20, y: latitude * 20 }),
    layerPointToLatLng: ([x, y]) => [y / 20, x / 20]
  };
  const layer = () => ({
    addTo(owner) { owner.addLayer(this); return this; },
    bindTooltip() { return this; }, unbindTooltip() { return this; }, on() { return this; }, off() { return this; },
    remove() { this.mounted = false; return this; }, setStyle() { return this; }
  });
  prepareLeaflet(layer);
  globalThis.L.layerGroup = () => {
    const group = {
      layers: [], mounted: false,
      addLayer(child) { this.layers.push(child); child.mounted = this.mounted; return this; },
      removeLayer(child) { this.layers = this.layers.filter(item => item !== child); child.mounted = false; return this; },
      addTo() { this.mounted = true; this.layers.forEach(child => { child.mounted = true; }); return this; },
      remove() { this.mounted = false; this.layers.forEach(child => { child.mounted = false; }); return this; },
      clearLayers() { this.layers.forEach(child => { child.mounted = false; }); this.layers = []; return this; },
      getLayers() { return this.layers; }
    };
    groups.push(group);
    return group;
  };
  const helpers = await import(`../../wwwroot/js/Trip/tripViewerHelpers.js?ownership=${Date.now()}`);
  helpers.addSegment(map, 'owned', [[0, 0], [0, 10]], '', { anchors: [], orientation: 'forward' });
  const segmentGroup = groups[1];
  const expected = () => 1 + 1 + helpers.getSegmentPresentationSnapshot().segments[0].chevronCount;

  assert.equal(segmentGroup.getLayers().length, expected());
  const firstGeneration = segmentGroup.getLayers().slice(2);
  helpers.refreshSegmentPresentation(map);
  assert.equal(segmentGroup.getLayers().length, expected(), 'refresh retained stale group-owned chevrons');
  helpers.refreshSegmentPresentation(map);
  assert.equal(segmentGroup.getLayers().length, expected(), 'public chevron count was false-green after repeated refresh');

  helpers.setSegmentVisible(map, 'owned', false);
  helpers.setSegmentVisible(map, 'owned', true);
  assert.equal(segmentGroup.getLayers().length, expected());
  assert.equal(firstGeneration.some(child => child.mounted), false, 'hide-show resurrected a stale chevron generation');

  helpers.disposeSegmentPresentation();
  assert.equal(segmentGroup.getLayers().length, 0, 'disposal retained Segment-owned layers');
});

/** Proves independent A-sequences and active-only closed-loop badges transfer cleanly. */
test('multiple Segment presentations remain independent and transfer active badges', async () => {
  const map = {
    _layers: [], removeLayer() {},
    latLngToLayerPoint: ([latitude, longitude]) => ({ x: longitude * 20, y: latitude * 20 }),
    latLngToContainerPoint: ([latitude, longitude]) => ({ x: longitude * 20 + 200, y: latitude * 20 + 200 }),
    layerPointToLatLng: ([x, y]) => [y / 20, x / 20],
    getSize: () => ({ x: 800, y: 600 }),
    getContainer: () => ({ getBoundingClientRect: () => ({ left: 0, top: 0 }), querySelectorAll: () => [] })
  };
  const layer = () => ({
    addTo(target) { target?._layers?.push(this); return this; }, bindTooltip() { return this; }, unbindTooltip() { return this; },
    on() { return this; }, off() { return this; }, remove() { return this; }, setStyle() { return this; },
    getElement() { return { complete: true, naturalWidth: 24, decode: () => Promise.resolve() }; }
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

/** Proves viewer placement uses the same bounded fixture results as the editor. */
test('places viewer route badges with deterministic collision avoidance', async () => {
  const presentation = await import(`../../wwwroot/js/Trip/segmentPresentation.js?placement=${Date.now()}`);
  const bounds = { left: 0, top: 0, right: 200, bottom: 160 };
  const badge = { width: 24, height: 24 };
  assert.equal(presentation.placeRouteBadge([100, 80], badge, bounds, [], []).offsetIndex, 0);
  assert.equal(presentation.placeRouteBadge([100, 80], badge, bounds,
    [{ left: 109, top: 61, right: 140, bottom: 87 }], []).offsetIndex, 1);
  assert.equal(presentation.placeRouteBadge([100, 80], badge, bounds, [],
    [{ left: 109, top: 61, right: 140, bottom: 87 }]).offsetIndex, 1);
  assert.notEqual(presentation.placeRouteBadge([190, 150], badge, bounds, [], []).offsetIndex, 0);
  assert.equal(presentation.placeRouteBadge([100, 80], badge, bounds, [bounds], []).fallback, true);
});

/** Proves blocked semantic badges collapse into one ordered renderer-owned fallback pill. */
test('combines all no-clear viewer badges into one meaningful fallback pill', async () => {
  const labels = [];
  const markers = [];
  prepareLeaflet();
  globalThis.document.createElement = () => ({
    width: 0, height: 0, getContext: () => ({
      scale() {}, beginPath() {}, roundRect() {}, fill() {}, stroke() {},
      fillText(label) { labels.push(label); }
    }),
    toDataURL: () => `data:image/png;base64,${labels.at(-1)}`
  });
  globalThis.L.marker = (_position, options) => {
    const marker = presentationLayer({ complete: true, naturalWidth: 24, decode: () => Promise.resolve() })();
    markers.push(options);
    return marker;
  };
  const { createViewerSegmentBadgeRenderer } = await import(`../../wwwroot/js/Trip/viewerSegmentBadgeRenderer.js?blocked=${Date.now()}`);
  const map = presentationMap();
  map.getContainer = () => ({
    getBoundingClientRect: () => ({ left: 0, top: 0 }),
    querySelectorAll: () => [{
      offsetParent: {}, getBoundingClientRect: () => ({ left: 0, top: 0, right: 800, bottom: 600 })
    }]
  });
  const renderer = createViewerSegmentBadgeRenderer(map);

  renderer.render([{ label: 'A', location: [0, 0] }]);
  await renderer.waitForCurrent();
  assert.equal(renderer.count(), 1);
  assert.deepEqual(labels, ['A']);
  labels.length = 0;
  markers.length = 0;

  renderer.render([
    { label: 'A', location: [0, 0] },
    { label: 'B', location: [1, 0] },
    { label: 'C', location: [2, 0] }
  ]);
  await renderer.waitForCurrent();

  assert.equal(renderer.count(), 1);
  assert.deepEqual(labels, ['A/B/C']);
  assert.equal(markers.length, 1);
  assert.match(markers[0].icon.className, /segment-route-badge-fallback/);
});

/** Proves clear badges stay separate while only blocked labels combine and replacement remains bounded. */
test('preserves clear viewer badges and replaces only the blocked group', async () => {
  const labels = [];
  prepareLeaflet();
  globalThis.document.createElement = () => ({
    width: 0, height: 0, getContext: () => ({
      scale() {}, beginPath() {}, roundRect() {}, fill() {}, stroke() {},
      fillText(label) { labels.push(label); }
    }), toDataURL: () => 'data:image/png;base64,badge'
  });
  globalThis.L.marker = () => presentationLayer({ complete: true, naturalWidth: 24, decode: () => Promise.resolve() })();
  const { createViewerSegmentBadgeRenderer } = await import(`../../wwwroot/js/Trip/viewerSegmentBadgeRenderer.js?mixed=${Date.now()}`);
  const map = presentationMap();
  map.getContainer = () => ({
    getBoundingClientRect: () => ({ left: 0, top: 0 }),
    querySelectorAll: () => [{
      offsetParent: {}, getBoundingClientRect: () => ({ left: 330, top: 0, right: 800, bottom: 600 })
    }]
  });
  const renderer = createViewerSegmentBadgeRenderer(map);
  const badges = [
    { label: 'A/C', location: [0, 0] },
    { label: 'B', location: [10, 0] },
    { label: 'D', location: [20, 0] }
  ];

  renderer.render(badges);
  await renderer.waitForCurrent();
  assert.equal(renderer.count(), 2);
  assert.deepEqual(labels, ['A/C', 'B/D']);

  labels.length = 0;
  renderer.render(badges);
  await renderer.waitForCurrent();
  assert.equal(renderer.count(), 2);
  assert.deepEqual(labels, ['A/C', 'B/D']);
});

/** Proves normal URL initialization is not routed through print-only setup. */
test('normal requested Segment uses the common controller selection and resolved bounds', async () => {
  prepareLeaflet();
  globalThis.document.querySelectorAll = () => [];
  globalThis.requestAnimationFrame = callback => callback();
  const helpers = await import('../../wwwroot/js/Trip/tripViewerHelpers.js');
  helpers.disposeSegmentPresentation();
  const map = {
    _layers: [], removeLayer() {}, on() {}, fitBounds() {},
    latLngToLayerPoint: ([latitude, longitude]) => ({ x: longitude * 20, y: latitude * 20 }),
    latLngToContainerPoint: ([latitude, longitude]) => ({ x: longitude * 20, y: latitude * 20 }),
    layerPointToLatLng: ([x, y]) => [y / 20, x / 20],
    getSize: () => ({ x: 800, y: 600 }),
    getContainer: () => ({ getBoundingClientRect: () => ({ left: 0, top: 0 }), querySelectorAll: () => [] }),
    flyToBounds(bounds, options) { this.fitted = { bounds, options }; }
  };
  const line = () => ({
    addTo(target) { target?._layers?.push(this); return this; }, bindTooltip() { return this; }, unbindTooltip() { return this; },
    on() { return this; }, off() { return this; }, remove() { return this; }, setStyle() { return this; }, getBounds() { return 'resolved-route-bounds'; }
  });
  globalThis.L.polyline = line;
  helpers.addSegment(map, 'one', [[0, 0], [0, 10]], '', { anchors: [], orientation: 'forward' });
  helpers.addSegment(map, 'two', [[1, 0], [1, 10]], '', { anchors: [], orientation: 'forward' });
  const { createViewerSegmentPresentationController } = await import(`../../wwwroot/js/Trip/viewerSegmentPresentationController.js?normal=${Date.now()}`);
  const root = { dataset: {} };
  const controller = createViewerSegmentPresentationController(map, root, { isPrint: false, paddingX: () => 75 });

  controller.initialize('one');

  const snapshot = helpers.getSegmentPresentationSnapshot();
  assert.equal(snapshot.segments.find(item => item.id === 'one').active, true);
  assert.equal(snapshot.segments.find(item => item.id === 'two').active, false);
  assert.deepEqual(map.fitted, { bounds: 'resolved-route-bounds', options: { animate: true, duration: 1.2, padding: [75, 60] } });
  controller.initialize('missing');
  assert.equal(helpers.getSegmentPresentationSnapshot().segments.some(item => item.active), false);
});

/** Proves print readiness must remain false until production badge decoding completes. */
test('production presentation readiness waits for delayed badge decode', async () => {
  prepareLeaflet();
  globalThis.document.querySelectorAll = () => [];
  let resolveDecode;
  const decode = new Promise(resolve => { resolveDecode = resolve; });
  const frames = [];
  globalThis.requestAnimationFrame = callback => { frames.push(callback); return frames.length; };
  const helpers = await import('../../wwwroot/js/Trip/tripViewerHelpers.js');
  helpers.disposeSegmentPresentation();
  const map = {
    _layers: [], removeLayer() {}, on() {}, fitBounds() {},
    latLngToLayerPoint: ([latitude, longitude]) => ({ x: longitude * 20, y: latitude * 20 }),
    latLngToContainerPoint: ([latitude, longitude]) => ({ x: longitude * 20, y: latitude * 20 }),
    layerPointToLatLng: ([x, y]) => [y / 20, x / 20],
    getSize: () => ({ x: 800, y: 600 }),
    getContainer: () => ({ getBoundingClientRect: () => ({ left: 0, top: 0 }), querySelectorAll: () => [] })
  };
  const element = { complete: true, naturalWidth: 24, decode: () => decode };
  const layer = () => ({
    addTo(target) { target?._layers?.push(this); return this; }, bindTooltip() { return this; }, unbindTooltip() { return this; },
    on() { return this; }, off() { return this; }, remove() { return this; }, setStyle() { return this; }, getElement() { return element; }, getBounds() { return {}; }
  });
  globalThis.L.polyline = layer;
  globalThis.L.marker = layer;
  globalThis.location.search = '?print=1&seg=one';
  globalThis.window.__segmentPresentationReady = false;
  helpers.addSegment(map, 'one', [[0, 0], [0, 10]], '', { anchors: [
    { position: 0, placeId: 'a', name: 'A', role: 'Start', longitude: 0, latitude: 0 },
    { position: 1, placeId: 'b', name: 'B', role: 'End', longitude: 10, latitude: 0 }
  ], orientation: 'forward' });
  const { createViewerSegmentPresentationController } = await import(`../../wwwroot/js/Trip/viewerSegmentPresentationController.js?decode=${Date.now()}`);
  const controller = createViewerSegmentPresentationController(map, { dataset: {} }, { isPrint: true, paddingX: () => 60 });
  const ready = controller.initialize('one');
  frames.splice(0).forEach(callback => callback());
  assert.equal(globalThis.window.__segmentPresentationReady, false);
  globalThis.requestAnimationFrame = callback => callback();
  resolveDecode();
  await ready;
  assert.equal(globalThis.window.__segmentPresentationReady, true);
});

/** Proves a rejected production decode cannot publish a false-ready print state. */
test('failed production badge decode leaves presentation readiness false', async () => {
  prepareLeaflet();
  globalThis.document.querySelectorAll = () => [];
  globalThis.requestAnimationFrame = callback => callback();
  const helpers = await import('../../wwwroot/js/Trip/tripViewerHelpers.js');
  helpers.disposeSegmentPresentation();
  const map = presentationMap();
  const layer = presentationLayer({ complete: true, naturalWidth: 0, decode: () => Promise.reject(new Error('decode failed')) });
  globalThis.L.polyline = layer;
  globalThis.L.marker = layer;
  globalThis.location.search = '?print=1&seg=one';
  globalThis.window.__segmentPresentationReady = false;
  helpers.addSegment(map, 'one', [[0, 0], [0, 10]], '', { anchors: badgeAnchors('a', 'b'), orientation: 'forward' });
  const { createViewerSegmentPresentationController } = await import(`../../wwwroot/js/Trip/viewerSegmentPresentationController.js?failed=${Date.now()}`);

  const ready = await createViewerSegmentPresentationController(map, { dataset: {} }, { isPrint: true, paddingX: () => 60 }).initialize('one');

  assert.equal(ready, false);
  assert.equal(globalThis.window.__segmentPresentationReady, false);
});

/** Proves replacement ignores an older pending badge generation. */
test('replacement render ignores stale production badge promises', async () => {
  prepareLeaflet();
  globalThis.document.querySelectorAll = () => [];
  globalThis.requestAnimationFrame = callback => callback();
  const helpers = await import('../../wwwroot/js/Trip/tripViewerHelpers.js');
  helpers.disposeSegmentPresentation();
  const map = presentationMap();
  let resolveOld;
  const oldDecode = new Promise(resolve => { resolveOld = resolve; });
  let markerCount = 0;
  globalThis.L.polyline = presentationLayer(null);
  globalThis.L.marker = () => presentationLayer(markerCount++ < 2
    ? { complete: true, naturalWidth: 24, decode: () => oldDecode }
    : { complete: true, naturalWidth: 24, decode: () => Promise.resolve() })();
  globalThis.location.search = '';
  helpers.addSegment(map, 'one', [[0, 0], [0, 10]], '', { anchors: badgeAnchors('a', 'b'), orientation: 'forward' });
  const { createViewerSegmentPresentationController } = await import(`../../wwwroot/js/Trip/viewerSegmentPresentationController.js?stale=${Date.now()}`);
  const controller = createViewerSegmentPresentationController(map, { dataset: {} }, { isPrint: true, paddingX: () => 60 });
  globalThis.window.__segmentPresentationReady = false;
  const oldInitialization = controller.initialize('one');
  const replacementReady = await controller.initialize('one');

  assert.equal(replacementReady, true);
  assert.equal(globalThis.window.__segmentPresentationReady, true);
  assert.equal(helpers.getSegmentPresentationSnapshot().segments.find(item => item.id === 'one').active, true);
  resolveOld();
  assert.equal(await oldInitialization, false);
  assert.equal(globalThis.window.__segmentPresentationReady, true);
});

/** Supplies a complete bounded map surface for production presentation unit tests. */
const presentationMap = () => ({
  _layers: [], removeLayer() {}, on() {}, fitBounds() {}, flyToBounds() {},
  latLngToLayerPoint: ([latitude, longitude]) => ({ x: longitude * 20, y: latitude * 20 }),
  latLngToContainerPoint: ([latitude, longitude]) => ({ x: longitude * 20 + 200, y: latitude * 20 + 200 }),
  layerPointToLatLng: ([x, y]) => [y / 20, x / 20], getSize: () => ({ x: 800, y: 600 }),
  getContainer: () => ({ getBoundingClientRect: () => ({ left: 0, top: 0 }), querySelectorAll: () => [] })
});

/** Supplies replaceable Leaflet layers and an optional production image element. */
const presentationLayer = element => () => ({
  _layers: [], addTo(target) { target?._layers?.push(this); return this; }, bindTooltip() { return this; }, unbindTooltip() { return this; },
  on() { return this; }, off() { return this; }, remove() { return this; }, clearLayers() { this._layers = []; return this; },
  getLayers() { return this._layers; }, setStyle() { return this; }, getElement() { return element; }, getBounds() { return {}; }
});

/** Returns the smallest complete production badge anchor pair. */
const badgeAnchors = (start, end) => [
  { position: 0, placeId: start, name: start, role: 'Start', longitude: 0, latitude: 0 },
  { position: 1, placeId: end, name: end, role: 'End', longitude: 10, latitude: 0 }
];
