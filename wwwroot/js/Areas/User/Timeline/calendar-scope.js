/** Gregorian civil selection and URL adapter. UTC Date is used only as a civil arithmetic carrier. */
const pad = number => String(number).padStart(2, '0');
export const isoDate = date => `${date.year}-${pad(date.month)}-${pad(date.day)}`;
export const civil = (year, month, day) => {
    const value = new Date(Date.UTC(year, month - 1, day));
    if (!Number.isInteger(year) || year < 1900 || year > 2100 || value.getUTCFullYear() !== year
        || value.getUTCMonth() + 1 !== month || value.getUTCDate() !== day) throw new Error('Choose a valid date from 1900 through 2100.');
    return { year, month, day };
};
export const parseCivil = value => {
    if (!/^\d{4}-\d\d-\d\d$/.test(value)) throw new Error('Enter an exact date.');
    return civil(...value.split('-').map(Number));
};
/** Civil successors never add an elapsed-day duration in the selected time zone. */
export const shiftDays = (date, days) => {
    const value = new Date(Date.UTC(date.year, date.month - 1, date.day));
    value.setUTCDate(value.getUTCDate() + days);
    return civil(value.getUTCFullYear(), value.getUTCMonth() + 1, value.getUTCDate());
};
export const shiftMonth = (date, months) => {
    const first = new Date(Date.UTC(date.year, date.month - 1, 1));
    first.setUTCMonth(first.getUTCMonth() + months);
    const year = first.getUTCFullYear(), month = first.getUTCMonth() + 1;
    const maximum = new Date(Date.UTC(year, month, 0)).getUTCDate();
    return civil(year, month, Math.min(date.day, maximum));
};
export const weekday = date => (new Date(Date.UTC(date.year, date.month - 1, date.day)).getUTCDay() + 6) % 7 + 1;
/** Feature-detected formatting locale; a restored Week carries its own frozen value. */
export const regionalWeekStart = () => {
    try {
        const locale = new Intl.Locale(Intl.DateTimeFormat().resolvedOptions().locale);
        const day = locale.getWeekInfo?.().firstDay;
        if (Number.isInteger(day) && day >= 1 && day <= 7) return { day, fallback: false };
    } catch { /* Older browsers keep the labelled Monday fallback. */ }
    return { day: 1, fallback: true };
};
export const todayInZone = (zone, now = new Date()) => {
    const parts = Object.fromEntries(new Intl.DateTimeFormat('en-US', { timeZone: zone, year: 'numeric', month: '2-digit', day: '2-digit' })
        .formatToParts(now).map(part => [part.type, part.value]));
    return civil(Number(parts.year), Number(parts.month), Number(parts.day));
};
export const horizonDate = (selection, maximumOffset, now = new Date()) => selection.timeBasis === 'viewer'
    ? isoDate(todayInZone(selection.viewerZone, now)) : new Date(now.getTime() + maximumOffset * 1000).toISOString().slice(0, 10);
/** Inclusive bounds and the retained preset anchor are separate identities. */
export const periodBounds = selection => {
    if (selection.dateType === 'custom') return { from: parseCivil(selection.fromDate), through: parseCivil(selection.throughDate) };
    const anchor = civil(selection.year, selection.month, selection.day);
    if (selection.dateType === 'day') return { from: anchor, through: anchor };
    if (selection.dateType === 'week') {
        if (!Number.isInteger(selection.weekStartsOn) || selection.weekStartsOn < 1 || selection.weekStartsOn > 7) throw new Error('This Week link needs its frozen week start.');
        const from = shiftDays(anchor, -(weekday(anchor) - selection.weekStartsOn + 7) % 7);
        return { from, through: shiftDays(from, 6) };
    }
    const from = civil(anchor.year, selection.dateType === 'year' ? 1 : anchor.month, 1);
    const lastMonth = selection.dateType === 'year' ? 12 : anchor.month;
    return { from, through: civil(anchor.year, lastMonth, new Date(Date.UTC(anchor.year, lastMonth, 0)).getUTCDate()) };
};
export const validateSelection = (selection, maximumOffset, now) => {
    if (!['day', 'week', 'month', 'year', 'custom'].includes(selection.dateType)
        || !['capture', 'viewer'].includes(selection.timeBasis)) throw new Error('Invalid Timeline selection.');
    todayInZone(selection.viewerZone, now);
    const bounds = periodBounds(selection);
    if (isoDate(bounds.from) > isoDate(bounds.through)) throw new Error('From must be on or before Through.');
    if (isoDate(bounds.from) > horizonDate(selection, maximumOffset, now)) throw new Error('This period starts after the browsing horizon.');
    return selection;
};
export const anchorOf = selection => selection.dateType === 'custom' ? parseCivil(selection.fromDate) : civil(selection.year, selection.month, selection.day);
export const preset = (selection, type, anchor = anchorOf(selection), weekStart = regionalWeekStart().day) => ({
    dateType: type, ...anchor, timeBasis: selection.timeBasis, viewerZone: selection.viewerZone,
    ...(type === 'week' ? { weekStartsOn: selection.weekStartsOn || weekStart } : {})
});
/** Same-unit Custom arrows preserve the inclusive civil length; cross-unit shortcuts explicitly enter Day. */
export const moveSelection = (selection, unit, direction, sameUnit = false) => {
    if (sameUnit && selection.dateType === 'custom') {
        const { from, through } = periodBounds(selection);
        const ordinal = date => Date.UTC(date.year, date.month - 1, date.day) / 86400000;
        const days = (ordinal(through) - ordinal(from) + 1) * direction;
        return { ...selection, fromDate: isoDate(shiftDays(from, days)), throughDate: isoDate(shiftDays(through, days)) };
    }
    const anchor = anchorOf(selection);
    const next = unit === 'day' || unit === 'week' ? shiftDays(anchor, direction * (unit === 'week' ? 7 : 1))
        : shiftMonth(anchor, direction * (unit === 'year' ? 12 : 1));
    const type = !sameUnit && (unit === 'day' || selection.dateType === 'custom') ? 'day' : selection.dateType;
    return preset(selection, type, next);
};
/** Public selection identity is independent of construction order; only its fields and an opaque context enter the URL. */
export const selectionSearch = (selection, context = null) => {
    const params = new URLSearchParams({ viewType: selection.dateType });
    for (const key of Object.keys(selection).sort()) if (key !== 'dateType') params.set(key, String(selection[key]));
    if (context) params.set('filterContext', context);
    return `?${params}`;
};
export const readSelection = (search, basis, zone, maximumOffset, now = new Date()) => {
    const params = new URLSearchParams(search);
    const allowed = ['viewType', 'year', 'month', 'day', 'fromDate', 'throughDate', 'timeBasis', 'viewerZone', 'weekStartsOn', 'filterContext'];
    for (const [key] of params) if (!allowed.includes(key) || params.getAll(key).length !== 1) throw new Error('Invalid or repeated Timeline link values.');
    if (!params.size) return { selection: validateSelection(preset({ timeBasis: basis, viewerZone: zone }, 'day', todayInZone(zone, now)), maximumOffset, now), context: null, adopted: false };
    const type = params.get('viewType');
    const custom = type === 'custom';
    if (custom && ['year', 'month', 'day', 'weekStartsOn'].some(key => params.has(key))
        || !custom && ['fromDate', 'throughDate'].some(key => params.has(key))
        || type !== 'week' && params.has('weekStartsOn') || params.has('timeBasis') !== params.has('viewerZone')) throw new Error('Conflicting Timeline link values.');
    const number = (key, fallback) => {
        const value = params.get(key);
        if (value === null && fallback !== undefined) return fallback;
        if (!/^\d+$/.test(value)) throw new Error('Invalid Timeline date component.');
        return Number(value);
    };
    const selection = { dateType: type, timeBasis: params.get('timeBasis') || basis, viewerZone: params.get('viewerZone') || zone,
        ...(custom ? { fromDate: params.get('fromDate'), throughDate: params.get('throughDate') }
            : { year: number('year'), month: number('month', type === 'year' ? 1 : undefined), day: number('day', type === 'month' || type === 'year' ? 1 : undefined) }),
        ...(type === 'week' ? { weekStartsOn: number('weekStartsOn') } : {}) };
    const context = params.get('filterContext');
    return { selection: validateSelection(selection, maximumOffset, now), context, adopted: !params.has('timeBasis') };
};
/** Finite viewport normalization is an adapter to the shared rectangle contract, independent of camera movement. */
export const viewportArea = ({ west, south, east, north }) => {
    if (![west, south, east, north].every(Number.isFinite) || east <= west) throw new Error('Map area is unavailable.');
    const span = east - west;
    south = Math.max(-90, south); north = Math.min(90, north);
    if (south >= north) throw new Error('Map area is empty.');
    if (span >= 360) return { west: -180, south, east: 180, north };
    west = ((west + 180) % 360 + 360) % 360 - 180;
    east = west + span;
    if (east > 180) east -= 360;
    return { west, south, east, north };
};
