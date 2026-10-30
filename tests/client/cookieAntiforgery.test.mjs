import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import vm from 'node:vm';
import ts from 'typescript';
import { saveLocationMutation, locationWork } from '../../wwwroot/js/util/location-mutation.js';

const pageToken = 'server-issued-page-token';
const tokenInput = { value: pageToken, cloneNode: () => ({ name: '__RequestVerificationToken', value: pageToken }) };
const documentToken = { querySelector: selector => {
    assert.equal(selector, 'input[name="__RequestVerificationToken"]');
    return tokenInput;
} };
const source = path => readFile(`wwwroot/js/${path}`, 'utf8');

// Execute the actual request expressions at the fetch seam, without map/editor bootstrapping.
const capturedRequests = async (path, marker, variables = {}) => {
    const text = await source(path);
    const calls = [];
    const script = ts.createSourceFile(path, text, ts.ScriptTarget.Latest, true, ts.ScriptKind.JS);
    const visit = node => {
        if (ts.isCallExpression(node) && node.expression.getText(script) === 'fetch'
            && node.arguments[0]?.getText(script).includes(marker)) {
            vm.runInNewContext(node.getText(script), {
                document: documentToken, JSON,
                fetch: (url, options) => calls.push({ url, options }), ...variables
            });
        }
        ts.forEachChild(node, visit);
    };
    visit(script);
    assert.ok(calls.length > 0, `${path}: request found`);
    for (const { options } of calls) assert.equal(options.headers.RequestVerificationToken, pageToken, path);
    return calls;
};

test('protected Activity command sends the authentic page token with its immutable selected originals', async () => {
    const previous = { document: globalThis.document, fetch: globalThis.fetch, localStorage: globalThis.localStorage,
        sessionStorage: globalThis.sessionStorage, location: globalThis.location };
    const navigation = Object.getOwnPropertyDescriptor(globalThis, 'navigator');
    const local = new Map(); const session = new Map();
    globalThis.localStorage = { getItem: key => local.get(key), setItem: (key, value) => local.set(key, value) };
    globalThis.sessionStorage = { getItem: key => session.get(key), setItem: (key, value) => session.set(key, value) };
    globalThis.location = { origin: 'https://wayfarer.test' };
    globalThis.document = { ...documentToken, getElementById: () => ({ dataset: { subjectId: 'owner' } }) };
    Object.defineProperty(globalThis, 'navigator', { configurable: true, value: { locks: { request: (_name, run) => run() } } });
    const detail = { subjectId: 'owner', record: { locationId: 12 }, locationMutation: { contract: 'location-mutation-1', normalization: 'location-values-1',
        baseId: '83e8095d-45b0-4dc7-a267-89a4ee119d67', baseHandle: 'H', expiresAtUtc: '2099-01-01T00:00:00Z', originalCreateAddressPublication: 'closed',
        baseValues: { uuid: { state: 'known', value: null }, activityTypeId: { state: 'known', value: null } } } };
    let commands = 0;
    globalThis.fetch = async (url, options) => {
        if (options.method !== 'POST') return { ok: true, text: async () => JSON.stringify(detail) };
        commands++;
        assert.match(url, /^\/api\/location\/12\/mutations\//);
        assert.equal(options.headers.RequestVerificationToken, pageToken);
        const body = JSON.parse(options.body);
        assert.deepEqual(body.changes, { activity: { op: 'set', id: 7 } });
        assert.deepEqual(body.expected, detail.locationMutation.baseValues);
        const attempt = locationWork(12).attempts[0];
        assert.equal(attempt.mayHaveAttempted, true); assert.equal(attempt.body, options.body);
        return { text: async () => JSON.stringify(({ contract: 'location-mutation-1', normalization: 'location-values-1', adapter: 'web-edit',
            subjectId: 'owner', locationId: 12, uuid: null, baseId: attempt.baseId, actionId: attempt.actionId, kind: 'update', fingerprint: attempt.fingerprint,
            receiptRetainUntilUtc: '2099-02-01T00:00:00Z', state: 'applied', changed: true, completedAtUtc: '2026-10-30T00:00:00Z', futureEffects: false,
            addressOutcome: 'unchanged', providerOutcome: 'notRequested', detailUrl: '/api/location/history/12' })) };
    };
    try {
        assert.equal((await saveLocationMutation(12, detail, 'update', { activity: { op: 'set', id: 7 } }, detail.locationMutation.baseValues)).success, true);
        assert.equal(commands, 1);
        assert.match(await source('util/activity-editor.js'), /saveLocationMutation\(locationId/);
        assert.doesNotMatch(await source('util/activity-editor.js'), /method:\s*['"]PUT['"]/);
    } finally { Object.assign(globalThis, previous); if (navigation) Object.defineProperty(globalThis, 'navigator', navigation); }
});

test('JSON location and visit deletions send tokens on every modal and bulk path', async () => {
    for (const path of ['Areas/User/Location/Index.js', 'Areas/User/Location/AllLocations.js',
        'Areas/User/Visit/Index.js']) {
        const calls = await capturedRequests(path, '/bulk-delete', {
            locationId: 1, selectedIds: [1, 2], id: 'visit-id'
        });
        for (const { options } of calls) {
            assert.equal(options.method, 'POST');
            assert.equal(options.headers['Content-Type'], 'application/json');
            const body = JSON.parse(options.body);
            assert.ok(body);
            if (!path.includes('/Visit/')) assert.equal(body.operationScope, 'bulk');
        }
    }
});

test('every per-row Location deletion uses the protected owner instead of a bare-ID bulk bypass', async () => {
    for (const path of ['Areas/User/Location/Index.js', 'Areas/User/Location/AllLocations.js',
        'Areas/User/Timeline/Index.js', 'Areas/User/Timeline/Chronological.js']) {
        const text = await source(path);
        assert.match(text, /prepareLocationDeletion\(/, path);
        assert.match(text, /deleteProtected\(\)/, path);
        assert.doesNotMatch(text, /locationIds:\s*\[/, path);
    }
});

test('Backfill apply and bodyless clear preserve their method and payload contracts', async () => {
    const [apply] = await capturedRequests('Areas/User/Trip/Index.js', '/api/backfill/apply/', {
        currentTripId: 'trip', createVisits: [1], confirmedSuggestions: [], deleteVisitIds: []
    });
    assert.equal(apply.options.method, 'POST');
    assert.deepEqual(JSON.parse(apply.options.body), { createVisits: [1], confirmedSuggestions: [], deleteVisitIds: [] });
    const [clear] = await capturedRequests('Areas/User/Trip/Index.js', '/api/backfill/clear/', { tripId: 'trip' });
    assert.equal(clear.url, '/api/backfill/clear/trip');
    assert.equal(clear.options.method, 'DELETE');
    assert.equal(clear.options.body, undefined);
});

test('delete-all locations sends its token without adding a body', async () => {
    const [request] = await capturedRequests('Areas/User/Settings/Index.js', '/api/users/', { btn: { dataset: { userId: 'owner' } } });
    assert.equal(request.url, '/api/users/owner/locations');
    assert.equal(request.options.method, 'DELETE');
    assert.equal(request.options.body, undefined);
});

test('group Leave event sends the page token on its bodyless POST', async () => {
    let click;
    let request;
    const tbody = { children: [{}], addEventListener: (_, callback) => { click = callback; }, querySelector: () => null };
    vm.runInNewContext(await source('Areas/User/Groups/Index.js'), {
        document: { ...documentToken, getElementById: () => tbody },
        fetch: async (url, options) => { if (options) request = { url, options }; return { ok: true, json: async () => [] }; },
        wayfarer: { showConfirmationModal: options => options.onConfirm() }
    });
    click({ target: { closest: () => ({ getAttribute: () => 'group-id' }) } });
    assert.equal(request.url, '/api/groups/group-id/leave');
    assert.equal(request.options.headers.RequestVerificationToken, pageToken);
    assert.equal(request.options.body, undefined);
});

test('Invitation Accept and Decline reuse the rendered token on bodyless POSTs', async () => {
    const requests = [];
    const script = await source('Areas/User/Invitations/Index.js');
    await vm.runInNewContext(`${script}\nPromise.all([accept('invite'), decline('invite')]);`, {
        document: { ...documentToken, getElementById: () => null, addEventListener: () => {} },
        window: { __userInvitationsConfig: { acceptUrl: id => `/api/invitations/${id}/accept`, declineUrl: id => `/api/invitations/${id}/decline` } },
        wayfarer: {},
        fetch: async (url, options) => { requests.push({ url, options }); return { ok: true, text: async () => '{}' }; }
    });
    assert.equal(requests.length, 2);
    for (const { options } of requests) {
        assert.equal(options.headers.RequestVerificationToken, pageToken);
        assert.equal(options.method, 'POST');
        assert.equal(options.body, undefined);
    }
});

test('Jobs SSE rebuilding copies the authentic token into every replacement form', async () => {
    const script = await source('Areas/Admin/Jobs/Index.js');
    for (const [status, count] of [['Running', 1], ['Paused', 1], ['Completed', 2]]) {
        const forms = [];
        const cell = {
            set innerHTML(html) {
                for (const match of html.matchAll(/<form\b[^>]*>/g)) forms.push({ html: match[0], inputs: [], append(input) { this.inputs.push(input); } });
            },
            querySelectorAll: selector => { assert.equal(selector, 'form'); return forms; }
        };
        vm.runInNewContext(`${script}\nupdateActionButtons(row, status, 'job', 'group');`, {
            row: { querySelector: () => cell }, status,
            document: { ...documentToken, addEventListener: () => {}, createElement: () => ({ textContent: '', get innerHTML() { return this.textContent; } }) }
        });
        assert.equal(forms.length, count);
        for (const form of forms) {
            assert.match(form.html, /method="post"/);
            assert.equal(form.inputs[0].name, '__RequestVerificationToken');
            assert.equal(form.inputs[0].value, pageToken);
        }
    }
});
