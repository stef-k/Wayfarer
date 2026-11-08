import assert from 'node:assert/strict';
import test from 'node:test';
import { createCalendarPicker } from '../../wwwroot/js/Areas/User/Timeline/calendar-picker.js';
import { TimelinePage } from '../../wwwroot/js/Areas/User/Timeline/calendar-page.js';
import { CalendarIntent, emptyFilters, filterBody, viewportArea } from '../../wwwroot/js/Areas/User/Timeline/calendar-intent.js';
import { CalendarPreferences, defaultRules, ruleDisplay } from '../../wwwroot/js/Areas/User/Timeline/calendar-preferences.js';
import { civil, isoDate, moveSelection, periodBounds, preset, readSelection, selectionSearch, validateSelection } from '../../wwwroot/js/Areas/User/Timeline/calendar-scope.js';
const selection = () => ({ dateType: 'day', year: 2026, month: 5, day: 17, timeBasis: 'viewer', viewerZone: 'Europe/Athens' });
const memory = () => { const values = new Map(); return { getItem: key => values.get(key) ?? null, setItem: (key, value) => values.set(key, value), removeItem: key => values.delete(key) }; };
const deferred = () => { let resolve; const promise = new Promise(done => { resolve = done; }); return { promise, resolve }; };
const observation = (operation, body) => {
    const selected = operation === 'availability' ? { dateType: 'month', year: body.year, month: body.month, day: 1, timeBasis: body.timeBasis, viewerZone: body.viewerZone }
        : Object.fromEntries(['dateType', 'year', 'month', 'day', 'weekStartsOn', 'fromDate', 'throughDate', 'timeBasis', 'viewerZone'].filter(key => body[key] !== undefined).map(key => [key, body[key]]));
    const bounds = periodBounds(selected);
    return { contract: operation === 'analysis' ? 'stored-history-connections/1' : 'private-calendar/2',
        ...(operation === 'analysis' ? { calendarContract: 'private-calendar/2' } : {}), subjectId: 'owner', selection: selected,
        scope: { timeBasis: body.timeBasis, viewerZone: body.viewerZone, timeMode: 'days', fromDate: isoDate(bounds.from), throughDate: isoDate(bounds.through),
            q: body.q || '', activityIds: body.activityIds ? String(body.activityIds).split(',').map(Number) : body.includeUnassigned ? [] : null,
            includeUnassigned: body.includeUnassigned === 'true', area: body.west !== undefined ? { west: body.west, south: body.south, east: body.east, north: body.north } : null },
        timeRules: { version: 'time-1', tzdb: '2026' }, matchingRules: { interpretation: 'query-1', textVersion: body.q ? 'text-1' : null },
        observationId: crypto.randomUUID(), asOfUtc: '2026-05-17T12:00:00Z', items: [], nextContinuation: null,
        navigation: { today: '2026-05-17' }, geometry: { state: body.includeGeometry ? 'available' : 'off', islands: [] }, presentDates: [], hasUnresolvedCandidates: false };
};
const setup = (fetchRead = async (operation, body) => observation(operation, body), session = memory(), pageOptions = {}) => {
    const preferences = new CalendarPreferences(memory(), 'owner', 'https://test');
    const intent = new CalendarIntent(session, 'owner'); const urls = [];
    const owner = new TimelinePage({ subject: 'owner', selection: selection(), context: null, filters: emptyFilters(), intent,
        preferences, maximumOffset: 57600, fetchRead, replaceUrl: url => urls.push(url), ...pageOptions });
    return { owner, preferences, intent, session, urls };
};

test('civil anchors clamp before construction, Week is frozen, Custom arrows preserve inclusive length', () => {
    let state = { ...selection(), month: 1, day: 31 };
    state = moveSelection(state, 'month', 1); assert.equal(state.day, 28);
    state = moveSelection(state, 'month', 1); assert.equal(state.month, 3); assert.equal(state.day, 28);
    assert.equal(preset(preset(selection(), 'year'), 'day').day, 17);
    const week = { ...selection(), dateType: 'week', year: 2026, month: 12, day: 31, weekStartsOn: 1 };
    assert.deepEqual(periodBounds(week), { from: civil(2026, 12, 28), through: civil(2027, 1, 3) });
    assert.equal(readSelection(selectionSearch(week), 'capture', 'UTC', 57600, new Date('2027-01-01T12:00Z')).selection.weekStartsOn, 1);
    const custom = { dateType: 'custom', fromDate: '2026-05-04', throughDate: '2026-05-17', timeBasis: 'viewer', viewerZone: 'Europe/Athens' };
    assert.equal(moveSelection(custom, 'custom', -1, true).fromDate, '2026-04-20');
    assert.equal(moveSelection(custom, 'custom', 1, true).throughDate, '2026-05-31');
    assert.equal(moveSelection(custom, 'month', 1).dateType, 'day');
    assert.throws(() => validateSelection({ ...custom, throughDate: '2026-05-03' }, 57600));
    assert.throws(() => readSelection('?viewType=week&year=2026&month=5&day=17', 'capture', 'UTC', 57600));
});

test('horizon is start-based for all periods; future Through is admitted without clipping', () => {
    const now = new Date('2026-05-17T12:00:00Z');
    const custom = { dateType: 'custom', fromDate: '2026-05-17', throughDate: '2026-06-30', timeBasis: 'viewer', viewerZone: 'UTC' };
    assert.equal(validateSelection(custom, 57600, now).throughDate, '2026-06-30');
    assert.throws(() => validateSelection({ ...custom, fromDate: '2026-05-18' }, 57600, now));
    assert.throws(() => periodBounds({ ...selection(), year: 1900, month: 1, day: 1, dateType: 'week', weekStartsOn: 7 }));
    assert.throws(() => readSelection('?viewType=day&year=2026&month=5&day=17&q=secret', 'viewer', 'UTC', 57600, now));
});

test('one subject-bound restoration slot restores matching contexts and never adopts filters from date-only links', () => {
    const { intent, session } = setup(); const context = crypto.randomUUID();
    const filters = { ...emptyFilters(), q: 'blue door' };
    intent.save(selection(), context, filters, { selectedId: 42, path: true, camera: { latitude: 0, longitude: 0, zoom: 4 } });
    assert.equal(intent.restore(selection(), context).filters.q, 'blue door');
    assert.equal(intent.restore(selection(), context).returnState.selectedId, 42);
    assert.equal(intent.restore(selection(), null).filters.q, '');
    assert.equal(intent.restore({ ...selection(), day: 18 }, context).returnState, undefined);
    assert.throws(() => intent.restore(selection(), crypto.randomUUID()), /unavailable/);
    const other = new CalendarIntent(session, 'other'); assert.throws(() => other.restore(selection(), context), /unavailable/);
    assert.throws(() => intent.restore(selection(), context), /unavailable/);
});

test('filter persistence failure retains applied filters, URL and draft; Clear is immediate and closes only filters', async () => {
    const store = memory(); const { owner, urls } = setup(undefined, store);
    owner.openEditor('filters', { ...emptyFilters(), q: 'new private value' });
    store.setItem = () => { throw new Error(); };
    assert.throws(() => owner.commitFilters(owner.editor.draft), /retained/);
    assert.equal(owner.filters.q, ''); assert.equal(owner.editor.draft.q, 'new private value'); assert.equal(urls.length, 0);
    const other = setup().owner;
    await other.commitFilters({ ...emptyFilters(), q: 'private' });
    other.openEditor('filters', { ...emptyFilters(), q: 'unfinished' });
    await other.clearFilters(); assert.equal(other.filters.q, ''); assert.equal(other.editor, null); assert.equal(other.context, null);
});

test('core reads are serial and summary still runs after period failure before optional work', async () => {
    let active = 0, maximum = 0; const order = [];
    const { owner } = setup(async (operation, body) => {
        active++; maximum = Math.max(maximum, active); order.push(operation); await Promise.resolve(); active--;
        if (operation === 'period') throw Object.assign(new Error(), { code: 'historyPreparationRequired' });
        return observation(operation, body);
    });
    const core = owner.refresh(); owner.openRecords(); owner.openMonth(2026, 5); await core; await owner.tail;
    assert.deepEqual(order, ['period', 'analysis', 'records', 'availability']); assert.equal(maximum, 1);
    assert.equal(owner.state.status.analysis, 'current'); assert.equal(owner.state.status.period, 'failed'); assert.ok(owner.identity);
});

test('Clear fences a late filtered period, records and month publication even when abort is ignored', async () => {
    const first = deferred(), reached = deferred(); let waiting = true;
    const { owner } = setup(async (operation, body) => {
        if (operation === 'period' && waiting) { waiting = false; reached.resolve(); await first.promise; }
        return observation(operation, body);
    });
    owner.filters.q = 'secret'; owner.refresh(); owner.openRecords(); owner.openMonth(2026, 5); await reached.promise;
    const cleared = owner.clearFilters(); first.resolve(); await cleared; await owner.tail;
    assert.equal(owner.state.period.scope.q, ''); assert.equal(owner.state.analysis.scope.q, '');
    assert.equal(owner.state.availability.scope.q, ''); assert.equal(owner.state.records.length, 0);
});

test('month closure and replacement fence marks, while rule changes preserve record traversal', async () => {
    const wait = deferred(), entered = deferred();
    const { owner } = setup(async (operation, body) => {
        if (operation === 'availability' && body.month === 5) { entered.resolve(); await wait.promise; }
        const result = observation(operation, body); result.presentDates = [`2026-${body.month}-01`]; return result;
    });
    owner.openMonth(2026, 5); await entered.promise;
    const june = owner.openMonth(2026, 6); wait.resolve(); await june;
    assert.equal(owner.state.availability.selection.month, 6);
    owner.closeMonth(); assert.equal(owner.state.availability, null);
    owner.state.records = [{ locationId: 42 }]; owner.state.nextContinuation = 'anchor';
    owner.preferences.openDraft(); await owner.setRules();
    assert.equal(owner.state.records[0].locationId, 42); assert.equal(owner.state.nextContinuation, 'anchor');
});

test('unknown writes retain their separate drafts and absent read pages grant no deletion authority', async () => {
    const protectedDraft = Object.freeze({ original: 'before', desired: 'after', attempt: 'unknown' });
    const { owner } = setup(); owner.camera = { latitude: 1, longitude: 2, zoom: 5 }; owner.selectedId = 42;
    owner.openEditor('date', { fromDate: '2026-05-01', throughDate: '' });
    assert.throws(() => owner.commitSelection({ ...selection(), day: 18 }), /Apply or Cancel/);
    assert.throws(() => owner.openEditor('filters', {}), /Apply or Cancel/);
    await owner.refresh();
    assert.equal(owner.editor.draft.throughDate, ''); assert.equal(owner.selectedId, 42); assert.equal(owner.camera.zoom, 5);
    assert.equal(protectedDraft.attempt, 'unknown'); assert.equal(owner.state.records.length, 0);
});

test('canonical activity redirects bind subsequent reads; a conflicting interpretation invalidates the whole group', async () => {
    const bodies = []; let mismatch = false;
    const { owner } = setup(async (operation, body) => {
        bodies.push(body); const result = observation(operation, body);
        result.scope.activityIds = [2];
        if (mismatch) result.matchingRules.interpretation = 'changed';
        return result;
    });
    owner.filters.activityIds = [1]; await owner.refresh();
    assert.equal(bodies[1].activityIds, '2'); assert.match(owner.state.activityNotice, /resolved/);
    mismatch = true; await owner.openMonth(2026, 5);
    assert.equal(owner.state.period, null); assert.equal(owner.state.analysis, null); assert.equal(owner.blocked, true);
});

test('foreign subject clears page data and its tab slot without touching preference or mutation storage', async () => {
    const { owner, intent } = setup(async (operation, body) => ({ ...observation(operation, body), subjectId: 'other' }));
    intent.save(selection(), null, emptyFilters(), {}); owner.state.records = [{ locationId: 1 }];
    await owner.refresh(); assert.equal(owner.blocked, true); assert.deepEqual(owner.state.records, []);
    assert.match(owner.state.errors.session, /Session/); assert.equal(owner.filters.q, '');
});

test('untouched rounded connection controls stay exact; explicit edits convert once and failed saves retain drafts', () => {
    const storage = memory(); const prefs = new CalendarPreferences(storage, 'owner', 'https://test');
    prefs.value.rules.maximumGapSeconds = 7200.123456; prefs.value.rules.maximumSpeedMetresPerSecond = 350.123456;
    prefs.openDraft(); assert.equal(prefs.draft.gap, '2.00');
    prefs.changeUnit('minutes'); assert.equal(prefs.rulesFromDraft().maximumGapSeconds, 7200.123456);
    prefs.draft.speed = '900'; prefs.draft.speedEdited = true;
    assert.equal(prefs.rulesFromDraft().maximumSpeedMetresPerSecond, 250);
    prefs.draft.gap = '90'; prefs.draft.gapEdited = true;
    assert.equal(prefs.rulesFromDraft().maximumGapSeconds, 5400);
    storage.setItem = () => { throw new Error(); };
    assert.throws(() => prefs.applyDraft(), /retained/); assert.equal(prefs.value.rules.maximumGapSeconds, 7200.123456); assert.equal(prefs.draft.gap, '90');
    prefs.resetDraft(); assert.equal(prefs.rulesFromDraft().maximumGapSeconds, 7200); assert.equal(prefs.value.rules.maximumGapSeconds, 7200.123456);
    prefs.draft.gap = '1e-999'; prefs.draft.gapEdited = true; assert.throws(() => prefs.rulesFromDraft());
    assert.doesNotMatch(ruleDisplay(Number.MAX_VALUE, 1 / 3.6), /Infinity/);
});

test('viewport normalization captures one finite rectangle, including wrapped and world views', () => {
    assert.deepEqual(viewportArea({ west: 170, east: 200, south: -100, north: 100 }), { west: 170, east: -160, south: -90, north: 90 });
    assert.deepEqual(viewportArea({ west: -500, east: 500, south: -10, north: 10 }), { west: -180, east: 180, south: -10, north: 10 });
    assert.throws(() => viewportArea({ west: NaN, east: 10, south: 0, north: 10 }));
    assert.throws(() => filterBody({ ...emptyFilters(), q: '😀'.repeat(257) }));
    assert.deepEqual(filterBody({ ...emptyFilters(), includeUnassigned: true }), { includeUnassigned: 'true' });
});


// Retention is shared by pagehide, camera, panel, scroll, detail and path/rule callers.
for (const loss of ['missing', 'mismatched', 'account-ended']) test(`${loss} context survives repeated retention/reload until explicit recovery`, async () => {
    const context = crypto.randomUUID(), session = memory(); let reads = 0;
    const fetchRead = async (operation, body) => { reads++; return observation(operation, body); };
    const options = { context, restorationPending: loss !== 'account-ended' };
    let { owner, intent } = setup(fetchRead, session, options);
    if (loss !== 'missing') intent.save(selection(), loss === 'account-ended' ? context : crypto.randomUUID(), { ...emptyFilters(), q: 'private' }, {});
    if (loss === 'account-ended') owner.sessionEnded();
    for (let reload = 0; reload < 2; reload++) {
        owner.retain(); owner.camera = { latitude: 1, longitude: 2, zoom: 5 };
        await owner.openRecords(); await owner.setPath(true); owner.retain();
        assert.throws(() => intent.restore(selection(), context), /unavailable/);
        assert.throws(() => owner.retry(), /restor|Sign in/);
        assert.equal(reads, 0);
        if (loss !== 'account-ended') ({ owner, intent } = setup(fetchRead, session, options));
    }
    if (loss === 'account-ended') {
        assert.throws(() => owner.clearFilters(), /Sign in/); return;
    }
    await owner.refresh(); await owner.tail; assert.equal(reads, 0);
    owner.openEditor('filters', { ...emptyFilters(), q: 'reentered' });
    const save = session.setItem; session.setItem = () => { throw new Error('storage denied'); };
    assert.throws(() => owner.commitFilters(owner.editor.draft), /retained/);
    assert.equal(owner.editor.draft.q, 'reentered'); assert.equal(owner.restorationPending, true);
    owner.retain(); assert.equal(reads, 0); assert.throws(() => intent.restore(selection(), context), /unavailable/);
    session.setItem = save;
    if (loss === 'missing') await owner.clearFilters(); else await owner.commitFilters(owner.editor.draft);
    assert.ok(reads > 0); assert.equal(owner.restorationPending, false);
    owner.selectedId = 42; owner.camera = { latitude: 1, longitude: 2, zoom: 5 }; owner.retain();
    const restored = new CalendarIntent(session, 'owner').restore(selection(), owner.context);
    assert.equal(restored.filters.q, loss === 'missing' ? '' : 'reentered');
    assert.equal(restored.returnState.selectedId, 42); assert.deepEqual(restored.returnState.camera, owner.camera);
});

test('failed filter write/read-back retains old applied intent and new draft without broadening reads', async () => {
    const { owner, session, intent } = setup();
    await owner.commitFilters({ ...emptyFilters(), q: 'old' }); const context = owner.context;
    owner.openEditor('filters', { ...emptyFilters(), q: 'new' });
    session.setItem = () => {}; // Simulate denied durability without overwriting the old slot.
    assert.throws(() => owner.commitFilters(owner.editor.draft), /retained/);
    await owner.refresh(); owner.retain();
    assert.equal(owner.filters.q, 'old'); assert.equal(owner.editor.draft.q, 'new');
    assert.equal(owner.state.period.scope.q, 'old'); assert.equal(intent.restore(selection(), context).filters.q, 'old');
});

// Exercise real replacement acquisition, continuation and stale success/failure on the serial owner.
for (const change of ['refresh', 'date', 'filters']) test(`pending Records ${change} restarts first page and fences stale traversal`, async () => {
    const entered = deferred(), release = deferred(), calls = []; let hold = true;
    const { owner } = setup(async (operation, body) => {
        calls.push({ operation, ...body });
        if (operation === 'records' && hold && (change !== 'refresh' || body.continuation)) {
            hold = false; entered.resolve(); await release.promise;
            if (change === 'refresh') throw new Error('obsolete Load more failed');
            return { ...observation(operation, body), items: [{ locationId: 999 }], nextContinuation: 'obsolete' };
        }
        return { ...observation(operation, body), items: [{ locationId: body.day }], nextContinuation: 'next' };
    });
    owner.panel = 'records'; owner.selectedId = 42;
    const initial = owner.refresh();
    if (change === 'refresh') { await initial; owner.loadRecords(); }
    await entered.promise;
    const replaced = change === 'date' ? owner.commitSelection({ ...selection(), day: 18 })
        : change === 'filters' ? owner.commitFilters({ ...emptyFilters(), q: 'new' }) : owner.refresh();
    release.resolve(); await replaced; await owner.tail;
    const recordCalls = calls.filter(call => call.operation === 'records');
    assert.equal(recordCalls.at(-1).continuation, undefined);
    assert.equal(owner.state.status.records, 'current'); assert.equal(owner.state.errors.records, undefined);
    assert.deepEqual(owner.state.records, [{ locationId: change === 'date' ? 18 : 17 }]);
    assert.equal(owner.selectedId, 42); assert.equal(owner.state.nextContinuation, 'next');
    await owner.loadRecords(); assert.equal(calls.at(-1).continuation, 'next');
    assert.equal(owner.state.records.length, 1, 'continuation deduplicates exact IDs');
});

// Bind the actual picker to the same small DOM boundary used by plain client renderer tests.
test('open picker renders invalidation, replacement failure and late old-month fences while retaining focus/endpoints', async t => {
    const previous = { document: globalThis.document, bootstrap: globalThis.bootstrap };
    const elements = new Map();
    const node = () => ({ children: [], dataset: {}, style: {}, attributes: {}, listeners: {}, value: '',
        addEventListener(type, callback) { this.listeners[type] = callback; },
        setAttribute(key, value) { this.attributes[key] = value; },
        append(child) { this.children.push(child); }, replaceChildren() { this.children = []; },
        contains(target) { return this.children.some(child => child === target || child.contains(target)); },
        querySelectorAll() { return this.children.flatMap(child => [child, ...child.querySelectorAll()]).filter(child => child.dataset.date); },
        querySelector(selector) { return this.querySelectorAll().find(child => selector === '[tabindex="0"]' ? child.tabIndex === 0 : selector.includes(child.dataset.date)); },
        focus() { document.activeElement = this; this.listeners.focus?.(); } });
    globalThis.document = { activeElement: null, createElement: node,
        getElementById: id => { if (!elements.has(id)) elements.set(id, node()); return elements.get(id); } };
    globalThis.bootstrap = { Modal: { getOrCreateInstance: () => ({ show() {} }), getInstance: () => ({ hide() {} }) } };
    t.after(() => Object.assign(globalThis, previous));
    let pending = null, fail = false; const calls = [];
    const { owner } = setup(async (operation, body) => {
        calls.push(operation);
        if (operation === 'availability' && pending) { const wait = pending; pending = null; wait.entered.resolve(); await wait.release.promise; }
        if (operation === 'availability' && fail) throw new Error('month unavailable');
        return { ...observation(operation, body), displayedMonth: `2026-0${body.month}`, presentDates: [`2026-0${body.month}-17`] };
    });
    const picker = createCalendarPicker(owner); owner.publish = () => picker.render();
    picker.open(true); await owner.tail;
    const grid = elements.get('calendarGrid'), caption = elements.get('monthStatus');
    const day = () => grid.querySelector('[data-date="2026-05-17"]');
    assert.match(day().textContent, /●/); assert.match(caption.textContent, /observed/);
    const editor = owner.editor; elements.get('rangeThrough').value = '2026-05-25';
    elements.get('rangeThrough').listeners.input(); day().focus();
    const entered = deferred(), release = deferred(); pending = { entered, release };
    const refreshed = owner.refresh();
    assert.match(caption.textContent, /Loading/); assert.match(day().textContent, /\?/);
    assert.equal(document.activeElement.dataset.date, '2026-05-17');
    await entered.promise; fail = true; release.resolve(); await refreshed;
    assert.match(caption.textContent, /unavailable/); assert.doesNotMatch(day().textContent, /●|○/);
    assert.equal(day().attributes['aria-disabled'], undefined); assert.equal(owner.editor, editor);
    assert.equal(elements.get('rangeThrough').value, '2026-05-25');
    assert.equal(owner.editor.draft.throughDate, '2026-05-25');
    fail = false; await owner.refresh(); assert.match(day().textContent, /●/);
    const oldEntered = deferred(), oldRelease = deferred(); pending = { entered: oldEntered, release: oldRelease };
    const old = owner.refresh(); await oldEntered.promise;
    elements.get('nextPickerMonth').listeners.click(); oldRelease.resolve(); await old; await owner.tail;
    assert.match(caption.textContent, /2026-06/); assert.match(grid.querySelector('[data-date="2026-06-17"]').textContent, /●/);
    const closedEntered = deferred(), closedRelease = deferred(); pending = { entered: closedEntered, release: closedRelease };
    const closing = owner.refresh(); await closedEntered.promise;
    elements.get('dateDialog').listeners['hidden.bs.modal'](); closedRelease.resolve(); await closing;
    assert.equal(owner.month, null); assert.equal(owner.state.availability, null); assert.equal(owner.editor, null);
    assert.deepEqual(calls.slice(1, 4), ['period', 'analysis', 'availability']);
});
