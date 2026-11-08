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

// Session changes hide private input until a read-only proof confirms the original owner; identity never rotates.
test('account recheck restores the original attempt without submitting it as another account', async t => {
    const originals = Object.fromEntries(['FormData', 'DOMParser', 'document', 'fetch'].map(name => [name, globalThis[name]]));
    t.after(() => { for (const [name, value] of Object.entries(originals)) globalThis[name] = value; });
    const fields = Object.fromEntries(Object.entries({ Latitude: '0', Longitude: '0', LocalTimestamp: '2026-11-08T12:00:00',
        Notes: 'first', CreateKey: 'original-key', CreateIntent: 'owner-bound', AttemptedValues: '', CreateMayHaveEffect: 'false',
        __RequestVerificationToken: 'old-csrf' }).map(([name, value]) => [name, { value }]));
    const status = {}, requests = [], nodes = [];
    let submit, warning, observedOwner = 'other';
    const form = { elements: fields, action: '/User/Location/Create', hidden: false,
        addEventListener: (_, callback) => { submit = callback; }, reportValidity: () => true,
        querySelector: () => status, before: value => { warning = value; } };
    globalThis.FormData = class extends originals.FormData {
        constructor() { super(); for (const [name, field] of Object.entries(fields)) this.set(name, field.value); }
    };
    globalThis.DOMParser = class {
        parseFromString(owner) { return { getElementById: () => ({ dataset: { subjectId: owner } }),
            querySelector: () => ({ value: 'fresh-csrf' }) }; }
    };
    globalThis.document = { getElementById: () => ({ dataset: { subjectId: 'original' } }), createElement: () => {
        const node = { setAttribute() {}, append() {}, remove() { this.removed = true; },
            addEventListener: (_, callback) => { node.click = callback; } };
        nodes.push(node); return node;
    } };
    globalThis.fetch = async (url, options) => {
        requests.push([url, options]);
        return { ok: true, redirected: false, text: async () => observedOwner };
    };
    attachLocationCreate(form, () => fields.Notes.value, () => assert.fail('Another account must not redisplay private values'));
    await submit({ preventDefault() {} });
    const frozen = fields.AttemptedValues.value;
    assert.equal(form.hidden, true);
    assert.equal(JSON.parse(frozen).notes, 'first');
    fields.Notes.value = 'retained newer input';
    const recheck = nodes[1];
    await recheck.click();
    assert.equal(form.hidden, true);
    observedOwner = 'original';
    await recheck.click();
    assert.equal(form.hidden, false);
    assert.equal(warning.removed, true);
    assert.equal(fields.CreateKey.value, 'original-key');
    assert.equal(fields.AttemptedValues.value, frozen);
    assert.equal(fields.Notes.value, 'retained newer input');
    assert.equal(fields.__RequestVerificationToken.value, 'fresh-csrf');
    assert.deepEqual(requests.map(([url]) => url), ['/User/Location/Create', '/User/Location', '/User/Location']);
    assert.equal(requests.filter(([, options]) => options.method === 'POST').length, 1);
});
