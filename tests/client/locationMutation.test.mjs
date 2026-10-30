import assert from 'node:assert/strict';
import test from 'node:test';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';
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


/** Mount the production form with the suite's narrow DOM, storage and fetch doubles. */
const mountedForm = async (zone = 'Europe/Athens') => {
    const { mountProtectedLocationForm } = await import('../../wwwroot/js/util/location-edit-form.js');
    const storage = new Map();
    globalThis.localStorage = { getItem: key => storage.get(key), setItem: (key, value) => storage.set(key, value) };
    globalThis.sessionStorage = { getItem: key => storage.get(`s:${key}`), setItem: (key, value) => storage.set(`s:${key}`, value) };
    let reloads = 0;
    globalThis.location = { origin: 'https://wayfarer.test', reload: () => { reloads++; } };
    Object.defineProperty(globalThis, 'navigator', { configurable: true, value: { locks: { request: async (_key, run) => run() } } });
    const known = value => ({ state: 'known', value });
    const detail = { subjectId: 'owner-a', record: { locationId: 421, activityTypeId: 7 }, locationMutation: {
        contract: 'location-mutation-1', normalization: 'location-values-1', baseId: base, baseHandle: 'H',
        expiresAtUtc: '2099-01-01T00:00:00Z', originalCreateAddressPublication: 'closed',
        baseValues: { uuid: known(null), notesHtml: known('original'), eventUtc: known('2026-10-29T08:00:00Z'), activityTypeId: known(7) } } };
    const names = ['Latitude', 'Longitude', 'Altitude', 'SelectedActivityId', 'LocalTimestamp', 'Address', 'FullAddress', 'AddressNumber', 'StreetName', 'PostCode', 'Place', 'Region', 'Country'];
    const fields = Object.fromEntries(names.map(name => [name, { value: '' }]));
    Object.assign(fields.Latitude, { value: '37.98' }); Object.assign(fields.Longitude, { value: '23.72' });
    fields.LocalTimestamp.value = '2026-10-29T10:00:00'; fields.SelectedActivityId.value = '7';
    const status = { textContent: '' }, listeners = {}, quillListeners = {}, clicks = {}, button = { disabled: false };
    globalThis.confirm = () => true;
    const elements = { 'browser-antiforgery': { dataset: { subjectId: 'owner-a' } }, 'location-save-status': status,
        timeAmbiguity: { value: '' }, 'location-input-time-zone': { textContent: zone }, 'location-adopt-current': { addEventListener: () => {} },
        'location-time-recovery': { hidden: true }, 'location-recovery-zone': { value: '' },
        'location-confirm-time-zone': { addEventListener: (_type, callback) => { clicks.confirmZone = callback; } } };
    globalThis.document = { getElementById: id => elements[id], querySelector: () => ({ value: 'csrf' }),
        createElement: () => ({ innerHTML: '', content: { querySelectorAll: () => [] } }) };
    const form = { dataset: { locationId: '421', locationDetail: JSON.stringify(detail), timeZone: zone },
        elements: { namedItem: name => fields[name] }, querySelectorAll: () => [button],
        addEventListener: (type, callback) => { (listeners[type] ||= []).push(callback); } };
    const quill = { root: { innerHTML: 'original' }, on: (type, callback) => { quillListeners[type] = callback; } };
    let clear = false;
    const mount = () => {
        // Reload replaces DOM listeners but preserves local/session storage.
        for (const key of Object.keys(listeners)) delete listeners[key];
        for (const key of Object.keys(quillListeners)) delete quillListeners[key];
        clear = false; mountProtectedLocationForm(form, quill, () => clear);
    };
    return { detail, form, fields, status, quill, elements, listeners, quillListeners, mount, storage, reloads: () => reloads,
        confirmZone: () => clicks.confirmZone(),
        input: () => listeners.input.at(-1)({ target: { name: 'LocalTimestamp' } }),
        submit: () => listeners.submit.at(-1)({ preventDefault: () => {} }),
        type: html => { quill.root.innerHTML = html; quillListeners['text-change']({}, {}, 'user'); },
        clear: () => { clear = true; quill.root.innerHTML = '<p><br></p>'; quillListeners['text-change']({}, {}, 'api'); } };
};

test('missing-zone time recovery explicitly confirms and retains the intended instant before saving after reload', async () => {
    const f = await mountedForm(); f.mount();
    f.fields.LocalTimestamp.value = '2026-10-25T03:30:00.123456'; f.input(); f.type('retained Notes');
    const missing = structuredClone(locationWork(421).draft.value); delete missing.inputTimeZone;
    retainLocationDraft(421, missing);
    const before = locationWork(421).draft;
    f.form.dataset.timeZone = 'America/New_York'; f.mount();
    let calls = 0;
    globalThis.fetch = async () => { calls++; throw new Error('No dispatch before Save'); };
    await f.submit();
    assert.equal(calls, 0); assert.match(f.status.textContent, /zone/i);
    assert.equal(f.elements['location-time-recovery'].hidden, false);
    assert.equal(f.elements['location-recovery-zone'].value, '', 'a refreshed zone must never be preselected');
    f.elements['location-recovery-zone'].value = 'Europe/Athens';
    f.confirmZone();
    assert.match(f.status.textContent, /occurs twice/);
    assert.deepEqual(locationWork(421).draft, before, 'ambiguous time requires an explicit occurrence');
    f.elements.timeAmbiguity.value = 'later'; f.input();
    const reviewed = locationWork(421).draft;
    let confirmation;
    globalThis.confirm = message => { confirmation = message; return true; };
    f.confirmZone();
    const recovered = locationWork(421).draft;
    assert.notEqual(recovered.generation, reviewed.generation);
    assert.deepEqual(recovered.value, { ...reviewed.value, inputTimeZone: 'Europe/Athens' });
    assert.match(confirmation, /2026-10-25T03:30:00.123456/); assert.match(confirmation, /Europe\/Athens/);
    assert.match(confirmation, /later/); assert.match(confirmation, /2026-10-25T01:30:00.123456Z/);
    assert.equal(calls, 0); assert.equal(locationWork(421).attempts.length, 0);
    // A full remount uses durable interpretation, even when the server renders a different zone.
    f.elements.timeAmbiguity.value = ''; f.mount();
    assert.equal(f.elements['location-time-recovery'].hidden, true);
    assert.equal(f.elements['location-input-time-zone'].textContent, 'Europe/Athens');
    assert.equal(f.elements.timeAmbiguity.value, 'later');
    let posted;
    globalThis.fetch = async (_url, options) => {
        if (options.method !== 'POST') return { ok: true, json: async () => f.detail };
        posted = JSON.parse(options.body); return { json: async () => receipt(locationWork(421).attempts[0]) };
    };
    await f.submit();
    assert.equal(posted.changes.eventUtc.value, '2026-10-25T01:30:00.123456Z');
    assert.equal(posted.changes.notes.html, 'retained Notes');
    assert.deepEqual(posted.expected.eventUtc, before.value.detail.locationMutation.baseValues.eventUtc);
    assert.equal(f.reloads(), 1);
});

test('cancelling missing-zone confirmation leaves the entire retained draft intact without dispatch', async () => {
    const f = await mountedForm(); f.mount();
    f.fields.LocalTimestamp.value = '2026-10-29T11:00:00'; f.input(); f.type('keep my Notes');
    const missing = structuredClone(locationWork(421).draft.value); delete missing.inputTimeZone;
    // Retain the same wrapper shape as an unresolved save, so recovery-field events cannot unwrap it.
    retainLocationDraft(421, { detail: f.detail, expected: f.detail.locationMutation.baseValues, draft: missing }); f.mount();
    const before = locationWork(421);
    let calls = 0; globalThis.fetch = async () => { calls++; throw new Error('No dispatch'); };
    f.elements['location-recovery-zone'].value = 'Europe/Athens';
    const event = { target: f.elements['location-recovery-zone'] };
    f.listeners.input.at(-1)(event); f.listeners.change.at(-1)(event);
    globalThis.confirm = () => false; f.confirmZone();
    assert.deepEqual(locationWork(421), before);
    await f.submit(); assert.equal(calls, 0); assert.deepEqual(locationWork(421), before);
    f.mount();
    assert.equal(f.fields.LocalTimestamp.value, '2026-10-29T11:00:00');
    assert.equal(f.quill.root.innerHTML, 'keep my Notes');
    assert.equal(f.elements['location-time-recovery'].hidden, false);
});

test('missing-zone recovery preserves pending attempts and original comparison evidence without bypassing recovery', async () => {
    const f = await mountedForm(); f.mount();
    f.fields.LocalTimestamp.value = '2026-10-29T11:00:00'; f.input(); f.type('pending Notes');
    let posts = 0, inspections = 0;
    globalThis.fetch = async (_url, options) => {
        if (options.method === 'POST') posts++; else inspections++;
        return { json: async () => pendingReceipt(locationWork(421).attempts[0]) };
    };
    await f.submit();
    const missing = structuredClone(locationWork(421).draft.value); delete missing.draft.inputTimeZone;
    retainLocationDraft(421, missing);
    const before = locationWork(421);
    const remote = structuredClone(f.detail); remote.locationMutation.baseValues.eventUtc.value = '2026-10-29T15:00:00Z';
    f.form.dataset.locationDetail = JSON.stringify(remote); f.form.dataset.timeZone = 'America/New_York'; f.mount();
    f.elements['location-recovery-zone'].value = 'Europe/Athens'; f.confirmZone();
    assert.notEqual(locationWork(421).draft.generation, before.draft.generation);
    assert.deepEqual(locationWork(421).attempts, before.attempts);
    assert.deepEqual(locationWork(421).draft.value.initial, before.draft.value.draft.initial);
    assert.deepEqual(locationWork(421).draft.value.detail, before.draft.value.draft.detail);
    assert.equal(posts, 1); assert.equal(inspections, 0);
    f.mount(); await f.submit();
    assert.equal(posts, 1); assert.equal(inspections, 1, 'Save must inspect the pending predecessor');
    assert.match(f.status.textContent, /pending/); assert.equal(f.reloads(), 0);
    assert.deepEqual(locationWork(421).attempts, before.attempts);
    assert.equal(locationWork(421).active, before.active);
    assert.deepEqual(locationWork(421).draft.value.expected, before.draft.value.expected);
});

test('missing-zone drafts with untouched time still save unrelated Notes without confirming a zone', async () => {
    const f = await mountedForm(); f.mount(); f.type('only Notes');
    const missing = structuredClone(locationWork(421).draft.value); delete missing.inputTimeZone;
    retainLocationDraft(421, missing); f.mount();
    let posted;
    globalThis.fetch = async (_url, options) => {
        if (options.method !== 'POST') return { ok: true, json: async () => f.detail };
        posted = JSON.parse(options.body); return { json: async () => receipt(locationWork(421).attempts[0]) };
    };
    await f.submit();
    assert.deepEqual(posted.changes, { notes: { op: 'set', html: 'only Notes' } });
    assert.equal(posted.expected.eventUtc, undefined); assert.equal(f.reloads(), 1);
});

test('missing-zone confirmation cannot unblock a time save when durable persistence fails', async () => {
    const f = await mountedForm(); f.mount();
    f.fields.LocalTimestamp.value = '2026-10-29T11:00:00'; f.input();
    const missing = structuredClone(locationWork(421).draft.value); delete missing.inputTimeZone;
    retainLocationDraft(421, missing); f.mount();
    const before = locationWork(421);
    globalThis.localStorage.setItem = () => { throw new Error('storage unavailable'); };
    let calls = 0; globalThis.fetch = async () => { calls++; throw new Error('No dispatch'); };
    f.elements['location-recovery-zone'].value = 'Europe/Athens'; f.confirmZone();
    assert.match(f.status.textContent, /storage unavailable/);
    assert.deepEqual(locationWork(421), before);
    await f.submit(); assert.equal(calls, 0);
    assert.equal(f.elements['location-time-recovery'].hidden, false);
});

test('a newer toolbar Clear survives an older applied acknowledgement', async () => {
    const f = await mountedForm(); f.mount(); f.type('older edit');
    let release, handedOff;
    const sent = new Promise(resolve => { handedOff = resolve; });
    globalThis.fetch = async (url, options) => {
        if (options.method === 'POST') {
            handedOff(); await new Promise(resolve => { release = resolve; });
            const state = locationWork(421); return { json: async () => receipt(state.attempts[0]) };
        }
        return { ok: true, json: async () => f.detail };
    };
    const saving = f.submit(); await sent;
    f.clear(); release(); await saving;
    assert.equal(f.reloads(), 0, 'an older acknowledgement must not reload away the newer explicit Clear');
    assert.equal(locationWork(421).draft.value.clearNotes, true);
    assert.deepEqual(JSON.parse(locationWork(421).attempts[0].body).changes.notes, { op: 'set', html: 'older edit' });
    assert.notEqual(locationWork(421).draft.generation, locationWork(421).attempts[0].generation);
    f.mount();
    assert.equal(locationWork(421).draft.value.clearNotes, true);
    assert.equal(f.quill.root.innerHTML, '<p><br></p>');
});

test('reload retains the edit-start wall-time zone after a remote coordinate-only change', async () => {
    const f = await mountedForm();
    f.detail.locationMutation.baseValues.eventUtc.value = '2026-10-29T08:00:00.123456Z';
    f.fields.LocalTimestamp.value = '2026-10-29T10:00:00.123456';
    f.form.dataset.locationDetail = JSON.stringify(f.detail); f.mount();
    f.fields.LocalTimestamp.value = '2026-10-29T11:00:00'; f.input();
    // The new GET legitimately renders the current row at New York coordinates; stored capture UTC is unchanged.
    const remote = structuredClone(f.detail);
    remote.record.latitude = 40.71; remote.record.longitude = -74.01;
    f.form.dataset.locationDetail = JSON.stringify(remote); f.form.dataset.timeZone = 'America/New_York';
    f.fields.LocalTimestamp.value = '2026-10-29T04:00:00.123456'; f.mount();
    let posted;
    globalThis.fetch = async (url, options) => {
        if (options.method === 'POST') {
            posted = JSON.parse(options.body);
            const state = locationWork(421); return { json: async () => receipt(state.attempts[0]) };
        }
        return { ok: true, json: async () => f.detail };
    };
    await f.submit();
    assert.equal(posted.changes.eventUtc.value, '2026-10-29T09:00:00Z', 'the draft was authored in Athens, irrespective of the reloaded row zone');
    assert.equal(posted.expected.eventUtc.value, '2026-10-29T08:00:00.123456Z');
    assert.equal(f.elements['location-input-time-zone'].textContent, 'Europe/Athens');
});

test('reopening inline Activity restores the retained desired selection', async () => {
    const f = await mountedForm();
    f.detail.record.activityName = 'Original activity';
    globalThis.fetch = async () => { throw new Error('offline'); };
    await saveLocationMutation(421, f.detail, 'update', { activity: { op: 'set', id: 9 } },
        { uuid: { state: 'known', value: null }, activityTypeId: { state: 'known', value: 7 } }, { activityDetail: f.detail });
    // Each scenario represents a new page module, while persisted work survives within the scenario.
    const { setupActivityEditorEvents, updateLocationActivity } = await import('../../wwwroot/js/util/activity-editor.js?pending-selection');
    let click, selected, selectedLabel;
    const select = { dataset: {}, tomselect: { addOption: option => { selectedLabel = option.name; }, setValue: value => { selected = value; } } };
    globalThis.TomSelect = function () {};
    const editor = { dataset: { locationId: '421' }, querySelector: () => ({ classList: { toggle: () => {} } }) };
    const target = { closest: selector => selector === '.activity-edit-toggle' ? { closest: () => editor } : null };
    globalThis.document.querySelector = () => ({ addEventListener: (_type, handler) => { click = handler; } });
    const oldGet = document.getElementById;
    document.getElementById = id => id === 'activitySelect-421' ? select : oldGet(id);
    setupActivityEditorEvents('#modal');
    await click({ target, preventDefault: () => {} });
    assert.equal(selected, '9', 'the retained desired Activity must be restored, rather than its original 7');
    assert.notEqual(selectedLabel, 'Original activity');
    globalThis.fetch = async () => ({ json: async () => pendingReceipt(locationWork(421).attempts[0]) });
    assert.equal((await updateLocationActivity(421, 11)).success, false);
    await click({ target, preventDefault: () => {} });
    assert.equal(selected, '11');
    assert.equal(locationWork(421).draft.value.expected.activityTypeId.value, 7);
    assert.equal(JSON.parse(locationWork(421).attempts[0].body).changes.activity.id, 9);
});

test('malformed acknowledgement timestamps cannot settle an otherwise bound action', async () => {
    const f = await mountedForm();
    globalThis.fetch = async (url, options) => {
        if (options.method === 'POST') {
            const state = locationWork(421);
            return { json: async () => ({ ...receipt(state.attempts[0]), completedAtUtc: ['2026-10-30'] }) };
        }
        return { ok: true, json: async () => f.detail };
    };
    const result = await saveLocationMutation(421, f.detail, 'update', { notes: { op: 'clear' } },
        { uuid: { state: 'known', value: null }, notesHtml: { state: 'known', value: 'original' } });
    assert.equal(result.success, false, 'the malformed array timestamp must leave the action Unknown');
    assert.equal(locationWork(421).settledGeneration, undefined);
    assert.equal(locationWork(421).attempts[0].receipt, undefined);
    assert.equal(locationWork(421).active, locationWork(421).attempts[0].actionId);
});


test('a second inline Activity save uses the baseline from the first successful save', async () => {
    const f = await mountedForm();
    const fresh = structuredClone(f.detail);
    fresh.record.activityTypeId = 9; fresh.locationMutation.baseValues.activityTypeId.value = 9;
    const posts = [];
    globalThis.fetch = async (url, options) => {
        if (options.method === 'POST') {
            posts.push(JSON.parse(options.body));
            const state = locationWork(421); const active = state.attempts.find(item => item.actionId === state.active);
            return { json: async () => receipt(active) };
        }
        return { ok: true, json: async () => fresh };
    };
    const expected = { uuid: { state: 'known', value: null }, activityTypeId: { state: 'known', value: 7 } };
    const first = await saveLocationMutation(421, f.detail, 'update', { activity: { op: 'set', id: 9 } }, expected, { activityDetail: f.detail });
    assert.equal(first.success, true);
    const { updateLocationActivity } = await import('../../wwwroot/js/util/activity-editor.js?consecutive-edits');
    fresh.record.activityTypeId = 11; fresh.locationMutation.baseValues.activityTypeId.value = 11;
    assert.equal((await updateLocationActivity(421, 11)).success, true);
    assert.equal(posts[1].expected.activityTypeId.value, 9, 'a settled old draft must not override the fresh Activity baseline');
    assert.equal(locationWork(421).attempts.length, 2);
    assert.deepEqual(locationWork(421).attempts.map(item => JSON.parse(item.body).changes.activity.id), [9, 11]);
    assert.equal(locationWork(421).fresh.record.activityTypeId, 11);
});

/** Mounts the existing delegated Activity owner on the form suite's storage/document seam. */
const mountedActivity = async f => {
    const { setupActivityEditorEvents } = await import(`../../wwwroot/js/util/activity-editor.js?correction=${crypto.randomUUID()}`);
    const handlers = {}, feedback = { textContent: '' };
    let label = '', editing = false;
    const view = { classList: { toggle: (_name, hidden) => { editing = hidden; } } };
    const link = { classList: { toggle: () => {} }, set textContent(value) { label = value; } };
    const editor = { dataset: { locationId: '421' }, querySelector: selector => ({
        '.activity-view': view, '.activity-edit': { classList: { toggle: () => {} } },
        '.activity-feedback': feedback, '.activity-current-link': link
    }[selector]) };
    const select = { dataset: {}, value: '7', get selectedOptions() { return [{ textContent: `Activity ${this.value}` }]; },
        tomselect: { options: { 7: {}, 9: {}, 11: {} }, setValue: value => { select.value = value; }, clear: () => { select.value = ''; } } };
    const buttons = Object.fromEntries(['toggle', 'save', 'clear'].map(name => [name,
        { dataset: { locationId: '421' }, disabled: false, closest: () => editor }]));
    globalThis.TomSelect = function () {};
    const oldGet = document.getElementById;
    document.getElementById = id => id === 'activitySelect-421' ? select : oldGet(id);
    document.querySelector = selector => selector.startsWith('.activity-editor') ? editor
        : selector.startsWith('input') ? { value: 'csrf' } : { addEventListener: (type, callback) => { handlers[type] = callback; } };
    setupActivityEditorEvents('#modal');
    const click = name => handlers.click({ preventDefault: () => {}, target: { closest: selector => selector ===
        ({ toggle: '.activity-edit-toggle', save: '.activity-save-btn', clear: '.activity-clear-btn' }[name]) ? buttons[name] : null } });
    return { click, select, feedback, label: () => label, editing: () => editing,
        choose: value => { select.value = value; handlers.change?.({ target: { closest: () => select } }); } };
};

test('C1 independent form Notes survive inline Activity settlement and restoration with unchanged originals/generation', async () => {
    const f = await mountedForm(); f.mount(); f.type('important unsent Notes');
    const retained = structuredClone(locationWork(421).draft);
    const fresh = structuredClone(f.detail);
    const posts = [];
    globalThis.fetch = async (_url, options) => {
        if (options.method === 'POST') {
            const body = JSON.parse(options.body); posts.push(body);
            fresh.record.activityTypeId = body.changes.activity.id;
            fresh.locationMutation.baseValues.activityTypeId.value = body.changes.activity.id;
            return new Response(JSON.stringify(receipt(locationWork(421).attempts.at(-1))));
        }
        return new Response(JSON.stringify(fresh));
    };
    const activity = await mountedActivity(f); await activity.click('toggle');
    activity.choose('9'); await activity.click('save');
    const firstAttempt = structuredClone(locationWork(421).attempts[0]);
    assert.deepEqual(locationWork(421).draft, retained);
    assert.notEqual(locationWork(421).settledGeneration, retained.generation);
    f.form.dataset.locationDetail = JSON.stringify(fresh); f.quill.root.innerHTML = 'server Notes'; f.mount();
    assert.equal(f.quill.root.innerHTML, 'important unsent Notes');
    assert.deepEqual(locationWork(421).draft, retained);
    await activity.click('toggle'); activity.choose('11'); await activity.click('save');
    assert.equal(posts[1].expected.activityTypeId.value, 9);
    assert.deepEqual(locationWork(421).attempts[0], firstAttempt);
    assert.deepEqual(locationWork(421).draft, retained);
});

test('C2 older inline Activity completion retains newer selection or Clear for its own save', async () => {
    for (const choice of ['11', '']) {
        const f = await mountedForm(); const activity = await mountedActivity(f);
        const fresh = structuredClone(f.detail); let release, sent;
        const handedOff = new Promise(resolve => { sent = resolve; });
        const bodies = [];
        globalThis.fetch = async (_url, options) => {
            if (options.method === 'POST') {
                bodies.push(options.body);
                const command = JSON.parse(options.body);
                if (bodies.length === 1) { sent(); await new Promise(resolve => { release = resolve; }); }
                fresh.record.activityTypeId = command.changes.activity.id ?? null;
                fresh.record.activityName = fresh.record.activityTypeId == null ? null : `Activity ${fresh.record.activityTypeId}`;
                fresh.locationMutation.baseValues.activityTypeId.value = fresh.record.activityTypeId;
                return new Response(JSON.stringify(receipt(locationWork(421).attempts.at(-1))));
            }
            return new Response(JSON.stringify(fresh));
        };
        await activity.click('toggle'); activity.choose('9');
        const saving = activity.click('save'); await handedOff;
        const submitted = structuredClone(locationWork(421).attempts[0]);
        activity.choose(choice);
        const generation = locationWork(421, 'activity').draft?.generation;
        release(); await saving;
        const work = locationWork(421, 'activity');
        assert.equal(activity.select.value, choice); assert.equal(activity.editing(), true);
        assert.equal(activity.label(), 'Activity 9'); assert.match(activity.feedback.textContent, /[Nn]ewer/);
        assert.equal(work.draft.generation, generation); assert.notEqual(work.settledGeneration, generation);
        assert.deepEqual(work.draft.value.changes.activity, choice ? { op: 'set', id: 11 } : { op: 'clear' });
        assert.equal(work.attempts[0].body, submitted.body); assert.equal(work.attempts[0].actionId, submitted.actionId);
        const completed = structuredClone(work.attempts[0]);
        await activity.click('save');
        assert.equal(JSON.parse(bodies[1]).expected.activityTypeId.value, 9);
        assert.deepEqual(JSON.parse(bodies[1]).changes.activity, choice ? { op: 'set', id: 11 } : { op: 'clear' });
        assert.deepEqual(locationWork(421).attempts[0], completed);
        assert.equal(activity.editing(), false);
    }
});

test('C3 duplicate wire members cannot settle mutations, recovery, retry or future-effect exclusion', async () => {
    for (const entry of ['mutation', 'recovery', 'retry', 'exclusion', 'detail', 'valid']) {
        const f = await mountedForm(); let posts = 0, reads = 0;
        const duplicate = envelope => '{"state":"pending",' + JSON.stringify(envelope).slice(1);
        globalThis.fetch = async (url, options) => {
            const active = locationWork(421).attempts.at(-1);
            if (options.method === 'POST') {
                posts++;
                if (entry === 'recovery' || entry === 'exclusion' || entry === 'retry' && posts === 1) throw new Error('lost reply');
                const text = entry === 'mutation' || entry === 'retry' ? duplicate(receipt(active)) : JSON.stringify(receipt(active));
                return new Response(text);
            }
            if (url.includes('/mutations/')) {
                const observation = { contract: 'location-mutation-1', subjectId: 'owner-a', locationId: 421,
                    baseId: active.baseId, actionId: active.actionId, observedAtUtc: entry === 'exclusion' ? '2100-01-01T00:00:00Z' : '2026-10-30T10:00:00Z',
                    state: 'unknown', code: entry === 'exclusion' ? 'operationHistoryExpired' : 'operationNotObserved',
                    futureEffects: entry !== 'exclusion', expiresAtUtc: active.expiresAtUtc,
                    ...(entry === 'exclusion' ? { historicalOutcome: 'unknown' } : {}) };
                return new Response(entry === 'retry' ? JSON.stringify(observation) : entry === 'mutation' ? '{}' : duplicate(entry === 'recovery' ? receipt(active) : observation));
            }
            reads++;
            // Escaped names compare as decoded keys, including nested objects inside arrays.
            return new Response(entry === 'detail' ? JSON.stringify(f.detail).replace('"baseValues":', '"extra":[{"a":1,"\\u0061":2}],"baseValues":') : JSON.stringify(f.detail));
        };
        const args = [421, f.detail, 'update', { notes: { op: 'clear' } },
            { uuid: { state: 'known', value: null }, notesHtml: { state: 'known', value: 'original' } }];
        let result = await saveLocationMutation(...args);
        if (entry === 'exclusion' || entry === 'retry') result = await saveLocationMutation(...args);
        assert.equal(result.success, entry === 'valid', entry);
        assert.equal(!!locationWork(421).settledGeneration, entry === 'valid', entry);
        assert.equal(locationWork(421).attempts[0].exclusion, undefined, entry);
        assert.equal(posts, entry === 'retry' ? 2 : 1, entry);
        if (['mutation', 'recovery', 'exclusion', 'retry'].includes(entry)) {
            assert.equal(locationWork(421).attempts[0].receipt, undefined, entry);
            assert.equal(reads, 0, entry);
        }
    }
});

/** Builds the server's Pending shape without terminal-only members. */
const pendingReceipt = value => {
    const { completedAtUtc, changed, detailUrl, addressOutcome, providerOutcome, ...common } = receipt(value);
    return { ...common, state: 'pending', futureEffects: true, observedAtUtc: '2026-10-30T10:00:00.1234567Z',
        executeBeforeUtc: '2026-10-30T10:02:00.123456Z', retryAfterSeconds: 2 };
};

test('restored time keeps the DST occurrence, and untouched capture microseconds stay out of the command', async () => {
    for (const occurrence of ['earlier', 'later', 'untouched']) {
        const f = await mountedForm();
        f.fields.LocalTimestamp.value = '2026-10-25T03:00:00.123456';
        f.detail.locationMutation.baseValues.eventUtc.value = '2026-10-25T00:00:00.123456Z';
        f.form.dataset.locationDetail = JSON.stringify(f.detail); f.mount();
        if (occurrence === 'untouched') f.type('only Notes changed');
        else {
            f.fields.LocalTimestamp.value = '2026-10-25T03:30:00.123456';
            f.elements.timeAmbiguity.value = occurrence; f.input();
        }
        f.form.dataset.timeZone = 'America/New_York'; f.elements.timeAmbiguity.value = ''; f.mount();
        let posted;
        globalThis.fetch = async (_url, options) => {
            if (options.method !== 'POST') return { ok: true, json: async () => f.detail };
            posted = JSON.parse(options.body); return { json: async () => receipt(locationWork(421).attempts[0]) };
        };
        await f.submit();
        if (occurrence === 'untouched') {
            assert.equal(posted.changes.eventUtc, undefined); assert.equal(posted.expected.eventUtc, undefined);
            assert.equal(locationWork(421).draft.value.draft.initial.LocalTimestamp, '2026-10-25T03:00:00.123456');
        } else {
            assert.equal(f.elements.timeAmbiguity.value, occurrence);
            assert.equal(posted.changes.eventUtc.value, `2026-10-25T0${occurrence === 'earlier' ? 0 : 1}:30:00.123456Z`);
            assert.equal(posted.expected.eventUtc.value, '2026-10-25T00:00:00.123456Z');
        }
    }
});

test('acknowledgement timestamps require UTC Gregorian strings at emitted DateTime precision and state-specific shapes', () => {
    const { changed, detailUrl, addressOutcome, providerOutcome, ...terminal } = receipt(attempt);
    const rejected = { ...terminal, state: 'rejected', code: 'locationBaseStale', staleGroups: ['notesHtml'] };
    const envelopes = [receipt(attempt), rejected, pendingReceipt(attempt)];
    const malformed = [null, 0, ['2026-10-30'], {}, '2026-10-30', '2026-10-30T10:00:00+00:00', '2026-10-30T10:00:00z',
        '0000-01-01T00:00:00Z', '2026-02-29T10:00:00Z', '2026-02-30T10:00:00Z', '2026-13-01T10:00:00Z',
        '2026-10-00T10:00:00Z', '2026-10-30T24:00:00Z', '2026-10-30T10:60:00Z', '2026-10-30T10:00:60Z',
        '2026-10-30T10:00:00.12345678Z', ' 2026-10-30T10:00:00Z'];
    for (const envelope of envelopes) {
        assert.equal(validateLocationReceipt(envelope, attempt), true);
        const fields = Object.keys(envelope).filter(key => key.endsWith('Utc'));
        for (const field of fields) {
            for (const value of malformed) assert.equal(validateLocationReceipt({ ...envelope, [field]: value }, attempt), false, `${envelope.state}.${field}: ${JSON.stringify(value)}`);
            for (const value of ['0001-01-01T00:00:00Z', '9999-12-31T23:59:59.9999999Z', '2024-02-29T10:00:00Z',
                ...Array.from({ length: 7 }, (_, i) => `2026-10-30T10:00:00.${'1234567'.slice(0, i + 1)}Z`)])
                assert.equal(validateLocationReceipt({ ...envelope, [field]: value }, attempt), true, value);
        }
    }
    assert.equal(validateLocationReceipt({ ...rejected, staleGroups: null }, attempt), false);
    assert.equal(validateLocationReceipt({ ...pendingReceipt(attempt), completedAtUtc: terminal.completedAtUtc }, attempt), false);
    assert.equal(validateLocationReceipt({ ...receipt(attempt), observedAtUtc: terminal.completedAtUtc }, attempt), false);
});

test('malformed or premature no-row timestamps cannot authorize retry or replacement', async () => {
    for (const expired of [false, true]) {
        const f = await mountedForm(); let posts = 0;
        f.detail.locationMutation.expiresAtUtc = '2099-01-01T00:00:00.1234567Z';
        globalThis.fetch = async (_url, options) => {
            if (options.method === 'POST') { posts++; throw new Error('lost reply'); }
            const active = locationWork(421).attempts[0];
            return { json: async () => ({ contract: 'location-mutation-1', subjectId: 'owner-a', locationId: 421,
                baseId: active.baseId, actionId: active.actionId, observedAtUtc: expired ? '2099-01-01T00:00:00.1234566Z' : ['2026-10-30'],
                state: 'unknown', code: expired ? 'operationHistoryExpired' : 'operationNotObserved', futureEffects: !expired,
                expiresAtUtc: active.expiresAtUtc, ...(expired ? { historicalOutcome: 'unknown' } : {}) }) };
        };
        const args = [421, f.detail, 'update', { notes: { op: 'clear' } }, { uuid: { state: 'known', value: null }, notesHtml: { state: 'known', value: 'original' } }];
        assert.equal((await saveLocationMutation(...args)).success, false);
        assert.equal((await saveLocationMutation(...args)).success, false);
        assert.equal(posts, 1); assert.equal(locationWork(421).attempts[0].exclusion, undefined);
        assert.equal(locationWork(421).settledGeneration, undefined);
    }
});

test('actual page controls retain map picks, empty-document Clear and image insertion before submission', async t => {
    const f = await mountedForm(); const events = {}, changes = [];
    const control = () => ({ value: '', dataset: {}, classList: { add: () => {} }, setAttribute: () => {},
        addEventListener: (type, callback) => { events[type] = callback; } });
    const clear = control(), image = control(), modal = control();
    const dom = { ...f.elements, ...f.fields, locationForm: f.form, mapContainer: { dataset: { lat: '37.98', lon: '23.72' } },
        notes: { dataset: { notesContent: 'original' } }, insertImageBtn: image, imageModal: modal, imageUrl: { value: 'https://images.example.test/a.jpg' } };
    let ready, pick, insert, clearClick;
    clear.addEventListener = (_type, callback) => { clearClick = callback; };
    image.addEventListener = (_type, callback) => { insert = callback; };
    document.getElementById = id => dom[id]; document.createElement = () => clear;
    // Templates are inert; this scenario tests control intent, while notesImages owns image URL policy.
    clear.content = { querySelectorAll: () => [] };
    document.querySelector = () => ({ querySelectorAll: () => [] });
    document.addEventListener = (_type, callback) => { ready = callback; };
    f.form.dispatchEvent = event => f.listeners[event.type].forEach(callback => callback({ target: f.form }));
    f.fields.Latitude.dispatchEvent = event => f.listeners[event.type].forEach(callback => callback({ target: { name: 'Latitude' } }));
    f.quill.on = (type, callback) => { if (type === 'text-change') changes.push(callback); };
    f.quill.getModule = () => ({ container: { appendChild: () => {} }, addHandler: () => {} });
    f.quill.getText = () => ''; f.quill.getLength = () => 0;
    f.quill.setText = () => {
        if (f.quill.root.innerHTML === '') return; // Real Quill emits no delta for a no-op Clear.
        f.quill.root.innerHTML = ''; changes.forEach(callback => callback({}, {}, 'api'));
    };
    f.quill.insertEmbed = () => { f.quill.root.innerHTML = '<p>image added</p>'; changes.forEach(callback => callback({}, {}, 'api')); };
    const map = { setView: () => map, attributionControl: { setPrefix: () => {} }, getContainer: () => ({ style: {} }), on: (_type, callback) => { pick = callback; } };
    const { mountProtectedLocationForm } = await import('../../wwwroot/js/util/location-edit-form.js');
    const source = await readFile(new URL('../../wwwroot/js/Areas/User/Location/Edit.js', import.meta.url), 'utf8');
    vm.runInNewContext(source.replace(/^import .*;$/gm, ''), { document, Event, console: { log: () => {} },
        mountProtectedLocationForm, notesHtmlForDisplay: html => html, registerNoteImageBlot: () => {},
        Quill: function () { return f.quill; }, createTileLayer: () => ({ addTo: () => {} }),
        L: { map: () => map, marker: () => ({ addTo: () => ({ remove: () => {} }) }) },
        bootstrap: { Modal: { getInstance: () => ({ hide: () => {} }) } } });
    ready();
    await t.test('map picker retains both programmatic coordinate changes', () => {
        pick({ latlng: { lat: 51.5, lng: 0 } });
        assert.equal(locationWork(421).draft?.value.current.Latitude, '51.50000000');
        assert.equal(locationWork(421).draft?.value.current.Longitude, '0.00000000');
    });
    await t.test('Clear persists with and without a Quill delta', () => {
        clearClick(); assert.equal(locationWork(421).draft?.value.clearNotes, true);
        retainLocationDraft(421, { ...locationWork(421).draft.value, clearNotes: false }); clearClick();
        assert.equal(locationWork(421).draft.value.clearNotes, true, 'Clear must persist even without a Quill delta');
    });
    await t.test('image insertion replaces the previous Clear intent', () => {
        insert(); assert.equal(locationWork(421).draft?.value.clearNotes, false);
        assert.equal(locationWork(421).draft.value.current.Notes, '<p>image added</p>');
    });
});
