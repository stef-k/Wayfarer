import { anchorOf, civil, horizonDate, isoDate, moveSelection, parseCivil, periodBounds, preset,
    regionalWeekStart, shiftDays, shiftMonth, validateSelection, weekday } from './calendar-scope.js';
/** One date transaction: browsing/focus is separate from direct activation or Custom Apply. */
export const createCalendarPicker = owner => {
    const element = document.getElementById('dateDialog');
    const grid = document.getElementById('calendarGrid');
    const monthInput = document.getElementById('pickerMonth');
    const fromInput = document.getElementById('rangeFrom'), throughInput = document.getElementById('rangeThrough');
    const typed = document.getElementById('typedDate');
    const error = document.getElementById('dateError');
    const regional = regionalWeekStart();
    let displayed, focusDate, custom = false, endpoint = 'fromDate', trigger = null;
    let renderedKey = null;
    const report = run => { try { error.textContent = ''; return run(); } catch (failure) { error.textContent = failure.message; } };
    const close = () => bootstrap.Modal.getInstance(element)?.hide();
    const commit = selection => report(() => { owner.commitSelection(selection, true); close(); });
    const browse = date => {
        displayed = civil(date.year, date.month, 1); monthInput.value = isoDate(displayed).slice(0, 7);
        renderedKey = null; owner.openMonth(displayed.year, displayed.month);
    };
    const endpointCaption = () => { document.getElementById('endpointCaption').textContent = `Calendar edits ${endpoint === 'fromDate' ? 'From' : 'Through'} only.`; };
    const activate = date => {
        if (custom) {
            if (endpoint === 'fromDate') fromInput.value = isoDate(date); else throughInput.value = isoDate(date);
            owner.editor.draft[endpoint] = isoDate(date); renderedKey = null; render();
        } else commit(preset(owner.selection, owner.selection.dateType === 'week' ? 'week' : 'day', date));
    };
    const open = (customRange = false, source = document.activeElement) => report(() => {
        const bounds = periodBounds(owner.selection);
        owner.openEditor('date', { fromDate: isoDate(bounds.from), throughDate: isoDate(bounds.through) });
        trigger = source; custom = customRange; endpoint = 'fromDate';
        fromInput.value = isoDate(bounds.from); throughInput.value = isoDate(bounds.through);
        focusDate = anchorOf(owner.selection); typed.value = isoDate(focusDate);
        document.getElementById('customEndpoints').hidden = !custom;
        document.getElementById('applyRange').hidden = !custom;
        document.getElementById('typedDateForm').hidden = custom;
        document.getElementById('showPickerMonth').disabled = custom;
        document.getElementById('showPickerYear').disabled = custom;
        endpointCaption(); browse(focusDate); bootstrap.Modal.getOrCreateInstance(element).show();
    });
    element.addEventListener('shown.bs.modal', () => {
        if (custom) fromInput.focus(); else grid.querySelector('[tabindex="0"]')?.focus();
    });
    element.addEventListener('hidden.bs.modal', () => {
        if (owner.editor?.kind === 'date') owner.cancelEditor(); else owner.closeMonth();
        trigger?.focus();
    });
    monthInput.addEventListener('change', () => report(() => { const date = parseCivil(monthInput.value + '-01'); focusDate = date; browse(date); }));
    for (const [id, months] of [['previousPickerMonth', -1], ['nextPickerMonth', 1], ['previousPickerYear', -12], ['nextPickerYear', 12]]) {
        document.getElementById(id).addEventListener('click', () => report(() => { focusDate = shiftMonth(focusDate, months); browse(shiftMonth(displayed, months)); }));
    }
    for (const [input, key] of [[fromInput, 'fromDate'], [throughInput, 'throughDate']]) {
        input.addEventListener('focus', () => { endpoint = key; endpointCaption(); });
        input.addEventListener('input', () => { if (owner.editor?.kind === 'date') owner.editor.draft[key] = input.value; renderedKey = null; render(); });
    }
    document.getElementById('typedDateForm').addEventListener('submit', event => { event.preventDefault(); report(() => activate(parseCivil(typed.value))); });
    document.getElementById('applyRange').addEventListener('click', () => {
        commit({ dateType: 'custom', fromDate: fromInput.value, throughDate: throughInput.value,
            timeBasis: owner.selection.timeBasis, viewerZone: owner.selection.viewerZone });
    });
    for (const type of ['month', 'year']) document.getElementById(type === 'month' ? 'showPickerMonth' : 'showPickerYear').addEventListener('click', () => report(() => {
        const anchor = anchorOf(owner.selection);
        const target = civil(displayed.year, type === 'month' ? displayed.month : anchor.month,
            Math.min(anchor.day, new Date(Date.UTC(displayed.year, type === 'month' ? displayed.month : anchor.month, 0)).getUTCDate()));
        commit(preset(owner.selection, type, target));
    }));
    /** Roving focus changes only the displayed month; activation is the explicit commitment. */
    grid.addEventListener('keydown', event => report(() => {
        if (!event.target.dataset.date) return;
        let next = focusDate;
        const start = owner.selection.weekStartsOn || regional.day;
        if (event.key === 'ArrowLeft') next = shiftDays(focusDate, -1);
        else if (event.key === 'ArrowRight') next = shiftDays(focusDate, 1);
        else if (event.key === 'ArrowUp') next = shiftDays(focusDate, -7);
        else if (event.key === 'ArrowDown') next = shiftDays(focusDate, 7);
        else if (event.key === 'Home') next = shiftDays(focusDate, -(weekday(focusDate) - start + 7) % 7);
        else if (event.key === 'End') next = shiftDays(focusDate, 6 - (weekday(focusDate) - start + 7) % 7);
        else if (event.key === 'PageUp' || event.key === 'PageDown') next = shiftMonth(focusDate, (event.key === 'PageUp' ? -1 : 1) * (event.shiftKey ? 12 : 1));
        else return;
        event.preventDefault(); focusDate = next;
        if (next.year !== displayed.year || next.month !== displayed.month) browse(next);
        renderedKey = null; render(); grid.querySelector(`[data-date="${isoDate(next)}"]`)?.focus();
    }));
    const render = () => {
        if (!owner.month || !displayed) return;
        const marks = owner.state.availability;
        const status = owner.state.status.availability;
        const key = JSON.stringify([displayed, focusDate, status, marks?.observationId, owner.selection, owner.editor?.draft]);
        if (key === renderedKey) return; renderedKey = key;
        const restoreFocus = grid.contains(document.activeElement);
        const starts = owner.selection.dateType === 'week' ? owner.selection.weekStartsOn : regional.day;
        const weekNames = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'];
        document.getElementById('weekOrder').textContent = `Week starts ${weekNames[starts - 1]}${owner.selection.dateType === 'week' ? ' (frozen selection)' : regional.fallback ? ' (Monday fallback)' : ' (browser formatting locale)'}.`;
        document.getElementById('monthStatus').textContent = marks ? `${marks.displayedMonth} · observed ${marks.asOfUtc}${marks.hasUnresolvedCandidates ? ' · Unmarked dates are unknown' : ''}`
            : `${status === 'loading' ? 'Loading date marks…' : 'Date marks unavailable. Manual selection remains available.'}`;
        grid.replaceChildren();
        const header = document.createElement('div'); header.setAttribute('role', 'row'); header.style.cssText = 'display:grid;grid-template-columns:repeat(7,minmax(0,1fr));';
        for (let i = 0; i < 7; i++) { const label = document.createElement('span'); label.setAttribute('role', 'columnheader'); label.className = 'text-center small'; label.textContent = weekNames[(starts - 1 + i) % 7].slice(0, 2); header.append(label); }
        grid.append(header);
        const offset = (weekday(displayed) - starts + 7) % 7;
        const count = new Date(Date.UTC(displayed.year, displayed.month, 0)).getUTCDate();
        const present = new Set(marks?.presentDates || []);
        const bounds = periodBounds(owner.selection), from = isoDate(bounds.from), through = isoDate(bounds.through);
        const today = owner.state.period?.navigation.today;
        let row;
        for (let index = 0; index < Math.ceil((offset + count) / 7) * 7; index++) {
            if (index % 7 === 0) { row = document.createElement('div'); row.setAttribute('role', 'row'); row.style.cssText = header.style.cssText; grid.append(row); }
            const cell = document.createElement('div'); cell.setAttribute('role', 'gridcell'); row.append(cell);
            const number = index - offset + 1;
            if (number < 1 || number > count) { cell.setAttribute('aria-label', 'Outside displayed month; not observed'); continue; }
            const date = civil(displayed.year, displayed.month, number), iso = isoDate(date);
            const button = document.createElement('button'); button.type = 'button'; button.dataset.date = iso;
            button.className = `btn btn-sm w-100 p-1 rounded-0 ${iso >= from && iso <= through ? 'border border-primary border-2' : ''}`;
            button.tabIndex = isoDate(focusDate) === iso ? 0 : -1;
            const positive = present.has(iso), unknown = !marks || marks.hasUnresolvedCandidates;
            const symbol = positive ? '●' : unknown ? '?' : '○';
            button.textContent = `${number}${symbol}${today === iso ? ' T' : ''}`;
            button.setAttribute('aria-label', `${iso}, ${positive ? 'matching records' : unknown ? 'unknown presence' : 'no matches at observation'}${iso >= from && iso <= through ? ', selected range' : ', outside selected range'}${today === iso ? ', Today' : ''}`);
            button.setAttribute('aria-selected', String(iso >= from && iso <= through));
            let reason = '';
            try { if (!custom) validateSelection(preset(owner.selection, owner.selection.dateType === 'week' ? 'week' : 'day', date), owner.maximumOffset); }
            catch (failure) { reason = failure.message; button.setAttribute('aria-disabled', 'true'); button.title = reason; }
            button.addEventListener('focus', () => { focusDate = date; grid.querySelectorAll('button').forEach(item => { item.tabIndex = item === button ? 0 : -1; }); });
            button.addEventListener('click', () => reason ? error.textContent = reason : report(() => activate(date)));
            cell.append(button);
        }
        if (restoreFocus) grid.querySelector('[tabindex="0"]')?.focus();
    };
    return { open, render };
};
