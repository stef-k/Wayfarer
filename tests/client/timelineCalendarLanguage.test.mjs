import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { assertCalendarDetailClocks } from './fixtures/calendarDetail.mjs';

// Set the actual document locale before loading either owner and its transitive message imports.
const source = await readFile(new URL('../../Resources/Timeline.el-GR.resx', import.meta.url), 'utf8');
const messages = Object.fromEntries(Array.from(source.matchAll(/<data name="([^"]+)"[^>]*>\s*<value>([^<]+)<\/value>/g),
    ([, key, value]) => [key, value]));
globalThis.document = { getElementById: id => id === 'wayfarer-locale'
    ? { textContent: JSON.stringify({ locale: 'el-GR', fallback: 'en-US', messages, english: {} }) } : null };
const { TimelinePage } = await import('../../wwwroot/js/Areas/User/Timeline/calendar-page.js');
const { CalendarIntent, emptyFilters } = await import('../../wwwroot/js/Areas/User/Timeline/calendar-intent.js');
const { selectionSearch } = await import('../../wwwroot/js/Areas/User/Timeline/calendar-scope.js');
const { createCalendarDetail } = await import('../../wwwroot/js/Areas/User/Timeline/calendar-detail.js');

/** A catalog remap rejects mixed observations until an explicit retry from the original filters. */
test('Greek calendar explains how to revalidate retained filters after an Activity catalog change', async () => {
    const selection = { dateType: 'week', year: 2026, month: 10, day: 29, weekStartsOn: 7, timeBasis: 'viewer', viewerZone: 'Europe/Athens' };
    const filters = { ...emptyFilters(), activityIds: [1], q: 'blue door' };
    const context = crypto.randomUUID();
    let slot, url, activityId = 1;
    const intent = new CalendarIntent({ getItem: () => slot, setItem: (_, value) => { slot = value; } }, 'owner');
    const requests = [];
    const owner = new TimelinePage({ subject: 'owner', selection, context, filters, intent, preferences: { value: { rules: {} } },
        replaceUrl: value => { url = value; },
        fetchRead: async (operation, body) => {
            requests.push({ operation, body });
            const selected = operation === 'availability' ? { dateType: 'month', year: body.year, month: body.month, day: 1,
                timeBasis: body.timeBasis, viewerZone: body.viewerZone } : selection;
            return { calendarContract: 'private-calendar/2', subjectId: 'owner', selection: selected,
                scope: { ...filters, timeBasis: body.timeBasis, viewerZone: body.viewerZone, activityIds: [activityId] },
                timeRules: { version: 'time-1' }, matchingRules: { interpretation: 'query-1', textVersion: 'text-1' } };
        } });
    await owner.refresh();
    activityId = 2;
    await owner.openMonth(2026, 10);
    assert.equal(owner.blocked, true);
    assert.equal(owner.state.period, null);
    assert.equal(owner.state.analysis, null);
    assert.equal(owner.state.availability, null);
    assert.deepEqual(owner.selection, selection);
    assert.equal(url, selectionSearch(selection, context));
    assert.deepEqual(intent.restore(selection, context).filters, filters);
    assert.equal(owner.state.errors.identity,
        'Άλλαξαν οι κανόνες αντιστοίχισης. Επιλέξτε «Ανανέωση / Νέα προσπάθεια» για να ελέγξετε ξανά τα φίλτρα που διατηρήθηκαν.');

    await owner.retry();
    const period = requests.findLast(request => request.operation === 'period');
    assert.deepEqual(period.body, { ...selection, q: filters.q, activityIds: '1' });
    assert.equal(owner.state.period.scope.activityIds[0], 2);
    assert.equal(owner.state.errors.identity, undefined);
});

/** Greek detail keeps full precision with the document's decimal separator. */
test('Greek calendar details display exact Event UTC and the two local clocks', t =>
    assertCalendarDetailClocks(t, createCalendarDetail, 'Χρόνος συμβάντος (UTC):', ','));
