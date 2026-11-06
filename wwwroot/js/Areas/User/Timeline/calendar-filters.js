import { emptyFilters, viewportArea } from './calendar-intent.js';
import { encode } from './calendar-detail.js';
/** Optional non-date filters edit a local draft; active-chip removal is its own persisted commitment. */
export const createCalendarFilters = (owner, mapView, onRecovery) => {
    const element = document.getElementById('filtersDialog');
    const search = document.getElementById('filterSearch'), unassigned = document.getElementById('filterUnassigned');
    const activities = document.getElementById('filterActivities'), error = document.getElementById('filterError');
    let trigger, catalog = [], catalogVersion = 0;
    const draft = () => owner.editor?.kind === 'filters' ? owner.editor.draft : null;
    const areaCaption = () => {
        const area = draft()?.area;
        document.getElementById('areaCaption').textContent = area ? `Saved rectangle: west ${area.west}, south ${area.south}, east ${area.east}, north ${area.north}${area.west > area.east ? ' (crosses dateline)' : ''}` : 'No saved area';
        document.getElementById('captureArea').textContent = area ? 'Replace with current map area' : 'Use this map area';
    };
    const renderCatalog = () => {
        if (!draft()) return;
        const ids = draft().activityIds;
        const choices = [...catalog];
        for (const id of ids) if (!choices.some(item => item.id === id)) choices.push({ id, name: `Retained activity ${id} (resolve or repair deliberately)` });
        activities.innerHTML = choices.map(item => `<label class="d-block"><input type="checkbox" data-activity="${item.id}" ${ids.includes(item.id) ? 'checked' : ''}> ${encode(item.name)}</label>`).join('');
    };
    const loadCatalog = async () => {
        const version = ++catalogVersion, epoch = owner.epoch;
        try {
            const response = await fetch('/api/activity', { credentials: 'same-origin', cache: 'no-store' });
            if (version !== catalogVersion || epoch !== owner.epoch || owner.blocked && !onRecovery()) return;
            if (response.status === 401 || response.status === 403 || response.redirected) { owner.sessionEnded(); return; }
            if (!response.ok) throw new Error();
            const values = await response.json();
            if (version !== catalogVersion || epoch !== owner.epoch) return;
            catalog = values.filter(item => Number.isInteger(item.id) && item.id > 0); renderCatalog();
        } catch { if (version === catalogVersion && draft()) error.textContent = 'Activities unavailable. Existing selections are retained; refresh the catalog to repair deliberately.'; }
    };
    const open = (recover = false) => {
        try {
            owner.openEditor('filters', structuredClone(recover ? emptyFilters() : owner.filters)); trigger = document.activeElement;
            search.value = draft().q; unassigned.checked = draft().includeUnassigned; error.textContent = '';
            renderCatalog(); areaCaption(); bootstrap.Modal.getOrCreateInstance(element).show(); loadCatalog();
        } catch (failure) { document.getElementById('timelineNotice').textContent = failure.message; }
    };
    element.addEventListener('shown.bs.modal', () => search.focus());
    element.addEventListener('hidden.bs.modal', () => { catalogVersion++; if (draft()) owner.cancelEditor(); trigger?.focus(); });
    search.addEventListener('input', () => { if (draft()) draft().q = search.value; });
    unassigned.addEventListener('change', () => { if (draft()) draft().includeUnassigned = unassigned.checked; });
    activities.addEventListener('change', event => {
        if (!draft() || !event.target.dataset.activity) return;
        const ids = new Set(draft().activityIds), id = Number(event.target.dataset.activity);
        if (event.target.checked) ids.add(id); else ids.delete(id);
        draft().activityIds = [...ids];
    });
    document.getElementById('reloadActivities').addEventListener('click', loadCatalog);
    document.getElementById('captureArea').addEventListener('click', () => {
        try { draft().area = viewportArea(mapView.viewport()); areaCaption(); }
        catch (failure) { error.textContent = failure.message; }
    });
    document.getElementById('clearArea').addEventListener('click', () => { draft().area = null; areaCaption(); });
    const apply = filters => {
        try { owner.commitFilters(filters); onRecovery(false); bootstrap.Modal.getInstance(element)?.hide(); }
        catch (failure) { error.textContent = failure.message; }
    };
    document.getElementById('applyFilters').addEventListener('click', () => apply(draft()));
    document.getElementById('clearFilters').addEventListener('click', () => apply(emptyFilters()));
    let previousChips;
    const render = () => {
        const effective = owner.effective || owner.filters;
        const key = JSON.stringify([owner.filters, effective]);
        if (key === previousChips) return; previousChips = key;
        const container = document.getElementById('activeFilters'); container.replaceChildren();
        const chip = (caption, clear) => {
            const button = document.createElement('button'); button.className = 'btn btn-sm btn-outline-secondary text-break text-start';
            button.style.maxWidth = '100%'; button.textContent = `${caption} ×`; button.setAttribute('aria-label', `Remove ${caption}`);
            button.addEventListener('click', () => { try { clear(); } catch (failure) { document.getElementById('timelineNotice').textContent = failure.message; } }); container.append(button);
        };
        if (owner.filters.q) chip(`Search Notes and address: ${owner.filters.q}`, () => owner.commitFilters({ ...owner.filters, q: '' }));
        if (owner.filters.activityIds.length || owner.filters.includeUnassigned) chip(`Activities: ${effective.activityIds.join(', ')}${effective.includeUnassigned ? ' · No activity' : ''}`,
            () => owner.commitFilters({ ...owner.filters, activityIds: [], includeUnassigned: false }));
        if (owner.filters.area) {
            const area = owner.filters.area;
            chip(`Saved map area: ${area.west}, ${area.south} → ${area.east}, ${area.north}`, () => owner.commitFilters({ ...owner.filters, area: null }));
        }
        if (container.childElementCount) chip('Clear filters', () => owner.clearFilters());
    };
    return { open, render };
};
