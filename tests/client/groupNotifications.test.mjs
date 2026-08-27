import assert from 'node:assert/strict';
import test from 'node:test';
import { createGroupNotificationRefresh, reloadGroupNotificationState } from '../../wwwroot/js/groupNotifications.js';

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

test('a burst during an in-flight reload queues one non-overlapping follow-up', async () => {
    const timers = [];
    const completions = [];
    const calls = [];
    const refresh = createGroupNotificationRefresh({
        schedule: callback => { timers.push(callback); return timers.length; },
        reload: types => {
            calls.push([...types]);
            return new Promise(resolve => completions.push(resolve));
        }
    });

    refresh.accept({ data: '{"type":"invitation-state"}' });
    timers.shift()();
    refresh.accept({ data: '{"type":"membership-state"}' });
    refresh.accept({ data: '{"type":"membership-state"}' });
    assert.equal(timers.length, 0);
    assert.equal(calls.length, 1);

    completions.shift()();
    await Promise.resolve();
    await Promise.resolve();
    assert.equal(timers.length, 1);
    timers.shift()();
    assert.deepEqual(calls, [['invitation-state'], ['membership-state']]);
    completions.shift()();
});

test('dispose aborts an active reload and prevents all later mutation', async () => {
    const timers = [];
    let release;
    const mutations = { badge: 0, storage: 0, alert: 0, event: 0 };
    const entered = Promise.withResolvers();
    const completed = Promise.withResolvers();
    const refresh = createGroupNotificationRefresh({
        schedule: callback => { timers.push(callback); return timers.length; },
        reload: async (_types, signal) => {
            entered.resolve();
            await new Promise(resolve => { release = resolve; });
            if (!signal.aborted) Object.keys(mutations).forEach(key => mutations[key]++);
            completed.resolve();
        }
    });

    refresh.accept({ data: '{"type":"invitation-state"}' });
    timers.shift()();
    await entered.promise;
    refresh.accept({ data: '{"type":"membership-state"}' });

    refresh.dispose();
    release();
    await completed.promise;
    await Promise.resolve();

    assert.deepEqual(mutations, { badge: 0, storage: 0, alert: 0, event: 0 });
    assert.equal(timers.length, 0);
});

test('production reload callback consumes native abort without mutations or another reload', async () => {
    const timers = [];
    const entered = Promise.withResolvers();
    const rejected = Promise.withResolvers();
    const mutations = { dom: 0, storage: 0, alert: 0, event: 0 };
    let fetches = 0;
    const dependencies = {
        fetch: (_url, { signal }) => {
            fetches++;
            entered.resolve(signal);
            return rejected.promise;
        },
        updateInvitesBadge: () => mutations.dom++,
        checkPendingInvitesDiff: () => mutations.alert++,
        dispatchInvitationState: () => mutations.event++,
        checkUserActivityDigest: () => mutations.storage++,
        checkJoinedGroups: () => mutations.dom++,
        updateManagerActivity: () => mutations.dom++
    };
    const refresh = createGroupNotificationRefresh({
        schedule: callback => { timers.push(callback); return timers.length; },
        reload: (types, signal) => reloadGroupNotificationState(types, signal, dependencies)
    });

    refresh.accept({ data: '{"type":"invitation-state"}' });
    timers.shift()();
    const signal = await entered.promise;
    refresh.accept({ data: '{"type":"membership-state"}' });
    refresh.dispose();
    rejected.reject(new DOMException('The operation was aborted.', 'AbortError'));
    await new Promise(resolve => setImmediate(resolve));

    assert.equal(signal.aborted, true);
    assert.equal(fetches, 1);
    assert.deepEqual(mutations, { dom: 0, storage: 0, alert: 0, event: 0 });
    assert.equal(timers.length, 0);
});
