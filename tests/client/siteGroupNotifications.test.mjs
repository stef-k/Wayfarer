import assert from 'node:assert/strict';
import test from 'node:test';

test('shipped site initialization opens one protected authenticated stream', async () => {
    const domReady = [];
    const urls = [];
    globalThis.window = {
        __currentUserId: 'authenticated-user',
        addEventListener() {},
        wayfarer: {}
    };
    globalThis.wayfarer = globalThis.window.wayfarer;
    globalThis.document = {
        body: {},
        documentElement: { getAttribute: () => 'light', setAttribute() {} },
        addEventListener: (type, callback) => { if (type === 'DOMContentLoaded') domReady.push(callback); },
        getElementById: () => null,
        querySelector: () => null,
        querySelectorAll: () => []
    };
    globalThis.localStorage = { getItem: () => null, setItem() {} };
    globalThis.sessionStorage = { getItem: () => null, setItem() {} };
    globalThis.fetch = async () => ({ ok: false });
    globalThis.setInterval = () => 1;
    globalThis.EventSource = function FakeEventSource(url) {
        urls.push(url);
        return { close() {} };
    };

    await import(`../../wwwroot/js/site.js?site-init=${Date.now()}`);
    assert.ok(domReady.length > 0);
    domReady[0]();

    assert.deepEqual(urls, ['/api/sse/group-notifications']);
});
