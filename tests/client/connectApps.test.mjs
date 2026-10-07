import assert from 'node:assert/strict';
import test from 'node:test';
import { createConnectionPage } from '../../wwwroot/js/Areas/User/ApiToken/Index.js';

const secret = `wf_${'A'.repeat(43)}`;
const issued = { token: secret, tokenId: 12, issuedAt: '2026-10-07T12:00:00.000000Z' };
const tick = async () => { await new Promise(resolve => setImmediate(resolve)); };

/** Supplies only the DOM/browser boundaries used by the production state owner. */
const page = (request = async () => ({ ok: true, json: async () => ({ ...issued }) }), https = true, exists = false, effectiveHttps = https) => {
    const nodes = new Map();
    const element = id => {
        if (!nodes.has(id)) nodes.set(id, {
            textContent: '', hidden: false, disabled: false, value: 'antiforgery', children: [], attributes: {},
            listeners: {},
            replaceChildren: () => { const node = nodes.get(id); node.children = []; node.textContent = ''; },
            removeAttribute: name => { delete nodes.get(id).attributes[name]; },
            addEventListener: (name, callback) => { nodes.get(id).listeners[name] = callback; }
        });
        return nodes.get(id);
    };
    const root = {
        dataset: { effectiveHttps: String(effectiveHttps), tokenId: exists ? '12' : '', issuedAt: issued.issuedAt },
        querySelector: selector => element(selector)
    };
    const events = {};
    let reloads = 0;
    const copied = [];
    const browser = {
        location: { protocol: https ? 'https:' : 'http:', origin: `${https ? 'https' : 'http'}://actual.example:8443`,
            reload: () => reloads++ },
        navigator: { clipboard: { writeText: async value => copied.push(value) } },
        addEventListener: (name, callback) => { events[name] = callback; }
    };
    const graphics = [];
    // Matches the bundled library's retained payload/options/model and title boundaries.
    class QrCode {
        constructor(node, options) {
            this._htOption = options;
            this._oQRCode = { payload: options.text };
            this._oDrawing = {};
            this._el = node;
            node.attributes.title = options.text;
            node.children.push({ graphic: true });
            graphics.push(this);
        }
        clear = () => {};
    }
    const state = createConnectionPage({ root, browser, request, QrCode });
    return { state, nodes, events, graphics, copied, browser, reloads: () => reloads,
        node: id => element(`#${id}`), click: id => element(`#${id}`).listeners.click() };
};

const assertHidden = fixture => {
    assert.equal(fixture.node('connection-token').textContent, '');
    assert.deepEqual(fixture.node('connection-qr').children, []);
    assert.equal(fixture.node('connection-qr').attributes.title, undefined);
    assert.equal(fixture.node('connection-reveal').hidden, true);
};

test('initial load and a fresh reload are secret-free', () => {
    assertHidden(page());
    assertHidden(page(undefined, true, true));
});

test('committed response displays the token and minimal Mobile payload with the actual browser origin', async () => {
    const fixture = page();
    await fixture.state.issue();
    assert.equal(fixture.node('connection-token').textContent, secret);
    assert.equal(fixture.node('connection-reveal').hidden, false);
    assert.deepEqual(JSON.parse(fixture.graphics[0]._htOption.text), {
        serverUrl: 'https://actual.example:8443', apiToken: secret
    });
    assert.equal(fixture.graphics[0]._htOption.useSVG, true);
    assert.equal(fixture.node('connection-qr').attributes.title, undefined);
    fixture.click('copy-token');
    fixture.click('copy-server');
    await tick();
    assert.deepEqual(fixture.copied, [secret, 'https://actual.example:8443']);
});

test('Done clears text, graphic, library references and copy authority without a server request', async () => {
    let requests = 0;
    const fixture = page(async () => { requests++; return { ok: true, json: async () => ({ ...issued }) }; });
    await fixture.state.issue();
    const qr = fixture.graphics[0];
    fixture.click('hide-connection');
    assertHidden(fixture);
    assert.equal(qr._htOption.text, '');
    assert.equal(qr._oQRCode, null);
    assert.equal(qr._oDrawing, null);
    assert.equal(qr._el, null);
    fixture.click('copy-token');
    assert.equal(fixture.copied.length, 0);
    assert.equal(requests, 1);
});

test('replacement requires its explicit confirmation and carries only the observed safe state', async () => {
    const calls = [];
    const fixture = page(async (url, options) => {
        calls.push({ url, options });
        return { ok: true, json: async () => ({ ...issued }) };
    }, true, true);
    fixture.click('issue-connection');
    assert.equal(calls.length, 0);
    fixture.click('confirm-replacement');
    await tick();
    assert.equal(calls[0].url, '/User/ApiToken/Replace');
    assert.deepEqual(JSON.parse(calls[0].options.body), { tokenId: 12, issuedAt: issued.issuedAt, confirmed: true });
    assert.equal(calls[0].options.cache, 'no-store');
    assert.equal(calls[0].options.credentials, 'same-origin');
});

for (const event of ['pagehide', 'pageshow']) {
    test(`${event} clears an active reveal; persisted restoration refreshes the safe page`, async () => {
        const fixture = page();
        await fixture.state.issue();
        fixture.events[event]({ persisted: true });
        assertHidden(fixture);
        if (event === 'pageshow') {
            assert.equal(fixture.reloads(), 1);
            assert.equal(fixture.node('issue-connection').disabled, true);
        }
    });
}

for (const invalidate of ['hide', 'pagehide', 'pageshow']) {
    test(`late response after ${invalidate} cannot recreate a reveal`, async () => {
        let finish;
        const fixture = page((url, options) => options?.method === 'POST'
            ? new Promise(resolve => { finish = resolve; })
            : Promise.resolve({ ok: true, json: async () => ({ tokenId: 12, issuedAt: issued.issuedAt }) }));
        const operation = fixture.state.issue();
        if (invalidate === 'hide') fixture.state.hide();
        else fixture.events[invalidate]({ persisted: true });
        finish({ ok: true, json: async () => ({ ...issued }) });
        await operation;
        await tick();
        assertHidden(fixture);
        assert.equal(fixture.graphics.length, 0);
    });
}

test('Hide while JSON parsing is pending also invalidates a late secret', async () => {
    let finish;
    const fixture = page((url, options) => Promise.resolve({ ok: true,
        json: options?.method === 'POST' ? () => new Promise(resolve => { finish = resolve; }) : async () => null }));
    const operation = fixture.state.issue();
    await tick();
    fixture.state.hide();
    finish({ ...issued });
    await operation;
    assertHidden(fixture);
});

test('normal pageshow invalidates an early pending mutation and refreshes safe status before another action', async () => {
    let finish;
    const fixture = page((url, options) => options?.method === 'POST'
        ? new Promise(resolve => { finish = resolve; })
        : Promise.resolve({ ok: true, json: async () => ({ tokenId: 12, issuedAt: issued.issuedAt }) }));
    const operation = fixture.state.issue();
    fixture.events.pageshow({ persisted: false });
    assert.equal(fixture.node('issue-connection').disabled, true);
    await tick();
    assert.equal(fixture.node('issue-connection').disabled, false);
    assert.equal(fixture.node('issue-connection').textContent, 'Replace connection token');
    finish({ ok: true, json: async () => ({ ...issued }) });
    await operation;
    assertHidden(fixture);
});

test('failed or lost mutation stays secret-free and refreshes metadata without retrying the POST', async () => {
    for (const lost of [false, true]) {
        const methods = [];
        const fixture = page(async (url, options) => {
            methods.push(options?.method || 'GET');
            if (options?.method === 'POST') {
                if (lost) throw new Error('Network loss');
                return { ok: false };
            }
            return { ok: true, json: async () => ({ tokenId: 12, issuedAt: issued.issuedAt }) };
        });
        await fixture.state.issue();
        assertHidden(fixture);
        assert.deepEqual(methods, ['POST', 'GET']);
        assert.equal(fixture.node('issue-connection').textContent, 'Replace connection token');
    }
});

test('duplicate submission is blocked while the first request is active', async () => {
    let finish;
    let requests = 0;
    const fixture = page(() => { requests++; return new Promise(resolve => { finish = resolve; }); });
    const first = fixture.state.issue();
    await fixture.state.issue();
    assert.equal(requests, 1);
    assert.equal(fixture.node('issue-connection').disabled, true);
    finish({ ok: true, json: async () => ({ ...issued }) });
    await first;
});

test('HTTP and an ineffective HTTPS proxy boundary cannot issue or pair', async () => {
    for (const browserHttps of [false, true]) {
        let requests = 0;
        const fixture = page(() => { requests++; }, browserHttps, false, false);
        await fixture.state.issue();
        assertHidden(fixture);
        assert.equal(requests, 0);
        assert.equal(fixture.node('copy-server').disabled, true);
    }
});
