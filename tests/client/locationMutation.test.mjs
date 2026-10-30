import assert from 'node:assert/strict';
import test from 'node:test';
import { locationRequestFingerprint, validateLocationReceipt, saveLocationMutation, retainLocationDraft, locationWork } from '../../wwwroot/js/util/location-mutation.js';
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
