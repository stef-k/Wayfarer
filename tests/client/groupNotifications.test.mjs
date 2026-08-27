import assert from 'node:assert/strict';
import test from 'node:test';
import { createGroupNotificationRefresh } from '../../wwwroot/js/groupNotifications.js';

test('exact invitation and membership hints coalesce to one durable reload', () => {
    const timers = [];
    const reloads = [];
    const refresh = createGroupNotificationRefresh({
        schedule: callback => timers.push(callback),
        reload: types => reloads.push([...types].sort())
    });

    refresh.accept({ data: '{"type":"invitation-state"}' });
    refresh.accept({ data: '{"type":"membership-state"}' });
    assert.equal(timers.length, 1);
    timers[0]();
    assert.deepEqual(reloads, [['invitation-state', 'membership-state']]);
});

test('malformed additional-field unrelated and content-bearing events are ignored', () => {
    let scheduled = 0;
    const refresh = createGroupNotificationRefresh({ schedule: () => scheduled++, reload: () => {} });

    for (const data of ['bad', '[]', '{}', '{"type":"other"}',
        '{"type":"invitation-state","groupId":"private"}',
        '{"type":"membership-state","action":"removed"}']) refresh.accept({ data });

    assert.equal(scheduled, 0);
});

test('connection uses only the protected server-owned URL', () => {
    let url = null;
    const source = { close() {} };
    const refresh = createGroupNotificationRefresh({ reload: () => {} });

    refresh.connect(function FakeEventSource(value) { url = value; return source; });

    assert.equal(url, '/api/sse/group-notifications');
    assert.equal(refresh.connected, true);
});

test('dispose closes listeners and cancels a pending callback', () => {
    const timers = [];
    const cancelled = [];
    let closed = 0;
    let reloads = 0;
    const source = { close: () => closed++ };
    const refresh = createGroupNotificationRefresh({
        schedule: callback => { timers.push(callback); return 7; },
        cancel: handle => cancelled.push(handle),
        reload: () => reloads++
    });
    refresh.connect(function FakeEventSource() { return source; });
    source.onmessage({ data: '{"type":"invitation-state"}' });

    refresh.dispose();
    timers[0]();

    assert.deepEqual(cancelled, [7]);
    assert.equal(closed, 1);
    assert.equal(source.onmessage, null);
    assert.equal(source.onerror, null);
    assert.equal(reloads, 0);
});
