import assert from 'node:assert/strict';
import test from 'node:test';
import { createTripImporter, submitTripImport, genericImportFailure, uncertainImport } from '../../wwwroot/js/Areas/User/Trip/tripImportClient.js';

const id = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const file = new Blob(['exact selected bytes'], { type: 'application/vnd.google-earth.kml+xml' });
const reply = (value, status = 200) => new Response(JSON.stringify(value), { status });
const duplicate = () => reply({ status: 'duplicate', tripId: id });
const success = () => reply({ status: 'success', tripId: id, redirectUrl: `/User/Trip/Edit/${id}`, notices: [{ code: 'notice' }] });
const review = () => reply({ status: 'review', review: { token: 'root-token', tripId: id, planning: [
    { key: 'trip/root', state: 'conflict', choices: [{ action: 'keep', token: 'keep-token' }, { action: 'use', token: 'use-token' }, { action: 'clear', token: 'clear-token' }] },
    { key: 'place/removed', state: 'removed', choices: [{ action: 'remove', token: 'remove-token' }] }
] } });
globalThis.document = { querySelector: () => ({ value: 'authentic-page-token' }) };
const mount = (fetchImpl, options) => {
    const observed = { errors: [], redirects: [], duplicates: 0, notices: [], calls: [] };
    const handlers = { fetchImpl: async function (url, init) {
        assert.equal(this, globalThis);
        observed.calls.push({ url, ...init });
        return fetchImpl(url, init, observed.calls.length);
    }, showError: m => observed.errors.push(m), onRedirect: u => observed.redirects.push(u),
    onDuplicate: () => observed.duplicates++, showNotices: n => observed.notices.push(...n) };
    return { importer: createTripImporter(handlers, options), handlers, observed };
};
const store = () => { const values = new Map(); return { getItem: k => values.get(k), setItem: (k, v) => values.set(k, v), removeItem: k => values.delete(k) }; };

// Recovery retires local intent only; neither inspection nor abandonment claims a server outcome.
test('lost reply, reload, cancelled recovery and confirmed abandonment require a separately selected import', async () => {
    const storage = store();
    const { importer, observed } = mount((url, init, n) => {
        if (n === 1) return duplicate(); if (n === 2) return review(); throw new Error('lost reply');
    }, { storage, owner: 'owner' });
    await importer.upload(file); await importer.update(); importer.choose('trip/root', 'use');
    await importer.apply();
    assert.equal(importer.abandon(false), false);
    assert.equal(importer.state.file, file); assert.equal(importer.state.choices.get('trip/root'), 'use');
    const reopened = mount(() => duplicate(), { storage, owner: 'owner' });
    assert.equal(reopened.importer.state.uncertain, true);
    assert.equal(reopened.importer.abandon(false), false);
    await reopened.importer.upload(file); await reopened.importer.createCopy();
    assert.equal(reopened.observed.calls.length, 0);
    assert.equal(reopened.importer.abandon(true), true);
    const state = reopened.importer.state;
    assert.equal(state.uncertain, false); assert.equal(state.file, null);
    assert.equal(state.review, null); assert.equal(state.choices.size, 0);
    assert.match(state.message, /still unknown/);
    await reopened.importer.update(); await reopened.importer.apply(); await reopened.importer.createCopy();
    assert.equal(reopened.observed.calls.length, 0); assert.deepEqual(reopened.observed.redirects, []);
    const fresh = new Blob(['separately selected file']);
    await reopened.importer.upload(fresh);
    assert.equal(reopened.observed.calls.length, 1);
    assert.equal(await reopened.observed.calls[0].body.get('file').text(), 'separately selected file');
    assert.equal(reopened.observed.calls[0].body.get('reviewToken'), null);
    assert.deepEqual(reopened.observed.calls[0].body.getAll('choices'), []);
    assert.equal(observed.calls.length, 3);
    assert.equal(importer.abandon(true), false); // The obsolete page cannot retire a later attempt.
});

test('confirmed abandonment discards the mounted file, review and explicit choices without sending', async () => {
    const { importer, observed } = mount((url, init, n) => {
        if (n === 1) return duplicate(); if (n === 2) return review(); throw new Error('lost reply');
    });
    await importer.upload(file); await importer.update(); importer.choose('trip/root', 'use'); await importer.apply();
    assert.equal(importer.abandon(true), true);
    assert.equal(importer.state.file, null); assert.equal(importer.state.review, null); assert.equal(importer.state.choices.size, 0);
    await importer.apply(); await importer.createCopy(); assert.equal(observed.calls.length, 3);
});

test('a late abandoned reply cannot redirect, publish success or release a separate import hold', async () => {
    let finishOld, finishNew;
    const storage = store();
    const old = mount(() => new Promise(resolve => { finishOld = resolve; }), { storage, owner: 'owner' });
    const pendingOld = old.importer.upload(file);
    const fresh = mount(() => new Promise(resolve => { finishNew = resolve; }), { storage, owner: 'owner' });
    assert.equal(fresh.importer.abandon(true), true);
    const pendingNew = fresh.importer.upload(new Blob(['new attempt']));
    const hold = storage.getItem('wayfarer:native-import-pending:owner');
    finishOld(success()); await pendingOld;
    assert.deepEqual(old.observed.redirects, []); assert.deepEqual(old.observed.notices, []);
    assert.equal(fresh.importer.state.busy, true);
    assert.equal(storage.getItem('wayfarer:native-import-pending:owner'), hold);
    finishNew(duplicate()); await pendingNew;
    assert.equal(fresh.observed.duplicates, 1); assert.deepEqual(fresh.observed.redirects, []);
});

test('multipart upload preserves exact bytes, modes and real antiforgery header without overriding the boundary', async () => {
    const { importer, observed } = mount((url, init, n) => n === 1 ? duplicate() : success());
    await importer.upload(file);
    await importer.createCopy();
    assert.equal(observed.duplicates, 1);
    assert.deepEqual(observed.redirects, [`/User/Trip/Edit/${id}`]);
    assert.equal(observed.notices.length, 1);
    for (const [index, call] of observed.calls.entries()) {
        assert.equal(call.url, '/User/Trip/Import'); assert.equal(call.method, 'POST');
        assert.equal(call.cache, 'no-store'); assert.equal(call.credentials, 'same-origin');
        assert.equal(call.headers.RequestVerificationToken, 'authentic-page-token');
        assert.equal(call.headers['Content-Type'], undefined);
        assert.equal(call.body.get('mode'), index === 0 ? 'Auto' : 'CreateNew');
        assert.equal(await call.body.get('file').text(), 'exact selected bytes');
    }
});

test('review sends no mutation, Keep defaults, and Use/Clear/removal are exclusive selected permits', async () => {
    for (const action of ['keep', 'use', 'clear']) {
        const { importer, observed } = mount((url, init, n) => n === 1 ? duplicate() : n === 2 ? review() : success());
        await importer.upload(file); await importer.update();
        assert.equal(observed.calls[1].url, '/User/Trip/ReviewImport');
        assert.equal(importer.state.choices.get('trip/root'), 'keep');
        assert.equal(importer.state.choices.get('place/removed'), '');
        importer.choose('trip/root', action); importer.choose('place/removed', 'remove');
        await importer.apply();
        const body = observed.calls[2].body;
        assert.equal(body.get('reviewToken'), 'root-token');
        assert.deepEqual(body.getAll('choices'), [`${action}-token`, 'remove-token']);
        assert.equal(importer.state.file, file);
    }
});

test('known stale rejection retains file and choices, requires fresh review and never auto-applies it', async () => {
    const { importer, observed } = mount((url, init, n) => n === 1 ? duplicate() : n === 3
        ? reply({ status: 'error', code: 'import_review_stale', message: '<script>unsafe text</script>' }, 409) : review());
    await importer.upload(file); await importer.update();
    importer.choose('trip/root', 'use'); importer.choose('place/removed', 'remove');
    await importer.apply();
    assert.equal(importer.state.file, file); assert.equal(importer.state.choices.get('trip/root'), 'use');
    await importer.apply(); assert.equal(observed.calls.length, 3);
    await importer.update(); assert.equal(observed.calls.length, 4);
    assert.equal(importer.state.choices.get('trip/root'), 'use'); assert.equal(importer.state.reviewStale, false);
    assert.doesNotMatch(observed.errors.join(), /unsafe|script/);
});

test('cancellation sends nothing, preserves the selection and cannot fall through to Create new', async () => {
    const { importer, observed } = mount(() => duplicate());
    await importer.upload(file); importer.cancel();
    await importer.createCopy(); await importer.update(); await importer.apply();
    assert.equal(observed.calls.length, 1); assert.equal(importer.state.file, file);
});

test('recoverable file errors retain the file and permit a separate explicit Create new action', async () => {
    const { importer, observed } = mount((url, init, n) => n === 1 ? reply({ status: 'error', code: 'validation_failed' }, 422) : success());
    await importer.upload(file);
    assert.deepEqual(observed.errors, [genericImportFailure]); assert.equal(importer.state.file, file);
    assert.equal(importer.state.uncertain, false); await importer.createCopy();
    assert.equal(observed.calls.length, 2);
});

test('read-only review transport failure retains choices and is safely refreshable', async () => {
    const { importer, observed } = mount((url, init, n) => {
        if (n === 1) return duplicate(); if (n === 3) throw new Error('network'); return review();
    });
    await importer.upload(file); await importer.update(); importer.choose('trip/root', 'clear');
    await importer.update(); assert.equal(importer.state.uncertain, false); assert.equal(importer.state.choices.get('trip/root'), 'clear');
    await importer.update(); assert.equal(observed.calls.length, 4);
});

for (const failure of ['network', 'non-json', 'redirect', 'malformed-success', 'server-unknown']) test(`${failure} holds mutating replay across reload and preserves the in-flight file and choices`, async () => {
    const storage = store();
    const { importer, observed } = mount((url, init, n) => {
        if (n === 1) return duplicate(); if (n === 2) return review();
        if (failure === 'network') throw new Error('private transport text');
        if (failure === 'non-json') return new Response('<html>untrusted</html>');
        if (failure === 'redirect') return { redirected: true, json: async () => ({ status: 'success' }) };
        if (failure === 'malformed-success') return reply({ status: 'success', redirectUrl: 'https://untrusted.invalid/' });
        return reply({ status: 'error', code: 'import_outcome_unknown', message: 'unsafe' }, 500);
    }, { storage, owner: 'owner' });
    await importer.upload(file); await importer.update(); importer.choose('trip/root', 'use');
    await importer.apply(); assert.equal(importer.state.uncertain, true); assert.equal(importer.state.file, file);
    assert.equal(importer.state.choices.get('trip/root'), 'use');
    await importer.apply(); await importer.createCopy(); await importer.update(); await importer.upload(new Blob(['other']));
    assert.equal(observed.calls.length, 3);
    const reopened = mount(() => { throw new Error('must not send'); }, { storage, owner: 'owner' }).importer;
    assert.equal(reopened.state.uncertain, true); await reopened.upload(file);
    assert.equal(mount(() => duplicate(), { storage, owner: 'other-owner' }).importer.state.uncertain, false);
});

test('an in-flight request fences later file selection and cancellation without discarding its exact bytes', async () => {
    let finish; const storage = store();
    const { importer, observed } = mount(() => new Promise(resolve => { finish = resolve; }), { storage, owner: 'owner' });
    const running = importer.upload(file);
    assert.equal(mount(() => duplicate(), { storage, owner: 'owner' }).importer.state.uncertain, true);
    await importer.upload(new Blob(['later'])); importer.cancel();
    finish(duplicate()); await running;
    assert.equal(observed.calls.length, 1); assert.equal(importer.state.file, file); assert.equal(importer.state.cancelled, false);
});

test('legacy direct entry point reports uncertainty without exposing transport or response text', async () => {
    const { handlers, observed } = mount(async () => { throw new Error('private text'); });
    await submitTripImport(file, 'Auto', handlers);
    assert.deepEqual(observed.errors, [uncertainImport]);
});

for (const [payload, status] of [
    [{ status: 'error', code: 'validation_failed', applied: true }, 409],
    [{ status: 'success', tripId: id, redirectUrl: `/User/Trip/Edit/${id}`, notices: [] }, 202],
    [{ status: 'success', tripId: id, redirectUrl: `/User/Trip/Edit/${id}` }, 200]
]) test(`contradictory or incomplete ${status} outcome cannot release replay`, async () => {
    const { importer, observed } = mount(() => reply(payload, status));
    await importer.upload(file); assert.equal(importer.state.uncertain, true);
    await importer.createCopy(); assert.equal(observed.calls.length, 1);
});
