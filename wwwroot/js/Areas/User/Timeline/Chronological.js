import { TimelinePage } from './calendar-page.js';
import { CalendarIntent, emptyFilters } from './calendar-intent.js';
import { CalendarPreferences } from './calendar-preferences.js';
import { readSelection, selectionSearch, preset, moveSelection, todayInZone, validateSelection } from './calendar-scope.js';
import { createCalendarMap } from './calendar-map.js';
import { createCalendarDetail } from './calendar-detail.js';
import { createCalendarPicker } from './calendar-picker.js';
import { createCalendarFilters } from './calendar-filters.js';
import { createCalendarConnections } from './calendar-connections.js';
import { createCalendarDisplay } from './calendar-display.js';

/** Authenticated private chronology bootstrap. Legacy/public/all-time and bearer collections keep their owners. */
document.addEventListener('DOMContentLoaded', () => {
    const root = document.getElementById('calendarTimeline');
    if (!root) return;
    const subject = () => document.getElementById('browser-antiforgery')?.dataset.subjectId;
    const maximumOffset = Number(root.dataset.maximumOffset);
    const notice = document.getElementById('timelineNotice');
    const guarded = run => { try { const result = run(); result?.catch?.(() => { notice.textContent = 'Read unavailable. Retain this scope and retry.'; }); } catch (error) { notice.textContent = error.message; } };
    // Access to the storage object itself can be denied; wrappers keep that failure inside the owning transaction.
    const storage = name => ({ getItem: key => window[name].getItem(key), setItem: (key, value) => window[name].setItem(key, value), removeItem: key => window[name].removeItem(key) });
    const preferences = new CalendarPreferences(storage('localStorage'), subject(), location.origin);
    const intent = new CalendarIntent(storage('sessionStorage'), subject());
    let parsed;
    try { parsed = readSelection(location.search, preferences.value.timeBasis, Intl.DateTimeFormat().resolvedOptions().timeZone, maximumOffset); }
    catch (error) { notice.textContent = error.message; root.querySelectorAll('button,select').forEach(element => { element.disabled = true; }); return; }
    let recovery = false, restored = { filters: emptyFilters() };
    try { restored = intent.restore(parsed.selection, parsed.context); }
    catch { recovery = true; document.getElementById('contextRecovery').hidden = false; }
    const fetchRead = async (operation, values, signal) => {
        const response = await fetch(`/User/Timeline/calendar/${operation}`, { method: 'POST', credentials: 'same-origin', cache: 'no-store', signal,
            headers: { 'Content-Type': 'application/x-www-form-urlencoded;charset=UTF-8', RequestVerificationToken: document.querySelector('input[name="__RequestVerificationToken"]')?.value || '' },
            body: new URLSearchParams(Object.entries(values).map(([key, value]) => [key, String(value)])) });
        if (response.redirected || response.status === 401 || response.status === 403) throw Object.assign(new Error('Session ended.'), { status: 401 });
        let body;
        try { body = await response.json(); }
        catch { throw new Error('Calendar response unavailable. Retain this scope and retry.'); }
        if (!response.ok) throw Object.assign(new Error('Calendar read unavailable.'), { status: response.status, code: body.code,
            retryAfter: Number(response.headers.get('Retry-After')) || null });
        return body;
    };
    let owner, detail, picker, filters, connections, display;
    const mapView = createCalendarMap(id => detail.open(id), camera => { if (owner) { owner.camera = camera; owner.retain(); } });
    owner = new TimelinePage({ subject: subject(), currentSubject: subject, selection: parsed.selection, context: parsed.context,
        ...restored, restorationPending: recovery, intent, preferences, maximumOffset, fetchRead,
        replaceUrl: search => history.replaceState(null, '', location.pathname + search),
        publish: () => {
            display?.render(); picker?.render(); filters?.render(); connections?.render();
            if (owner.state.errors.session) {
                detail?.clear(); document.getElementById('statsModalContent').textContent = '';
                document.querySelectorAll('#dateDialog, #filtersDialog, #basisDialog').forEach(element => bootstrap.Modal.getInstance(element)?.hide());
                document.getElementById('filterSearch').value = ''; document.getElementById('filterActivities').textContent = '';
                root.querySelectorAll('button,select').forEach(element => { element.disabled = true; });
            }
        } });
    detail = createCalendarDetail(owner, mapView); picker = createCalendarPicker(owner);
    filters = createCalendarFilters(owner, mapView, value => {
        if (value !== undefined) { recovery = value; document.getElementById('contextRecovery').hidden = !value; }
        return recovery;
    });
    connections = createCalendarConnections(owner); display = createCalendarDisplay(owner, mapView, detail);
    mapView.restore(owner.camera);
    history.replaceState(null, '', location.pathname + selectionSearch(owner.selection, owner.context));
    const click = (id, action) => document.getElementById(id).addEventListener('click', () => guarded(action));
    click('choosePeriod', () => picker.open(owner.selection.dateType === 'custom'));
    click('chooseFilters', () => filters.open()); click('reenterFilters', () => filters.open(true));
    click('continueUnfiltered', () => { owner.clearFilters(); recovery = false; document.getElementById('contextRecovery').hidden = true; });
    click('connectionSettings', connections.open);
    click('today', () => owner.commitSelection(preset(owner.selection, 'day', todayInZone(owner.selection.viewerZone))));
    for (const [id, direction] of [['previousPeriod', -1], ['nextPeriod', 1]]) click(id, () => owner.commitSelection(moveSelection(owner.selection, owner.selection.dateType, direction, true)));
    document.querySelectorAll('[data-jump]').forEach(button => button.addEventListener('click', () => guarded(() => owner.commitSelection(moveSelection(owner.selection, button.dataset.jump, Number(button.dataset.direction))))));
    document.getElementById('periodMode').addEventListener('change', event => guarded(() => {
        if (event.target.value === 'custom') { event.target.value = owner.selection.dateType; picker.open(true, event.target); }
        else owner.commitSelection(preset(owner.selection, event.target.value));
    }));
    click('refreshTimeline', () => owner.retry()); click('fitPeriod', mapView.fit);
    click('showRecords', () => owner.openRecords()); click('loadMore', () => owner.loadRecords());
    click('showMap', () => { owner.panel = 'map'; owner.retain(); owner.emit(); mapView.map.invalidateSize(); });
    click('geographicDetails', () => { bootstrap.Modal.getOrCreateInstance(document.getElementById('statsModal')).show(); owner.enqueue('statistics'); });
    const basisModal = document.getElementById('basisDialog'); let basisTrigger;
    click('chooseBasis', () => {
        owner.openEditor('basis', { ...owner.selection }); basisTrigger = document.activeElement;
        document.getElementById('basisSelect').value = owner.selection.timeBasis; document.getElementById('viewerZone').value = owner.selection.viewerZone;
        bootstrap.Modal.getOrCreateInstance(basisModal).show();
    });
    click('currentBrowserZone', () => { document.getElementById('viewerZone').value = Intl.DateTimeFormat().resolvedOptions().timeZone; });
    click('applyBasis', () => {
        try { owner.applyBasis(document.getElementById('basisSelect').value, document.getElementById('viewerZone').value); bootstrap.Modal.getInstance(basisModal)?.hide(); }
        catch (error) { document.getElementById('basisError').textContent = error.message; }
    });
    basisModal.addEventListener('hidden.bs.modal', () => { if (owner.editor?.kind === 'basis') owner.cancelEditor(); basisTrigger?.focus(); });
    document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible' && !owner.blocked) owner.refresh(); });
    window.addEventListener('pageshow', event => { if (event.persisted && !owner.blocked) owner.refresh(); });
    window.addEventListener('pagehide', () => { owner.retain(); owner.active?.controller.abort(); });
    if (parsed.adopted) notice.textContent = 'Legacy date link adopted the remembered calendar basis and validated viewer zone.';
    if (parsed.adopted) owner.state.adoptionNotice = 'Legacy date link adopted the remembered calendar basis and validated viewer zone.';
    owner.emit();
    if (!recovery) owner.refresh().then(() => { if (owner.selectedId && !owner.blocked) detail.open(owner.selectedId); });
});
