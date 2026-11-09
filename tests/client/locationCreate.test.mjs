import test from 'node:test';
import assert from 'node:assert/strict';
import { createValues, attachLocationCreate } from '../../wwwroot/js/util/location-create.js';

// Blank measurements must not become zero, and the original attempt is independent of a changed form.
test('Web authored values retain zero, empty Notes and an independent original attempt', () => {
    const values = new FormData();
    values.set('Latitude', '0'); values.set('Longitude', '0'); values.set('Altitude', ''); values.set('Notes', '');
    values.set('LocalTimestamp', '2026-11-08T15:00:00');
    const frozen = JSON.stringify(createValues(values));
    values.set('Notes', 'newer'); values.set('Latitude', '1');
    assert.equal(JSON.parse(frozen).notes, '');
    assert.equal(JSON.parse(frozen).latitude, 0);
    assert.equal(JSON.parse(frozen).altitude, null);
    assert.equal(createValues(values).notes, 'newer');
});

// Exercise the production form handler with explicit owner proof, form fields and returned MVC content.
const fixture = t => {
    const originals = Object.fromEntries(['FormData', 'DOMParser', 'document', 'fetch'].map(name => [name, globalThis[name]]));
    t.after(() => { for (const [name, value] of Object.entries(originals)) globalThis[name] = value; });
    const fields = Object.fromEntries(Object.entries({ Latitude: '0', Longitude: '0', LocalTimestamp: '2026-11-08T12:00:00',
        Notes: 'first', CreateKey: 'original-key', CreateIntent: 'owner-bound', AttemptedValues: '', CreateMayHaveEffect: 'false',
        __RequestVerificationToken: 'old-csrf' }).map(([name, value]) => [name, { value }]));
    const status = {}, button = {}, nodes = [], requests = [];
    const dialog = { open: true, close() { this.open = false; } };
    const surface = { hidden: false, inert: false, isConnected: true,
        querySelectorAll: () => [dialog], before: node => { warning = node; } };
    let submit, warning, redisplayed;
    const form = { elements: fields, action: '/User/Location/Create', hidden: false, reportValidity: () => true,
        closest: () => surface, addEventListener: (_, callback) => { submit = callback; },
        querySelector: selector => selector === 'button[type=submit]' ? button : status, before: node => { warning = node; } };
    globalThis.FormData = class extends originals.FormData {
        constructor() { super(); for (const [name, field] of Object.entries(fields)) this.set(name, field.value); }
    };
    globalThis.document = { getElementById: () => ({ dataset: { subjectId: 'original' } }), createElement: () => {
        const node = { setAttribute() {}, append() {}, focus() {}, remove() { this.removed = true; },
            addEventListener: (_, callback) => { node.click = callback; } };
        nodes.push(node); return node;
    } };
    const returned = { owner: 'original', csrf: 'fresh-csrf', ok: true, redirected: false };
    const nextFields = Object.fromEntries(Object.entries(fields).map(([name, field]) => [name, { value: field.value }]));
    const nextStatus = {}, notes = { dataset: {} }, content = {};
    globalThis.DOMParser = class {
        parseFromString() { return {
            getElementById: id => id === 'browser-antiforgery' ? returned.owner ? { dataset: { subjectId: returned.owner } } : null
                : id === 'notes' ? notes : nextStatus,
            querySelector: selector => selector === '.site-content' ? content : selector.includes('__RequestVerificationToken')
                ? { value: returned.csrf } : { elements: nextFields }
        }; }
    };
    globalThis.fetch = async (url, options) => {
        requests.push([url, options]);
        return { ok: returned.ok, redirected: returned.redirected, text: async () => '' };
    };
    attachLocationCreate(form, () => fields.Notes.value, value => { redisplayed = value; });
    return { fields, form, surface, dialog, status, button, nodes, requests, returned, nextFields, nextStatus, notes, content,
        submit: () => submit({ preventDefault() {} }), get warning() { return warning; }, get redisplayed() { return redisplayed; } };
};

// Missing/malformed owner proof needs a truthful warning and the same privacy fence as affirmative foreign evidence.
for (const [label, owner, ok, changed] of [
    ['markerless HTTP 500', null, false, false],
    ['malformed HTTP 200 page', '', true, false],
    ['foreign-owner HTTP 200', 'other', true, true],
    ['foreign-owner HTTP 500', 'other', false, true]
]) test(`${label} retains the original attempt until owner recheck`, async t => {
    const f = fixture(t);
    Object.assign(f.returned, { owner, ok });
    await f.submit();
    const frozen = f.fields.AttemptedValues.value;
    assert.equal(f.surface.hidden, true);
    assert.equal(f.surface.inert, true);
    assert.equal(f.dialog.open, false);
    assert.equal(f.redisplayed, undefined);
    if (changed) assert.match(f.warning.textContent, /Your session changed/);
    else {
        assert.doesNotMatch(f.warning.textContent, /Your session changed/);
        assert.match(f.warning.textContent, /save could not be confirmed/);
        assert.match(f.warning.textContent, /recheck the original account/);
    }
    f.fields.Notes.value = 'retained newer input';
    await f.submit();
    assert.equal(f.requests.length, 1, 'Hidden forms cannot submit under a different account');
    const recheck = f.nodes[1];
    // Failed, redirected, markerless, foreign and antiforgery-less proofs must not reopen the private form.
    for (const proof of [{ owner: 'original', ok: false }, { owner: 'original', redirected: true },
        { owner: null }, { owner: 'other' }, { owner: 'original', csrf: '' }]) {
        Object.assign(f.returned, { owner: 'original', ok: true, redirected: false, csrf: 'fresh-csrf' }, proof);
        await recheck.click();
        assert.equal(f.surface.hidden, true);
        assert.equal(f.fields.__RequestVerificationToken.value, 'old-csrf');
    }
    Object.assign(f.returned, { owner: 'original', ok: true, redirected: false, csrf: 'fresh-csrf' });
    await recheck.click();
    assert.equal(f.surface.hidden, false);
    assert.equal(f.surface.inert, false);
    assert.equal(f.warning.removed, true);
    assert.equal(f.fields.CreateKey.value, 'original-key');
    assert.equal(f.fields.CreateIntent.value, 'owner-bound');
    assert.equal(f.fields.AttemptedValues.value, frozen);
    assert.equal(f.fields.Notes.value, 'retained newer input');
    assert.equal(f.fields.__RequestVerificationToken.value, 'fresh-csrf');
    assert.match(f.status.textContent, /earlier save is unconfirmed/);
    assert.equal(f.requests.filter(([, options]) => options.method === 'POST').length, 1);
    await f.submit();
    const retry = f.requests.at(-1)[1].body;
    assert.equal(retry.get('AttemptedValues'), frozen);
    assert.equal(retry.get('CreateKey'), 'original-key');
    assert.equal(retry.get('Notes'), 'retained newer input');
    assert.equal(retry.get('__RequestVerificationToken'), 'fresh-csrf');
});

// An unsuccessful page with affirmative same-owner proof is still an unconfirmed save, never successful content.
test('same-owner server failure retains actionable retry without adopting the error page', async t => {
    const f = fixture(t);
    f.returned.ok = false;
    await f.submit();
    assert.equal(f.surface.hidden, false);
    assert.equal(f.redisplayed, undefined);
    assert.match(f.status.textContent, /save could not be confirmed/);
    assert.equal(f.button.textContent, 'Retry original save');
});

// Freeze one attempt across transport loss, double submit and typing during a delayed same-owner reply.
for (const typing of [false, true]) test(`same-owner success retains original retry; in-flight typing: ${typing}`, async t => {
    const f = fixture(t);
    let release, calls = 0;
    globalThis.fetch = async () => {
        calls++;
        if (calls === 1) throw new Error('lost reply');
        await new Promise(resolve => { release = resolve; });
        return { ok: true, redirected: false, text: async () => '' };
    };
    await f.submit();
    const attempt = f.fields.AttemptedValues.value;
    const pending = f.submit();
    await Promise.resolve();
    await f.submit();
    if (typing) f.fields.Notes.value = 'latest during response';
    release();
    await pending;
    assert.equal(calls, 2);
    assert.equal(f.fields.AttemptedValues.value, attempt);
    assert.equal(f.fields.CreateMayHaveEffect.value, 'true');
    assert.equal(f.redisplayed, f.content);
    if (typing) {
        assert.equal(f.nextFields.Notes.value, 'latest during response');
        assert.equal(f.notes.dataset.notesContent, 'latest during response');
        assert.match(f.nextStatus.textContent, /remain unsaved/);
    } else assert.equal(f.nextFields.Notes.value, 'first');
});

// Serial owner proofs prevent an earlier request from racing a later privacy observation.
test('owner recheck cannot start a second request while pending and failed proof stays recoverable', async t => {
    const f = fixture(t);
    f.returned.owner = 'other';
    await f.submit();
    const recheck = f.nodes[1];
    const frozen = f.fields.AttemptedValues.value;
    let release, calls = 0;
    globalThis.fetch = async () => {
        calls++;
        await new Promise(resolve => { release = resolve; });
        return { ok: true, redirected: false, text: async () => '' };
    };
    const pending = recheck.click();
    await Promise.resolve();
    assert.equal(recheck.disabled, true);
    await recheck.click();
    assert.equal(calls, 1, 'a concurrent recheck must not issue another proof request');
    release();
    await pending;
    assert.equal(f.surface.hidden, true, 'foreign proof keeps all retained input fenced');
    assert.equal(recheck.disabled, false);
    Object.assign(f.returned, { owner: 'original' });
    globalThis.fetch = async () => ({ ok: true, redirected: false, text: async () => '' });
    await recheck.click();
    assert.equal(f.surface.hidden, false);
    assert.equal(f.fields.AttemptedValues.value, frozen);
});

// A proof belongs only to the still-mounted recovery surface which requested it.
test('a detached recovery cannot apply its pending original-owner proof', async t => {
    const f = fixture(t);
    f.returned.owner = 'other';
    await f.submit();
    let release;
    globalThis.fetch = async () => {
        await new Promise(resolve => { release = resolve; });
        return { ok: true, redirected: false, text: async () => '' };
    };
    const pending = f.nodes[1].click();
    await Promise.resolve();
    f.surface.isConnected = false;
    f.returned.owner = 'original';
    release();
    await pending;
    assert.equal(f.surface.hidden, true);
    assert.equal(f.fields.__RequestVerificationToken.value, 'old-csrf');
});
