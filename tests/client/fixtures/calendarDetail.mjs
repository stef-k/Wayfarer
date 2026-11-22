import assert from 'node:assert/strict';

/** Opens records through the real detail owner and checks the three rendered clock rows. */
export const assertCalendarDetailClocks = async (t, createCalendarDetail, eventLabel, decimal) => {
    const previous = { document: globalThis.document, bootstrap: globalThis.bootstrap, window: globalThis.window, fetch: globalThis.fetch };
    t.after(() => Object.assign(globalThis, previous));
    const button = { addEventListener() {} };
    const modal = { addEventListener() {}, classList: { contains: () => false } };
    const content = { addEventListener() {}, querySelector: selector => selector === '#locateRecord' ? null : button };
    globalThis.document = {
        getElementById: id => id === 'locationModal' ? modal : id === 'modalContent' ? content : null,
        querySelector: () => content,
        createElement: () => ({ content: { querySelectorAll: () => [] } })
    };
    globalThis.window = { location: { pathname: '/User/Timeline/Chronological', search: '', hash: '' } };
    globalThis.bootstrap = { Modal: { getOrCreateInstance: () => ({ show() {} }) } };

    const observedAtUtc = '2026-07-03T13:00:00Z';
    const fractions = ['123456', '654321', '000120'];
    const owner = { subject: 'owner', selection: { viewerZone: 'America/New_York' }, epoch: 0, detailVersion: 0, retain() {} };
    const detail = createCalendarDetail(owner, { select() {} });
    for (const [index, fraction] of fractions.entries()) {
        const id = index + 1;
        const captureTime = { status: 'available', local: `2026-07-03T15:15:00.${fraction}`, zoneId: 'Europe/Athens', offsetSeconds: 10800 };
        const viewerTime = { status: 'available', local: `2026-07-03T08:15:00.${fraction}`, zoneId: 'America/New_York', offsetSeconds: -14400 };
        globalThis.fetch = async () => Response.json({ subjectId: owner.subject, observedAtUtc,
            record: { locationId: id, eventUtc: `2026-07-03T12:15:00.${fraction}Z`, captureTime, viewerTime } });
        await detail.open(id);
        const event = content.innerHTML?.split(`<strong>${eventLabel}</strong> `)[1]?.split('</p>')[0];
        assert.equal(event?.match(/12:15:00[.,]\d+/)?.[0], `12:15:00${decimal}${fraction}`, event ?? content.textContent);
        assert.match(event, / UTC$/);
        assert.ok(content.innerHTML.includes(`15:15:00${decimal}${fraction} · Europe/Athens · UTC+03:00`));
        assert.ok(content.innerHTML.includes(`08:15:00${decimal}${fraction} · America/New_York · UTC−04:00`));
    }
};
