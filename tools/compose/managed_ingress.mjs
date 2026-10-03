/** One mounted #749 journey against the running Compose endpoint; no product events are injected. */
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { createInterface } from 'node:readline';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

let phase = 'startup'; // Retain only the bounded phase name on failure, never raw browser diagnostics.
let statisticsError = false; // A production page exception is a counterexample, not acceptable qualification debt.

/** Project only non-secret joins and reject partial, unrelated or pre-replacement observations. */
export const validateObservation = (facts, origin, username) => {
    const canonical = `/Public/Users/Timeline/${encodeURIComponent(username)}`;
    assert.equal(facts.embedUrl, `${origin}${canonical}/embed`);
    assert.equal(facts.fullViewPath, canonical);
    assert.equal(facts.leaflet, true);
    assert.match(facts.tile.path, /^\/Public\/tiles\/\d+\/\d+\/\d+\.png$/);
    assert.ok(Number.isInteger(facts.tile.status) && facts.tile.status >= 200);
    assert.ok(facts.connections >= 2);
    assert.equal(facts.first.connection, 1);
    assert.ok(facts.second.connection > facts.first.connection);
    assert.notEqual(facts.first.locationId, facts.second.locationId);
    const update = value => {
        assert.ok(Number.isInteger(value.locationId) && value.locationId > 0);
        assert.equal(value.sse, true);
        assert.equal(value.refresh, true);
        return { connection: value.connection, locationId: value.locationId, sse: true, refresh: true };
    };
    return { embedUrl: facts.embedUrl, fullViewPath: canonical, leaflet: true,
        tile: { path: facts.tile.path, status: facts.tile.status }, connections: facts.connections,
        first: update(facts.first), second: update(facts.second) };
};

/** Host owns check-ins and Compose replacement; this process keeps the same browser/iframe alive. */
const probe = async () => {
    const input = createInterface({ input: process.stdin });
    const lines = input[Symbol.asyncIterator]();
    const receive = async () => JSON.parse((await lines.next()).value);
    const exchange = async (action, extra = {}) => {
        process.stdout.write(JSON.stringify({ action, ...extra }) + '\n');
        return receive();
    };
    const { origin, username } = await receive();
    const { chromium } = createRequire(import.meta.url)('/app/.playwright/package');
    // Curl/Python separately validate the leaf using only the fixture ACME root.
    // Match the established cross-origin embed Chromium convention for the mounted observation.
    const browser = await chromium.launch();
    try {
        const page = await browser.newPage({ ignoreHTTPSErrors: true });
        page.on('pageerror', error => {
            if (error.message === 'Username is required') statisticsError = true;
        });
        page.setDefaultTimeout(30000);
        const canonical = `/Public/Users/Timeline/${encodeURIComponent(username)}`;
        const embedUrl = `${origin}${canonical}/embed`;
        const streamUrl = `${origin}/api/sse/stream/location-update/${username}`;
        const connections = [];
        const refreshes = [];
        let tile;
        page.on('response', async response => {
            const url = new URL(response.url());
            if (url.origin !== origin) return;
            if (response.url() === streamUrl && response.status() === 200) {
                assert.equal(response.request().resourceType(), 'eventsource');
                assert.match(response.headers()['content-type'], /^text\/event-stream/);
                connections.push({ id: null, events: new Set() });
            }
            if (/^\/Public\/tiles\/\d+\/\d+\/\d+\.png$/.test(url.pathname)) {
                tile ??= { path: url.pathname, status: response.status() };
            }
            if (url.pathname === '/Public/Users/GetPublicTimeline' && response.ok()) {
                const data = await response.json();
                refreshes.push({ connection: connections.length,
                    ids: Array.isArray(data.data) ? data.data.map(location => location.id) : [] });
            }
        });
        // The parent has no scripts; only its actual production iframe reaches the managed proxy.
        await page.route('https://embed-host.example.test/', route => route.fulfill({ contentType: 'text/html',
            body: `<!doctype html><iframe title="Timeline" src="${embedUrl}" style="width:900px;height:600px"></iframe>` }));
        await page.goto('https://embed-host.example.test/');
        phase = 'mounted-map';
        const frame = page.frameLocator('iframe');
        await frame.locator('#mapContainer.leaflet-container .leaflet-tile-pane').waitFor();
        const escape = frame.getByRole('link', { name: 'Open full view', exact: true });
        await escape.waitFor();
        assert.equal(await escape.getAttribute('href'), canonical);
        // Chromium isolates the cross-origin iframe in its own target; observe that target's real SSE messages.
        const child = page.frames().find(value => value.url().startsWith(embedUrl));
        phase = 'sse-observer';
        const session = await page.context().newCDPSession(child).catch(error => {
            if (!error.message.includes('does not have a separate CDP session')) throw error;
            return page.context().newCDPSession(page);
        });
        await session.send('Network.enable');
        session.on('Network.eventSourceMessageReceived', event => {
            const connection = connections.at(-1);
            if (connection && (connection.id === null || connection.id === event.requestId)) {
                connection.id = event.requestId;
                connection.events.add(JSON.parse(event.data).locationId);
            }
        });
        // Waits synchronize observed transport/data; they never sleep to manufacture ordering.
        const waitFor = async (predicate, label) => {
            const until = Date.now() + 30000;
            while (!predicate()) {
                assert.ok(Date.now() < until, label);
                await new Promise(resolve => setTimeout(resolve, 50));
            }
        };
        await waitFor(() => connections.length === 1 && refreshes.length && tile, 'Initial SSE/map transport missing');
        const observe = async (action, connection) => {
            phase = action;
            const start = refreshes.length;
            const { locationId } = await exchange(action);
            await waitFor(() => connections[connection - 1].events.has(locationId) &&
                refreshes.slice(start).some(value => value.connection === connection && value.ids.includes(locationId)),
                'Bearer SSE message and resulting Timeline refresh missing');
            return { connection, locationId, sse: true, refresh: true };
        };
        const first = await observe('first', 1);
        assert.equal(statisticsError, false, 'Timeline live statistics rejected a missing username');
        phase = 'replacement-reconnect';
        await exchange('replace');
        await waitFor(() => connections.length >= 2, 'EventSource did not automatically reconnect');
        const second = await observe('second', connections.length);
        assert.equal(page.frames().filter(value => value.url().startsWith(embedUrl)).length, 1);
        await exchange('result', { observation: validateObservation({ embedUrl, fullViewPath: canonical,
            leaflet: true, tile, connections: connections.length, first, second }, origin, username) });
    } finally {
        await browser.close();
        input.close();
    }
};

if (resolve(process.argv[1] ?? '') === fileURLToPath(import.meta.url)) {
    // A hung fixture must fail closed without raw browser logs, cookies or network payloads.
    const deadline = setTimeout(() => process.exit(1), 120000);
    probe().catch(error => {
        process.stdout.write(JSON.stringify({ action: 'failed', phase, statisticsError,
            reason: error.message.split('\n')[0].slice(0, 180) }) + '\n');
        process.exitCode = 1;
    }).finally(() => clearTimeout(deadline));
}
