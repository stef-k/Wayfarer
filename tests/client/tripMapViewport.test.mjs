import assert from 'node:assert/strict';
import test from 'node:test';
import { EventEmitter } from 'node:events';
import { canonicalMapView, createMapViewport, replaceUrlMapView, resolveUrlMapView } from '../../ClientApps/trip-editor/src/map/mapViewport.ts';

const base = { center: { latitude: 37, longitude: 23 }, zoom: 8 };

test('URL components independently override the best base with finite, bounded values', () => {
  for (const [query, latitude, longitude, zoom] of [
    ['?lat=12.3456&lng=45.6789&zoom=9', 12.3456, 45.6789, 9],
    ['?lat=38&lng=24', 38, 24, 8], ['?zoom=11', 37, 23, 11],
    ['?lat=38&zoom=9', 38, 23, 9], ['?lng=24', 37, 24, 8],
    ['?lat=91&lng=-181&zoom=20', 37, 23, 8],
    ['?lat=Infinity&lng=NaN&zoom=-1', 37, 23, 8],
    ['?lat=&lng=hello&zoom=', 37, 23, 8],
    ['?lat=-90&lng=180&zoom=0', -90, 180, 0], ['?zoom=9.6', 37, 23, 10]
  ]) assert.deepEqual(resolveUrlMapView(query, base), { center: { latitude, longitude }, zoom }, query);
});

test('canonical URLs retain history state, unrelated query keys and hash and replace duplicate viewport keys', () => {
  const state = { navigation: 'retained' };
  const calls = [];
  const browser = { location: { href: 'https://example.test/editor?tag=a&lat=1&lat=2&lng=0#section' },
    history: { state, replaceState: (...args) => calls.push(args) } };
  replaceUrlMapView({ center: { latitude: 38.12345678, longitude: 540.25 }, zoom: 10.7 }, browser);
  assert.equal(calls[0][0], state);
  assert.equal(calls[0][2].href, 'https://example.test/editor?tag=a&lat=38.123457&lng=-179.750000&zoom=11#section');
  assert.deepEqual(canonicalMapView({ center: { latitude: -93, longitude: -541 }, zoom: 22 }),
    { center: { latitude: -90, longitude: 179 }, zoom: 19 });
  const antimeridian = canonicalMapView({ center: { latitude: 0, longitude: 179.99999999 }, zoom: 9 });
  assert.equal(antimeridian.center.longitude, -180);
  assert.deepEqual(canonicalMapView(antimeridian), antimeridian, 'rounding remains stable across serialization and reload');
});

test('only gestures capture: asynchronous commands, paired terminal events, auto-pan, resize and map-work remain transient', () => {
  const previousWindow = globalThis.window;
  const frames = new Map();
  let frameId = 0;
  const browser = Object.assign(new EventTarget(), { location: { href: 'https://example.test/editor' },
    requestAnimationFrame(callback) { frames.set(++frameId, callback); return frameId; },
    cancelAnimationFrame(id) { frames.delete(id); },
    history: { state: null, replaceState(_state, _title, url) { browser.location.href = String(url); } } });
  globalThis.window = browser;
  const previousDocument = globalThis.document;
  globalThis.document = new EventTarget();
  const element = Object.assign(new EventTarget(), { dataset: {}, querySelectorAll: () => [] });
  const events = new EventEmitter();
  const fire = name => events.emit(name, { type: name });
  const frame = () => { const callbacks = [...frames.values()]; frames.clear(); callbacks.forEach(callback => callback()); };
  let current = structuredClone(base);
  let work = false;
  const captures = [];
  // Minimal Leaflet boundary; assertions exercise the shipped event owner, not harness internals.
  const map = { getContainer: () => element, getCenter: () => ({ lat: current.center.latitude, lng: current.center.longitude }),
    getZoom: () => current.zoom, options: { wheelDebounceTime: 40 },
    scrollWheelZoom: { enabled: () => true }, keyboard: { enabled: () => true }, touchZoom: { enabled: () => true },
    doubleClickZoom: { enabled: () => true },
    on: (names, fn) => names.split(' ').forEach(name => events.on(name, fn)),
    off: (names, fn) => names.split(' ').forEach(name => events.off(name, fn)) };
  const owner = createMapViewport(map, { canCapture: () => !work, onCaptured: view => captures.push(view) });
  const finish = (latitude, zoom = 9) => {
    current = { center: { latitude, longitude: 24 }, zoom };
    events.emit('zoomend');
    events.emit('moveend');
  };
  try {
    owner.initialize(() => finish(38));
    assert.equal(browser.location.href, 'https://example.test/editor', 'initial placement leaves clean URL clean');
    frame();
    owner.navigate(() => {}); // The command's animation start is deferred until the next frame.
    element.dispatchEvent(new Event('wheel')); // This input must not claim the queued command's movement.
    fire('zoomstart');
    fire('movestart');
    frame();
    finish(39); // Terminal pair arrives after the command call returned.
    events.emit('moveend');
    assert.equal(captures.length, 0);
    assert.match(browser.location.href, /lat=39.000000/);
    events.emit('dragstart');
    finish(40);
    assert.equal(captures.length, 1, 'next genuine gesture is released');
    finish(41); // Leaflet resize or unsolicited terminal event is not user intent.
    assert.equal(captures.length, 1);
    events.emit('dragstart');
    events.emit('autopanstart');
    finish(42);
    assert.equal(captures.length, 1);
    work = true;
    events.emit('dragstart');
    work = false; // Work ends before inertia settles; it still cannot capture.
    finish(43);
    assert.equal(captures.length, 1);
    element.dispatchEvent(new Event('wheel'));
    fire('zoomstart');
    finish(44, 10);
    assert.equal(captures.length, 2);
    assert.equal(element.dataset.tripEditorMapLat, '44.000000');
    element.dispatchEvent(Object.assign(new Event('keydown'), { keyCode: 39 }));
    fire('movestart');
    finish(45, 10);
    element.dispatchEvent(Object.assign(new Event('touchstart'), { touches: [{}, {}] }));
    fire('zoomstart');
    finish(46, 11);
    for (const pointerId of [1, 2]) element.dispatchEvent(Object.assign(new Event('pointerdown'), { pointerId, pointerType: 'touch' }));
    fire('zoomstart');
    finish(47, 12);
    element.dispatchEvent(new Event('dblclick'));
    fire('zoomstart');
    finish(48, 13);
    assert.equal(captures.length, 6, 'keyboard, native/pointer pinch, and double-click are recognized gestures');
    owner.dispose();
    events.emit('dragstart');
    finish(49);
    assert.equal(captures.length, 6, 'disposed listeners no longer publish');
  } finally { owner.dispose(); globalThis.window = previousWindow; globalThis.document = previousDocument; }
});
