import assert from 'node:assert/strict';
import test from 'node:test';
import { locationRequestFingerprint, validateLocationReceipt, saveLocationMutation, retainLocationDraft, locationWork, prepareLocationDeletion } from '../../wwwroot/js/util/location-mutation.js';
import { locationEditCommand, locationWallTimeUtc } from '../../wwwroot/js/util/location-edit-state.js';

const base = '83e8095d-45b0-4dc7-a267-89a4ee119d67';
const action = '24cddbea-057b-4f78-8ccf-b0aa254aa011';
const body = '{"contract":"location-mutation-1","normalization":"location-values-1","adapter":"owner","baseHandle":"example-handle","kind":"update","expected":{"uuid":{"state":"known","value":null},"notesHtml":{"state":"known","value":null}},"changes":{"notes":{"op":"clear"}}}';
const attempt = { id: 421, baseId: base, actionId: action, subjectId: 'owner-a', uuid: null, adapter: 'owner', kind: 'update', fingerprint: 'sha256:961954f23232166d67d4495d4f7370b4d8be7acb47c8d90bdff401d91a3322ce' };
const receipt = value => ({ contract: 'location-mutation-1', normalization: 'location-values-1', adapter: value.adapter,
    subjectId: value.subjectId, locationId: value.id, uuid: value.uuid, baseId: value.baseId, actionId: value.actionId,
    kind: value.kind, fingerprint: value.fingerprint, state: 'applied', futureEffects: false, changed: true,
    completedAtUtc: '2026-10-30T10:00:00Z', receiptRetainUntilUtc: '2026-11-30T10:00:00Z',
    addressOutcome: 'unchanged', providerOutcome: 'notRequested', detailUrl: '/api/location/history/421' });

test('Web independently computes all approved exact-byte vectors before the first send', async () => {
    for (const [bytes, digest] of [[body, attempt.fingerprint],
        [body.replace('{"op":"clear"}', '{"op":"set","html":"<p>Αθήνα</p>"}'), 'sha256:b7c9900c92a43eeed2934fa334a39ce7eb1b7fc41f94bed3b4d4e1b13e2ebdbd'],
        [body + '\n', 'sha256:3dad01e294e9c69bac41ba4afa3e6730a0a03ae1e2f47ac7ff4befc60ffea9d5']]) {
        assert.equal(await locationRequestFingerprint('owner-a', 421, base, action, 'owner', bytes), digest);
    }
});

test('first recovered receipt validates the full immutable binding and refuses extra/missing result members', () => {
    assert.equal(validateLocationReceipt(receipt(attempt), attempt), true);
    for (const field of ['fingerprint', 'subjectId', 'uuid', 'baseId', 'actionId', 'normalization', 'kind', 'adapter', 'locationId']) {
        assert.equal(validateLocationReceipt({ ...receipt(attempt), [field]: 'wrong' }, attempt), false, field);
        const missing = receipt(attempt); delete missing[field];
        assert.equal(validateLocationReceipt(missing, attempt), false, field);
    }
    assert.equal(validateLocationReceipt({ ...receipt(attempt), extra: true }, attempt), false);
    assert.equal(validateLocationReceipt({ ...receipt(attempt), futureEffects: true }, attempt), false);
});

test('coordinate movement omits unchanged time/manual prefill and keeps explicit value-equal manual Clear', () => {
    const initial = { Latitude: '37.98', Longitude: '23.72', LocalTimestamp: '2026-10-29T10:15:00.123456', Notes: '<p>n</p>', SelectedActivityId: '7', Altitude: '', Country: '' };
    const command = locationEditCommand(initial, { ...initial, Longitude: '0' }, ['Country'], false, 'Europe/Athens', '');
    assert.deepEqual(command.changes, { position: { op: 'set', latitude: 37.98, longitude: 0 }, manualAddress: { op: 'set', fields: { country: null } } });
    assert.deepEqual(command.groups.sort(), ['uuid', 'position', 'address'].sort());
    assert.equal(locationWallTimeUtc('2026-10-29T10:15:00.123456', 'Europe/Athens'), '2026-10-29T08:15:00.123456Z');
    assert.throws(() => locationWallTimeUtc('2026-03-29T03:30', 'Europe/Athens'), /does not exist/);
    assert.throws(() => locationWallTimeUtc('2026-10-25T03:30', 'Europe/Athens'), /occurs twice/);
    assert.equal(locationWallTimeUtc('2026-10-25T03:30', 'Europe/Athens', 'earlier'), '2026-10-25T00:30:00Z');
    assert.equal(locationWallTimeUtc('2026-10-25T03:30', 'Europe/Athens', 'later'), '2026-10-25T01:30:00Z');
});

test('actual lost-response client path recovers same identity and preserves newer typing during the request', async () => {
    const storage = new Map();
    globalThis.localStorage = { getItem: key => storage.get(key), setItem: (key, value) => storage.set(key, value) };
    globalThis.sessionStorage = { getItem: key => storage.get(`session:${key}`), setItem: (key, value) => storage.set(`session:${key}`, value) };
    globalThis.location = { origin: 'https://wayfarer.test' };
    globalThis.document = { getElementById: () => ({ dataset: { subjectId: 'owner-a' } }), querySelector: () => ({ value: 'antiforgery' }) };
    Object.defineProperty(globalThis, 'navigator', { configurable: true, value: { locks: { request: async (_key, run) => run() } } });
    const known = value => ({ state: 'known', value });
    const detail = { subjectId: 'owner-a', record: { locationId: 421 }, locationMutation: { contract: 'location-mutation-1', normalization: 'location-values-1',
        baseId: base, baseHandle: 'real-handle-fixture', expiresAtUtc: '2099-01-01T00:00:00Z', originalCreateAddressPublication: 'closed',
        baseValues: { uuid: known(null), notesHtml: known(null) } } };
    const calls = [];
    globalThis.fetch = async (url, options) => {
        calls.push([url, options]);
        if (options.method === 'POST') {
            const state = locationWork(421); const active = state.attempts.find(item => item.actionId === state.active);
            assert.equal(active.mayHaveAttempted, true);
            assert.equal(active.body, options.body);
            retainLocationDraft(421, { current: { Notes: 'newer typing' } });
            throw new Error('Lost reply after server commit');
        }
        if (url.includes('/mutations/')) {
            const state = locationWork(421); const active = state.attempts.find(item => item.actionId === state.active);
            return { json: async () => receipt(active) };
        }
        return { ok: true, json: async () => detail };
    };
    const result = await saveLocationMutation(421, detail, 'update', { notes: { op: 'clear' } }, { uuid: known(null), notesHtml: known(null) });
    assert.equal(result.success, false); assert.equal(result.applied, true);
    assert.match(result.message, /Newer typing/);
    assert.equal(calls.filter(([, options]) => options.method === 'POST').length, 1);
    assert.equal(calls[0][0], calls[1][0]);
    assert.equal(locationWork(421).draft.value.current.Notes, 'newer typing');
    assert.equal(locationWork(421).attempts[0].receipt.state, 'applied');
});

test('user retry preserves exact identity and bytes, and a settled double click does not send a second command', async () => {
    const storage = new Map();
    globalThis.localStorage = { getItem: key => storage.get(key), setItem: (key, value) => storage.set(key, value) };
    globalThis.sessionStorage = { getItem: key => storage.get(`s:${key}`), setItem: (key, value) => storage.set(`s:${key}`, value) };
    globalThis.location = { origin: 'https://wayfarer.test' };
    globalThis.document = { getElementById: () => ({ dataset: { subjectId: 'owner-a' } }), querySelector: () => ({ value: 'csrf' }) };
    Object.defineProperty(globalThis, 'navigator', { configurable: true, value: { locks: { request: async (_key, run) => run() } } });
    const known = value => ({ state: 'known', value });
    const detail = { subjectId: 'owner-a', record: { locationId: 421 }, locationMutation: { contract: 'location-mutation-1', normalization: 'location-values-1',
        baseId: base, baseHandle: 'H', expiresAtUtc: '2099-01-01T00:00:00Z', originalCreateAddressPublication: 'closed', baseValues: { uuid: known(null), notesHtml: known(null) } } };
    const posts = [];
    globalThis.fetch = async (url, options) => {
        const state = locationWork(421); const active = state.attempts.find(item => item.actionId === state.active) || state.attempts[0];
        if (options.method === 'POST') {
            posts.push({ url, body: options.body });
            if (posts.length === 1) throw new Error('Connection lost before arrival');
            return { json: async () => receipt(active) };
        }
        if (url.includes('/mutations/')) return { json: async () => ({ contract: 'location-mutation-1', subjectId: 'owner-a', locationId: 421,
            baseId: active.baseId, actionId: active.actionId, observedAtUtc: '2026-10-30T10:00:00Z', state: 'unknown', code: 'operationNotObserved', futureEffects: true, expiresAtUtc: active.expiresAtUtc }) };
        return { ok: true, json: async () => detail };
    };
    const args = [421, detail, 'update', { notes: { op: 'clear' } }, { uuid: known(null), notesHtml: known(null) }];
    assert.equal((await saveLocationMutation(...args)).success, false);
    assert.equal((await saveLocationMutation(...args)).success, true);
    assert.deepEqual(posts[0], posts[1]); assert.equal(locationWork(421).attempts.length, 1);
    assert.equal((await saveLocationMutation(...args)).success, true); assert.equal(posts.length, 2);
});

test('receipt loss re-admits unchanged originals with fresh authority, or labels simple current-state satisfaction as historical Unknown', async () => {
    for (const alreadySatisfied of [false, true]) {
        const storage = new Map();
        globalThis.localStorage = { getItem: key => storage.get(key), setItem: (key, value) => storage.set(key, value) };
        globalThis.sessionStorage = { getItem: key => storage.get(`s:${key}`), setItem: (key, value) => storage.set(`s:${key}`, value) };
        globalThis.location = { origin: 'https://wayfarer.test' };
        globalThis.document = { getElementById: () => ({ dataset: { subjectId: 'owner-a' } }), querySelector: () => ({ value: 'csrf' }) };
        Object.defineProperty(globalThis, 'navigator', { configurable: true, value: { locks: { request: async (_key, run) => run() } } });
        const known = value => ({ state: 'known', value });
        const detail = { subjectId: 'owner-a', record: { locationId: 421 }, locationMutation: { contract: 'location-mutation-1', normalization: 'location-values-1',
            baseId: base, baseHandle: 'old-H', expiresAtUtc: '2099-01-01T00:00:00Z', originalCreateAddressPublication: 'closed', baseValues: { uuid: known(null), notesHtml: known(null) } } };
        const fresh = structuredClone(detail); fresh.observedAtUtc = '2100-01-01T00:00:00Z';
        fresh.locationMutation.baseId = crypto.randomUUID(); fresh.locationMutation.baseHandle = 'new-H'; fresh.locationMutation.expiresAtUtc = '2101-01-01T00:00:00Z';
        if (alreadySatisfied) fresh.locationMutation.baseValues.notesHtml = known('desired');
        const posts = [];
        globalThis.fetch = async (url, options) => {
            const state = locationWork(421); const active = state.attempts.find(item => item.actionId === state.active);
            if (options.method === 'POST') {
                posts.push(JSON.parse(options.body));
                if (posts.length === 1) throw new Error('No retained response');
                return { json: async () => receipt(active) };
            }
            if (url.includes('/mutations/')) return { json: async () => ({ contract: 'location-mutation-1', subjectId: 'owner-a', locationId: 421,
                baseId: active.baseId, actionId: active.actionId, observedAtUtc: '2100-01-01T00:00:00Z', state: 'unknown', code: 'operationHistoryExpired', futureEffects: false,
                expiresAtUtc: active.expiresAtUtc, historicalOutcome: 'unknown' }) };
            return { ok: true, json: async () => fresh };
        };
        const expected = { uuid: known(null), notesHtml: known(null) };
        const args = [421, detail, 'update', { notes: { op: 'set', html: 'desired' } }, expected];
        assert.equal((await saveLocationMutation(...args)).success, false);
        const result = await saveLocationMutation(...args); assert.equal(result.success, true);
        if (alreadySatisfied) {
            assert.equal(result.currentStateSatisfied, true); assert.equal(posts.length, 1);
            assert.equal(locationWork(421).currentObservation.historicalOutcome, 'unknown');
            assert.equal(locationWork(421).attempts[0].receipt, undefined);
        } else {
            assert.equal(posts.length, 2); assert.deepEqual(posts[1].expected, expected); assert.equal(posts[1].baseHandle, 'new-H');
            assert.equal(locationWork(421).attempts.length, 2);
        }
        assert.equal(locationWork(421).attempts[0].exclusion.historicalOutcome, 'unknown');
    }
});

test('corrupt immutable bytes or a missing active binding prevent any new handoff', async () => {
    for (const missingActive of [false, true]) {
        const storage = new Map();
        globalThis.localStorage = { getItem: key => storage.get(key), setItem: (key, value) => storage.set(key, value) };
        globalThis.sessionStorage = { getItem: key => storage.get(`s:${key}`), setItem: (key, value) => storage.set(`s:${key}`, value) };
        globalThis.location = { origin: 'https://wayfarer.test' };
        globalThis.document = { getElementById: () => ({ dataset: { subjectId: 'owner-a' } }), querySelector: () => ({ value: 'csrf' }) };
        Object.defineProperty(globalThis, 'navigator', { configurable: true, value: { locks: { request: async (_key, run) => run() } } });
        const known = value => ({ state: 'known', value });
        const detail = { subjectId: 'owner-a', record: { locationId: 421 }, locationMutation: { contract: 'location-mutation-1', normalization: 'location-values-1',
            baseId: base, baseHandle: 'H', expiresAtUtc: '2099-01-01T00:00:00Z', originalCreateAddressPublication: 'closed', baseValues: { uuid: known(null), notesHtml: known(null) } } };
        let calls = 0;
        globalThis.fetch = async () => { calls++; throw new Error('offline'); };
        const args = [421, detail, 'update', { notes: { op: 'clear' } }, { uuid: known(null), notesHtml: known(null) }];
        await saveLocationMutation(...args);
        const key = [...storage.keys()].find(key => key.includes('https://wayfarer.test'));
        const saved = JSON.parse(storage.get(key));
        if (missingActive) saved.active = crypto.randomUUID(); else saved.attempts[0].body += '\n';
        storage.set(key, JSON.stringify(saved));
        const before = calls;
        await assert.rejects(saveLocationMutation(...args), /Saved command evidence/);
        assert.equal(calls, before);
    }
});

test('per-record deletion requires informed fresh consent after rejection and keeps pending consent immutable', async () => {
    for (const pending of [false, true]) {
        const storage = new Map();
        globalThis.localStorage = { getItem: key => storage.get(key), setItem: (key, value) => storage.set(key, value) };
        globalThis.sessionStorage = { getItem: key => storage.get(`s:${key}`), setItem: (key, value) => storage.set(`s:${key}`, value) };
        globalThis.location = { origin: 'https://wayfarer.test' };
        globalThis.document = { getElementById: () => ({ dataset: { subjectId: 'owner-a' } }), querySelector: () => ({ value: 'csrf' }) };
        Object.defineProperty(globalThis, 'navigator', { configurable: true, value: { locks: { request: async (_key, run) => run() } } });
        const known = value => ({ state: 'known', value });
        const detail = { subjectId: 'owner-a', record: { locationId: 421, notes: 'Original content' }, unavailableFields: [],
            locationMutation: { contract: 'location-mutation-1', normalization: 'location-values-1', baseId: base,
                baseHandle: 'H', expiresAtUtc: '2099-01-01T00:00:00Z', originalCreateAddressPublication: 'closed',
                baseValues: { uuid: known(null), deleteContent: known({ format: 'location-delete-storage-1', fingerprint: 'old-digest' }) } } };
        const fresh = structuredClone(detail);
        fresh.record.notes = 'New content requiring consent'; fresh.locationMutation.baseId = crypto.randomUUID();
        fresh.locationMutation.baseHandle = 'fresh-H'; fresh.locationMutation.baseValues.deleteContent.value.fingerprint = 'new-digest';
        const posts = []; let reads = 0;
        globalThis.fetch = async (url, options) => {
            if (!url.includes('/mutations/')) return { ok: true, json: async () => ++reads === 1 ? detail : fresh };
            const state = locationWork(421); const active = state.attempts.find(item => item.actionId === state.active);
            const envelope = receipt(active);
            delete envelope.addressOutcome; delete envelope.providerOutcome;
            if (options.method === 'POST') posts.push({ url, body: JSON.parse(options.body) });
            if (pending) {
                delete envelope.completedAtUtc; delete envelope.changed; delete envelope.detailUrl;
                return { json: async () => ({ ...envelope, state: 'pending', futureEffects: true,
                    observedAtUtc: '2026-10-30T10:00:00Z', executeBeforeUtc: '2026-10-30T10:02:00Z', retryAfterSeconds: 2 }) };
            }
            if (posts.length === 1) {
                delete envelope.changed; delete envelope.detailUrl;
                return { json: async () => ({ ...envelope, state: 'rejected', code: 'locationBaseStale', staleGroups: ['content'] }) };
            }
            return { json: async () => ({ ...envelope, detailUrl: null }) };
        };
        const first = await prepareLocationDeletion(421);
        assert.match(first.message, /Original content/);
        assert.equal((await first()).success, false);
        const oldAction = locationWork(421).active;
        const next = await prepareLocationDeletion(421);
        assert.equal(locationWork(421).active, oldAction, 'preview/cancel cannot discard the predecessor');
        assert.equal(posts.length, 1);
        if (pending) {
            assert.match(next.message, /Original content/); assert.doesNotMatch(next.message, /New content/);
            assert.equal((await next()).success, false); assert.equal(posts.length, 1); assert.equal(reads, 1);
        } else {
            assert.match(next.message, /new conditional deletion/); assert.match(next.message, /Original content/);
            assert.match(next.message, /New content requiring consent/);
            assert.equal((await next()).success, true); assert.equal(posts.length, 2);
            assert.notEqual(posts[0].url, posts[1].url);
            assert.equal(posts[1].body.expected.content.value.fingerprint, 'new-digest');
            assert.equal(locationWork(421).attempts[0].receipt.code, 'locationBaseStale');
        }
    }
});
