import { selectionSearch, viewportArea } from './calendar-scope.js';
/** Exactly one tab slot; no result DTOs, pagination, Notes copies or prior-query history. */
const slot = 'wayfarer-private-timeline/2';
export const emptyFilters = () => ({ q: '', activityIds: [], includeUnassigned: false, area: null });
export const filtersActive = filters => !!(filters.q || filters.activityIds.length || filters.includeUnassigned || filters.area);
/** Validate only transport; server normalization and catalog resolution remain authoritative. */
export const filterBody = filters => {
    if (typeof filters.q !== 'string' || [...filters.q].length > 256 || /\u0000|[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/u.test(filters.q)) throw new Error('Search must contain at most 256 valid Unicode characters.');
    if (!Array.isArray(filters.activityIds) || filters.activityIds.some(id => !Number.isInteger(id) || id < 1 || id > 2147483647)) throw new Error('Choose valid activities.');
    const values = {};
    if (filters.q) values.q = filters.q;
    if (filters.activityIds.length) values.activityIds = [...new Set(filters.activityIds)].sort((a, b) => a - b).join(',');
    if (filters.includeUnassigned) values.includeUnassigned = 'true';
    if (filters.area) {
        const { west, south, east, north } = filters.area;
        if (![west, south, east, north].every(Number.isFinite) || west < -180 || west > 180 || east < -180 || east > 180
            || south < -90 || north > 90 || south >= north || west === east) throw new Error('Choose a valid saved map area.');
        Object.assign(values, { west, south, east, north });
    }
    const bytes = new TextEncoder().encode(Object.entries(values).map(([key, value]) => key + value).join(''));
    if (bytes.length > 8192) throw new Error('Filter values exceed the request transport limit.');
    return values;
};
export class CalendarIntent {
    constructor(storage, subject) { this.storage = storage; this.subject = subject; }
    /** Public date identity is authoritative; a date-only link never adopts stored private filters. */
    restore(selection, context) {
        let saved;
        try { saved = JSON.parse(this.storage.getItem(slot) || 'null'); } catch { /* Context loss is explicit below. */ }
        if (saved?.subject !== this.subject && saved) { try { this.storage.removeItem(slot); } catch { /* Remain fenced. */ } saved = null; }
        if (context !== null && (!/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(context) || saved?.version !== 2 || saved.context !== context || saved.subject !== this.subject)) throw new Error('Private filters are unavailable in this tab.');
        if (context) {
            try { filterBody(saved.filters); } catch { throw new Error('Private filters are unavailable in this tab.'); }
        }
        const matches = saved?.version === 2 && saved.subject === this.subject && saved.publicSelection === selectionSearch(selection)
            && (saved.context || null) === context;
        return { filters: context ? saved.filters : emptyFilters(), ...(matches ? { returnState: saved.returnState } : {}) };
    }
    /** Filter transactions must be durable before URL replacement or read publication. */
    save(selection, context, filters, returnState) {
        filterBody(filters);
        const raw = JSON.stringify({ version: 2, subject: this.subject, publicSelection: selectionSearch(selection), context, filters, returnState });
        try {
            this.storage.setItem(slot, raw);
            if (this.storage.getItem(slot) !== raw) throw new Error();
        } catch { throw new Error('Tab restoration could not be saved. The applied filters and your draft are retained. Retry Apply.'); }
    }
    clear() { try { this.storage.removeItem(slot); } catch { /* Publication remains fenced after session loss. */ } }
}
export { viewportArea };
