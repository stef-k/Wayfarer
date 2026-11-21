import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

/** Supplies the same catalog values as the Razor bootstrap for the public Timeline renderer. */
const greekBundle = async () => {
    const sources = await Promise.all(['PublicTimeline', 'Shell'].map(owner =>
        readFile(new URL(`../../Resources/${owner}.el-GR.resx`, import.meta.url), 'utf8')));
    const messages = Object.fromEntries(sources.flatMap(source =>
        Array.from(source.matchAll(/<data name="([^"]+)"[^>]*>\s*<value>([^<]+)<\/value>/g),
            ([, key, value]) => [key, value])));
    return { locale: 'el-GR', fallback: 'en-US', messages, english: {} };
};

test('Timeline renders Greek browser feedback and detail while retaining timestamps and authored data', async () => {
    const bundle = await greekBundle();
    const originalDocument = globalThis.document;
    globalThis.document = {
        getElementById: id => id === 'wayfarer-locale' ? { textContent: JSON.stringify(bundle) } : null
    };

    try {
        const { renderTimelineLocation } = await import('../../wwwroot/js/Areas/Public/UsersTimeline/timelineDetail.js');
        const { formatTimelineSummary, readTimelineLocations } =
            await import('../../wwwroot/js/Areas/Public/UsersTimeline/timelineFeedback.js');
        const location = {
            localTimestamp: '2026-07-03T12:15:00Z',
            timezone: 'Europe/Athens',
            coordinates: { latitude: 37.987654, longitude: 23.1 },
            accuracy: 1.25,
            activityType: 'Walk <Sunday>',
            fullAddress: 'Author & "Provider"'
        };
        const detail = renderTimelineLocation(location, { isLive: false, isLatest: true }, 'America/New_York');
        assert.match(detail, /ΤΕΛΕΥΤΑΙΑ ΤΟΠΟΘΕΣΙΑ/);
        assert.match(detail, /Τοπική ώρα καταγραφής:/);
        assert.match(detail, /08:15.*GMT-4.*America\/New_York/);
        assert.match(detail, /15:15.*GMT\+3.*Europe\/Athens/);
        assert.match(detail, /37,987654/);
        assert.match(detail, /1,25 μ\./);
        assert.match(detail, /Walk &lt;Sunday&gt;/);
        assert.match(detail, /Author &amp; &quot;Provider&quot;/);
        assert.match(detail, /37\.987654%2C23\.100000/);
        assert.match(detail, /Αναζήτηση στη Βικιπαίδεια/);
        assert.match(formatTimelineSummary({ totalLocations: 1234, fromDate: location.localTimestamp }, 'UTC'),
            /Σύνολο καταγραφών τοποθεσίας: 1\.234 \| Από: 3 Ιουλ 2026/);

        const rendered = [];
        const render = rows => rendered.push(rows);
        const empty = await readTimelineLocations(Response.json({ success: true, data: [] }), render);
        assert.equal(empty, 'Δεν υπάρχουν δημόσιες τοποθεσίες σε αυτή την προβολή του χάρτη.');
        const unavailable = await readTimelineLocations(new Response('legacy body', { status: 404 }), render);
        assert.equal(unavailable, 'Το χρονολόγιο δεν είναι διαθέσιμο.');
        await assert.rejects(readTimelineLocations(Response.json({ success: false, data: 'legacy body' }), render),
            /Timeline locations unavailable/);
        assert.deepEqual(rendered, [[], []]);
    } finally {
        globalThis.document = originalDocument;
    }
});
