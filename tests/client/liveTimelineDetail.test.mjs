import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

// Bootstrap the production Greek strings and locale for the detail's HTML.
const catalog = await readFile(new URL('../../Resources/Timeline.el-GR.resx', import.meta.url), 'utf8');
const messages = Object.fromEntries(Array.from(catalog.matchAll(/<data name="([^"]+)"[^>]*>\s*<value>([^<]+)<\/value>/g),
    ([, key, value]) => [key, value]));
globalThis.document = {
    getElementById: id => id === 'wayfarer-locale'
        ? { textContent: JSON.stringify({ locale: 'el-GR', fallback: 'en-US', messages, english: {} }) } : null,
    // The fixture has no note images for the inert template to rewrite.
    createElement: () => ({ content: { querySelectorAll: () => [] } })
};
globalThis.window = { location: { pathname: '/User/Timeline', search: '', hash: '' } };
const { renderLiveLocation } = await import('../../wwwroot/js/Areas/User/Timeline/timeline-location.js');

/** Render the details through the same entry point that an owner marker click uses. */
const renderDetail = clocks => renderLiveLocation({
    id: 1, coordinates: { latitude: 37.98, longitude: 23.73 }, timezone: 'Europe/Athens', ...clocks
}, { isLive: false, isLatest: true }, 'America/New_York');

/** Both clock rows must describe the same event in their respective zones. */
const assertClocks = detail => {
    assert.match(detail, /στη ζώνη ώρας σας\):<\/strong>\s*<div>3 Ιουλ 2026, 05:15 \(GMT-4 America\/New_York\)<\/div>/);
    assert.match(detail, /Τοπική ώρα καταγραφής:<\/strong>\s*<div>3 Ιουλ 2026, 12:15 \(GMT\+3 Europe\/Athens\)<\/div>/);
};

test('live Timeline prefers the authoritative event instant for both clock rows', () => {
    assertClocks(renderDetail({ eventUtc: '2026-07-03T09:15:00.123456Z', localTimestamp: '2026-07-03T12:15:00' }));
    assertClocks(renderDetail({ eventUtc: '2026-07-03T09:15:00Z', localTimestamp: '2026-07-03T15:15:00+03:00' }));
});

test('live Timeline accepts an explicit legacy offset when eventUtc is absent or invalid', () => {
    for (const eventUtc of [undefined, '2026-07-03T09:15:00', '2026-02-30T09:15:00Z', 'invalid']) {
        assertClocks(renderDetail({ eventUtc, localTimestamp: '2026-07-03T12:15:00+03:00' }));
    }
});

test('live Timeline keeps the localized unavailable states without a valid instant', () => {
    for (const localTimestamp of ['2026-07-03T12:15:00', '2026-07-03T12:15:00+99:99', undefined]) {
        const detail = renderDetail({ eventUtc: 'invalid', localTimestamp });
        assert.match(detail, /στη ζώνη ώρας σας\):<\/strong>\s*<div>Μη διαθέσιμο<\/div>/);
        assert.match(detail, /Τοπική ώρα καταγραφής:<\/strong>\s*<div class="fst-italic text-muted">Η ζώνη ώρας καταγραφής δεν είναι διαθέσιμη<\/div>/);
    }
});
