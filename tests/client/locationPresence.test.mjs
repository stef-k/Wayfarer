import assert from 'node:assert/strict';
import test from 'node:test';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';
import { isLiveLocation, scheduleLocationExpiry } from '../../wwwroot/js/util/datetime.js';

const now = Date.parse('2026-10-19T12:00:00Z');
const fresh = { id: 42, presenceObservedAtUtc: new Date(now - 1000).toISOString(), locationTimeThresholdMinutes: 5,
    localTimestamp: new Date(now).toISOString(), isLive: true, coordinates: { latitude: 10, longitude: 20 } };

test('observation freshness fails closed independently of recent human event time', () => {
    assert.equal(isLiveLocation(fresh, now), true);
    assert.equal(isLiveLocation({ ...fresh, presenceObservedAtUtc: null }, now), false);
    assert.equal(isLiveLocation({ ...fresh, presenceObservedAtUtc: new Date(now - 360000).toISOString() }, now), false);
    assert.equal(isLiveLocation({ ...fresh, presenceObservedAtUtc: new Date(now + 1).toISOString() }, now), false);
    assert.equal(isLiveLocation({ ...fresh, presenceObservedAtUtc: 'invalid' }, now), false);
    assert.equal(isLiveLocation({ ...fresh, locationTimeThresholdMinutes: 0 }, now), false);
    assert.equal(isLiveLocation({ ...fresh, locationTimeThresholdMinutes: null }, now), false);
    assert.equal(isLiveLocation({ ...fresh, locationTimeThresholdMinutes: -1 }, now), false);
});

/** Execute actual renderer/timer owners at a controlled Leaflet clock boundary, including time spent rendering. */
for (const entry of ['Public/UsersTimeline/Embed.js', 'Public/UsersTimeline/Timeline.js', 'User/Location/Index.js',
    'User/Timeline/Index.js', 'User/Timeline/Chronological.js']) {
    test(`${entry} withholds historical LIVE and expires at/during the render boundary`, async () => {
        const source = await readFile(`wwwroot/js/Areas/${entry}`, 'utf8');
        const block = name => {
            const start = source.indexOf(`const ${name} = (`);
            return source.slice(start, source.indexOf('\n};', start) + 3);
        };
        const icons = [];
        const callbacks = new Map();
        let clock = now;
        let renderDelay = 0;
        let render;
        let nextId = 0;
        const layer = () => ({ addLayer() {}, clearLayers() {}, addTo() {}, bringToFront() {} });
        const context = vm.createContext({ markerTransitionTimer: null,
            Date: class extends Date { static now() { return clock; } },
            setTimeout: (fn, delay) => { const id = ++nextId; callbacks.set(id, { fn, delay }); return id; },
            clearTimeout: id => callbacks.delete(id), displayLocationsOnMap: (_, rows) => render(rows),
            highlightLayer: layer(), mapContainer: { getZoom: () => 3, addLayer() {} },
            latestLocationMarker: 'latest', liveMarker: 'live',
            L: { layerGroup: layer, markerClusterGroup: layer, marker: (_, options) => {
                icons.push(options.icon);
                clock += renderDelay;
                return { bindTooltip() {}, on() {}, addTo() {}, setZIndexOffset() {} };
            } } });
        vm.runInContext(`const isLiveLocation = ${isLiveLocation.toString()};
            const scheduleLocationExpiry = ${scheduleLocationExpiry.toString()};
            ${block('scheduleMarkerTransition')}\n${block('buildLayers')}`, context);
        render = vm.runInContext('buildLayers', context);
        render([{ ...fresh, presenceObservedAtUtc: null, isLatestLocation: true }]);
        assert.equal(icons.at(-1), 'latest');
        assert.equal(callbacks.size, 0);
        for (const remaining of [0, 1]) {
            clock = now;
            renderDelay = remaining ? 2 : 0;
            render([{ ...fresh, isLatestLocation: true,
                presenceObservedAtUtc: new Date(now - 300000 + remaining).toISOString() }]);
            assert.equal(icons.at(-1), 'live');
            const expiry = [...callbacks.values()][0];
            assert.equal(expiry.delay, 1);
            clock += expiry.delay;
            expiry.fn();
            assert.equal(icons.at(-1), 'latest');
            assert.equal(callbacks.size, 0);
        }
    });
}

/** Invoke the actual Group marker owner at its Leaflet boundary with controlled time and scheduled callbacks. */
for (const area of ['User', 'Manager']) {
    test(`${area} Group marker expires while idle, stays last-known and cannot reappear after removal`, async () => {
        const source = await readFile(`wwwroot/js/Areas/${area}/Groups/Map.js`, 'utf8');
        const start = source.indexOf('  function upsertLatestForUser(');
        const end = source.indexOf('\n  /**', start);
        const callbacks = new Map();
        let clock = now;
        let renderDelay = 0;
        let nextId = 0;
        const latestMarkers = new Map();
        const timers = { setTimeout: (fn, delay) => { const id = ++nextId; callbacks.set(id, { fn, delay }); return id; },
            clearTimeout: id => callbacks.delete(id) };
        const context = vm.createContext({ ...timers, latestMarkers, map: {},
            Date: class extends Date { static now() { return clock; } },
            idToInfoMap: () => new Map(), colorFromString: () => 'red',
            latestIconHtml: (_, live) => live ? 'live' : 'latest', buildTooltipHtml: () => 'Location',
            openLocationModal: () => {},
            L: { divIcon: options => options, marker: (_, options) => { clock += renderDelay; return { icon: options.icon, handlers: {},
                bindTooltip(text) { this.tooltip = text; return this; }, on(type, fn) { this.handlers[type] = fn; },
                addTo() {}, setLatLng() {}, setIcon(icon) { this.icon = icon; clock += renderDelay; }, setZIndexOffset() {} }; } } });
        // Import-free production functions execute in the same controlled clock/timer realm as the marker owner.
        vm.runInContext(`const isLiveLocation = ${isLiveLocation.toString()};
            const scheduleLocationExpiry = ${scheduleLocationExpiry.toString()};\n${source.slice(start, end)}`, context);
        context.location = { ...fresh, presenceObservedAtUtc: null };
        vm.runInContext('upsertLatestForUser("owner", location)', context);
        assert.equal(latestMarkers.get('owner').icon.html, 'latest');
        assert.equal(callbacks.size, 0);
        context.location = fresh;
        vm.runInContext('upsertLatestForUser("owner", location)', context);
        const marker = latestMarkers.get('owner');
        assert.equal(marker.icon.html, 'live');
        const expiry = [...callbacks.values()][0];
        assert.equal(expiry.delay, 299001);
        clock = now + expiry.delay;
        expiry.fn();
        assert.equal(marker.icon.html, 'latest');
        assert.match(marker.tooltip, /Latest Location/);
        assert.equal(callbacks.size, 0);
        clock = now;
        vm.runInContext('upsertLatestForUser("owner", location)', context);
        const removedExpiry = [...callbacks.values()][0];
        marker.handlers.remove();
        latestMarkers.delete('owner');
        assert.equal(callbacks.size, 0);
        clock += removedExpiry.delay;
        removedExpiry.fn();
        assert.equal(latestMarkers.size, 0);
        for (const remaining of [0, 1]) {
            clock = now;
            renderDelay = remaining ? 2 : 0;
            context.location = { ...fresh, presenceObservedAtUtc: new Date(now - 300000 + remaining).toISOString() };
            vm.runInContext('upsertLatestForUser("owner", location)', context);
            const atBoundary = latestMarkers.get('owner');
            assert.equal(atBoundary.icon.html, 'live');
            const due = [...callbacks.values()][0];
            assert.equal(due.delay, 1);
            clock += due.delay;
            due.fn();
            assert.equal(atBoundary.icon.html, 'latest');
            assert.equal(callbacks.size, 0);
        }
    });
}
