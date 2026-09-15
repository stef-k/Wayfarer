import assert from 'node:assert/strict';
import test from 'node:test';
import { readFile } from 'node:fs/promises';
import { embeddedMapOptions, installEmbeddedMap } from '../../wwwroot/js/embeddedMap.js';
import { buildMapEmbed, copyMapEmbed, handleEmbedCopy } from '../../wwwroot/js/embedSharing.js';

/** Leaflet boundary double: exercise the production owner's real DOM listeners and lifecycle. */
const mount = () => {
    const handler = enabled => ({ enabled: () => enabled, enable: () => { enabled = true; },
        disable: () => { enabled = false; } });
    const container = new EventTarget();
    container.style = { touchAction: '' };
    const controls = new Set();
    const events = new Map();
    const map = { dragging: handler(true), scrollWheelZoom: handler(false),
        getContainer: () => container, on: (type, fn) => events.set(type, fn),
        off: type => events.delete(type) };
    globalThis.document = { createElement: () => ({}) };
    globalThis.L = { DomEvent: { disableClickPropagation: () => {} }, control: () => ({
        addTo() { this.element = this.onAdd(); controls.add(this); }, remove() { controls.delete(this); }
    }) };
    const send = (type, values = {}) => {
        const event = Object.assign(new Event(type, { cancelable: true }), values);
        container.dispatchEvent(event);
        return event;
    };
    return { map, container, controls, events, send };
};

test('normal and print initialization preserve defaults; both embed consumers use the shared owner', async () => {
    assert.deepEqual(embeddedMapOptions(false), {});
    assert.deepEqual(embeddedMapOptions(true, true), {});
    assert.deepEqual(embeddedMapOptions(true), { scrollWheelZoom: false });
    const trip = await readFile('wwwroot/js/Trip/tripViewerHelpers.js', 'utf8');
    const timeline = await readFile('wwwroot/js/Areas/Public/UsersTimeline/Embed.js', 'utf8');
    assert.match(trip, /embeddedMapOptions\(Boolean\(embedUrl\), isPrint\)/);
    assert.match(trip, /if \(embedUrl && !isPrint\) installEmbeddedMap/);
    assert.match(timeline, /embeddedMapOptions\(true\)/);
    assert.match(timeline, /installEmbeddedMap\(mapContainer,/);
    assert.doesNotMatch(timeline, /scrollWheelZoom:/);
});

test('intentional gestures do not permanently enable ordinary wheel or single-touch pan', () => {
    Object.defineProperty(globalThis, 'navigator', { configurable: true, value: { platform: 'Win32' } });
    const fixture = mount();
    const dispose = installEmbeddedMap(fixture.map, '/Public/Trips/trip-id');
    fixture.send('wheel', { ctrlKey: true });
    assert.equal(fixture.map.scrollWheelZoom.enabled(), true);
    fixture.send('wheel', { ctrlKey: false });
    assert.equal(fixture.map.scrollWheelZoom.enabled(), false);
    navigator.platform = 'MacIntel';
    fixture.send('wheel', { metaKey: true });
    assert.equal(fixture.map.scrollWheelZoom.enabled(), true);
    fixture.send('wheel', { ctrlKey: true, metaKey: false });
    assert.equal(fixture.map.scrollWheelZoom.enabled(), false);
    fixture.send('pointerdown', { pointerType: 'touch' });
    assert.equal(fixture.map.dragging.enabled(), false);
    assert.equal(fixture.send('touchstart', { touches: [{}] }).defaultPrevented, false);
    assert.equal(fixture.send('touchstart', { touches: [{}, {}] }).defaultPrevented, true);
    fixture.send('pointerdown', { pointerType: 'mouse' });
    assert.equal(fixture.map.dragging.enabled(), true);
    dispose();
});

test('full-view link stays independent of gestures and reinitialization releases listeners and controls', () => {
    const fixture = mount();
    const dispose = installEmbeddedMap(fixture.map, '/Public/Users/Timeline/alice');
    assert.equal(installEmbeddedMap(fixture.map, '/ignored'), dispose);
    assert.equal(fixture.controls.size, 1);
    const link = [...fixture.controls][0].element;
    assert.equal(link.textContent, 'Open full view');
    assert.equal(link.href, '/Public/Users/Timeline/alice');
    assert.equal(link.target, '_blank');
    assert.equal(link.rel, 'noopener');
    fixture.events.get('unload')();
    assert.equal(fixture.controls.size, 0);
    assert.equal(fixture.container.style.touchAction, '');
    fixture.send('pointerdown', { pointerType: 'touch' });
    assert.equal(fixture.map.dragging.enabled(), true);
    const nextDispose = installEmbeddedMap(fixture.map, '/Public/Trips/another');
    assert.notEqual(nextDispose, dispose);
    assert.equal(fixture.controls.size, 1);
    nextDispose();
});

test('canonical URL and escaped HTML share the public browser origin and omit transient state', () => {
    const trip = buildMapEmbed({ kind: 'trip', id: 'trip-id', title: 'A & "B" <map>\'s' }, 'https://maps.example.test:8443');
    assert.equal(trip.url, 'https://maps.example.test:8443/Public/Trips/trip-id?embed=true');
    assert.equal(trip.html, '<iframe src="https://maps.example.test:8443/Public/Trips/trip-id?embed=true" title="A &amp; &quot;B&quot; &lt;map&gt;&#39;s" width="100%" height="600" loading="lazy" style="border:0;"></iframe>');
    const timeline = buildMapEmbed({ kind: 'timeline', id: 'alice + smith' }, 'https://maps.example.test');
    assert.equal(timeline.url, 'https://maps.example.test/Public/Users/Timeline/alice%20%2B%20smith/embed');
    assert.ok(timeline.html.includes(`src="${timeline.url}"`));
    assert.match(timeline.html, /title="Wayfarer timeline map"/);
    assert.doesNotMatch(timeline.html, /sandbox|frameborder|postMessage|token|lat=|zoom=/);
    for (const origin of ['http://maps.example.test', 'https://localhost:7150', 'https://127.0.0.1',
        'https://10.0.0.5', 'https://172.16.2.3', 'https://192.168.1.2', 'https://server.internal']) {
        assert.throws(() => buildMapEmbed({ kind: 'trip', id: 'id' }, origin), /public HTTPS address/);
    }
});

test('copy actions report successful writes and rejected writes/origins through existing feedback', async () => {
    const writes = [];
    const notifications = [];
    globalThis.wayfarer = { showToast: (...args) => notifications.push(args) };
    navigator.clipboard = { writeText: async value => writes.push(value) };
    const element = { dataset: { embedKind: 'trip', embedId: 'id', embedTitle: 'Trip', embedFormat: 'url' } };
    const origin = 'https://maps.example.test';
    await copyMapEmbed(element, origin);
    element.dataset.embedFormat = 'html';
    await copyMapEmbed(element, origin);
    assert.ok(writes[1].includes(`src="${writes[0]}"`));
    assert.deepEqual(notifications.map(item => item[0]), ['success', 'success']);
    navigator.clipboard.writeText = async () => { throw new Error('denied'); };
    await copyMapEmbed(element, origin);
    assert.equal(notifications.at(-1)[0], 'danger');
    delete wayfarer.showToast;
    wayfarer.showAlert = (...args) => notifications.push(args);
    await copyMapEmbed(element, 'http://localhost');
    assert.match(notifications.at(-1)[1], /public HTTPS address/);
    assert.equal(writes.length, 2);
    let prevented = false;
    globalThis.window = { location: { origin } };
    await handleEmbedCopy({ target: { closest: () => element }, preventDefault: () => { prevented = true; } });
    assert.equal(prevented, true);
    assert.equal(notifications.at(-1)[0], 'danger');
});
