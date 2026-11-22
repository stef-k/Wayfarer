import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import test from 'node:test';

// Use the same Greek catalog and status sink as the document.
const catalog = readFileSync('Resources/Groups.el-GR.resx', 'utf8');
const messages = Object.fromEntries(['HistoryFailed', 'HistoryEmpty'].map(key => {
    const id = `Groups.${key}`;
    const entry = catalog.match(new RegExp(`<data name="${id}"[^>]*>\\s*<value>([^<]+)</value>`));
    return [id, entry[1]];
}));
const status = { hidden: true, textContent: '', classList: { toggle() {} } };
globalThis.document = {
    getElementById: id => id === 'wayfarer-locale'
        ? { textContent: JSON.stringify({ locale: 'el-GR', fallback: 'en-US', messages, english: {} }) }
        : id === 'groupHistoryStatus' ? status : null
};
const { setGroupMapStatus } = await import('../../wwwroot/js/util/group-map-presentation.js');
const { groupMessage: t } = await import('../../wwwroot/js/util/group-web-messages.js');

for (const area of ['User', 'Manager']) {
    test(`${area} Group history renders and announces only the current view's result`, async () => {
        const source = readFileSync(`wwwroot/js/Areas/${area}/Groups/Map.js`, 'utf8');
        const start = source.indexOf('\n  /**', source.indexOf('  async function loadLatest('));
        const end = source.indexOf('  // initial', start);
        const moveStart = source.indexOf('  // move refresh', end);
        const moveEnd = source.indexOf('  // Subscribe', moveStart);
        const initialCall = source.match(/^  (loadViewport\(\)[^\n]*);$/m)[1];
        const datePicker = { value: '2025-01-15' };
        const requests = [];
        const activeLayers = new Set();
        let move;
        let refresh;
        setGroupMapStatus('groupHistoryStatus');

        // Run the shipped request flow with controlled network completion and Leaflet layers.
        const loadViewport = runInNewContext(`${source.slice(start, end)}${source.slice(moveStart, moveEnd)}
            () => ${initialCall}`, {
            groupId: 'group',
            selectedUsers: () => [{ id: 'selected-member' }],
            toBBox: () => ({ MinLng: 5, MinLat: 5, MaxLng: 50, MaxLat: 50, ZoomLevel: 9 }),
            isViewingToday: () => false,
            document: {
                querySelector: () => ({ value: 'day' }),
                getElementById: id => id === 'datePicker' ? datePicker : null
            },
            postJson: (url, body) => new Promise((resolve, reject) => requests.push({ url, body, resolve, reject })),
            restClusters: new Map(),
            map: {
                on: (_, handler) => { move = handler; },
                addLayer: layer => activeLayers.add(layer),
                removeLayer: layer => activeLayers.delete(layer)
            },
            setTimeout: handler => { refresh = handler; },
            clearTimeout() {},
            idToInfoMap: () => new Map(),
            L: {
                markerClusterGroup: () => ({ markers: [], addLayer(marker) { this.markers.push(marker); } }),
                divIcon: () => ({}),
                marker: coordinates => ({ coordinates, bindTooltip() { return this; }, on() {} })
            },
            restIconHtml: () => '',
            groupLocationTooltip: () => '',
            setGroupMapStatus,
            t
        });

        const older = loadViewport();
        datePicker.value = '2025-01-16';
        const current = loadViewport();
        assert.deepEqual(requests.map(request => request.body.Day), [15, 16]);
        assert.equal(requests[1].url, '/api/groups/group/locations/query');
        assert.deepEqual(Array.from(requests[1].body.UserIds), ['selected-member']);
        const coordinates = { latitude: 11, longitude: 12 };
        requests[1].resolve({ results: [{ userId: 'selected-member', coordinates }] });
        await current;
        const [currentLayer] = activeLayers;
        assert.deepEqual(Array.from(currentLayer.markers[0].coordinates), [11, 12]);
        assert.equal(status.hidden, true);
        requests[0].reject(new Error('older date failed'));
        await older;
        assert.equal(status.hidden, true, 'the previous date must stay quiet');
        assert.deepEqual([...activeLayers], [currentLayer]);

        const beforeMove = loadViewport();
        move();
        requests[2].resolve({ results: [] });
        await beforeMove;
        assert.equal(status.hidden, true, 'a pending move refresh already owns the view');
        assert.deepEqual([...activeLayers], [currentLayer]);

        const afterMove = refresh();
        requests[3].reject(new Error('current view failed'));
        await afterMove;
        assert.equal(status.hidden, false);
        assert.equal(status.textContent, messages['Groups.HistoryFailed']);
        assert.deepEqual([...activeLayers], [currentLayer]);
    });
}
