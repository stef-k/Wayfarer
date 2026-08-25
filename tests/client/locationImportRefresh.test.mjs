import assert from 'node:assert/strict';
import test from 'node:test';
import { createLocationImportRefresh } from '../../wwwroot/js/Areas/User/LocationImport/Refresh.js';

test('allowed import and enrichment hints coalesce to one relational reload', () => {
    const timers = [];
    let reloads = 0;
    const refresh = createLocationImportRefresh({
        schedule: callback => timers.push(callback),
        reload: () => reloads++
    });

    refresh.accept({ data: '{"type":"import-state"}' });
    refresh.accept({ data: '{"type":"enrichment-state"}' });
    assert.equal(timers.length, 1);
    timers[0]();
    assert.equal(reloads, 1);
});

test('malformed unrelated and content-bearing events are ignored', () => {
    let scheduled = 0;
    const refresh = createLocationImportRefresh({ schedule: () => scheduled++, reload: () => {} });

    refresh.accept({ data: 'not-json' });
    refresh.accept({ data: '{"type":"location-state"}' });
    refresh.accept({ data: '{"type":"enrichment-state","address":"private"}' });
    refresh.accept({ data: '{"type":"import-state","userId":"other"}' });
    assert.equal(scheduled, 0);
});

test('missing EventSource leaves page initialization usable', () => {
    const refresh = createLocationImportRefresh({ schedule: () => {}, reload: () => {} });

    assert.doesNotThrow(() => refresh.connect(undefined, '/api/sse/import'));
    assert.equal(refresh.connected, false);
});

test('stream errors and disposal do not reload or retain listeners', () => {
    let reloads = 0;
    let closed = 0;
    const source = { close: () => closed++ };
    const refresh = createLocationImportRefresh({ schedule: () => {}, reload: () => reloads++ });

    refresh.connect(() => source, '/api/sse/import');
    source.onerror();
    refresh.dispose();

    assert.equal(reloads, 0);
    assert.equal(closed, 1);
    assert.equal(source.onmessage, null);
    assert.equal(source.onerror, null);
});
