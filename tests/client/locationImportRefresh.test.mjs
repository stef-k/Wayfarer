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

    refresh.connect(function FakeEventSource() { return source; }, '/api/sse/import');
    source.onerror();
    refresh.dispose();

    assert.equal(reloads, 0);
    assert.equal(closed, 1);
    assert.equal(source.onmessage, null);
    assert.equal(source.onerror, null);
});

test('dispose cancels an accepted pending reload and a stale callback no-ops', () => {
    const timers = [];
    const cancelled = [];
    let reloads = 0;
    const refresh = createLocationImportRefresh({
        schedule: callback => { timers.push(callback); return 17; },
        cancel: handle => cancelled.push(handle),
        reload: () => reloads++
    });

    refresh.accept({ data: '{"type":"enrichment-state"}' });
    refresh.dispose();
    timers[0]();

    assert.deepEqual(cancelled, [17]);
    assert.equal(reloads, 0);
});

test('dispose after a hint burst cancels the single coalesced reload', () => {
    let schedules = 0;
    let cancellations = 0;
    const refresh = createLocationImportRefresh({
        schedule: () => { schedules++; return 9; },
        cancel: handle => { assert.equal(handle, 9); cancellations++; },
        reload: () => assert.fail('disposed coordinator must not reload')
    });

    refresh.accept({ data: '{"type":"import-state"}' });
    refresh.accept({ data: '{"type":"enrichment-state"}' });
    refresh.dispose();
    refresh.dispose();

    assert.equal(schedules, 1);
    assert.equal(cancellations, 1);
});

test('dispose before a hint and unavailable EventSource remain inert', () => {
    let schedules = 0;
    const refresh = createLocationImportRefresh({
        schedule: () => schedules++, cancel: () => {}, reload: () => {}
    });

    refresh.connect(undefined, '/api/sse/import');
    refresh.dispose();
    refresh.accept({ data: '{"type":"import-state"}' });

    assert.equal(refresh.connected, false);
    assert.equal(schedules, 0);
});
