import test from 'node:test';
import assert from 'node:assert/strict';
import { createValues } from '../../wwwroot/js/util/location-create.js';

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
