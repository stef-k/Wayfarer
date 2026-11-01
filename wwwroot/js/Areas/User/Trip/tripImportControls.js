import { createTripImporter, uncertainImport, abandonImportConfirmation } from './tripImportClient.js';

/** Presents literal authored civil values and optional qualifiers without inferred dates or zones. */
const describeEntry = entry => {
    const value = v => v ? `${v.local} (${v.precision})${v.zone ? ` ${v.zone}` : ''}${v.selectedOffsetSeconds == null ? '' : `, selected UTC offset ${v.selectedOffsetSeconds} seconds`}` : 'not supplied';
    const bound = b => !b ? 'not supplied' : b.state === 'known' ? value(b.value) : b.state;
    const when = entry.when;
    const timing = when.form === 'at' ? value(when.value) : when.form === 'period'
        ? `Start: ${bound(when.start)}; End: ${bound(when.end)}` : when.form;
    return `${entry.label == null ? '' : `${entry.label}: `}${timing}${entry.kind == null ? '' : `; meaning: ${entry.kind}`}${entry.allocationMinutes == null ? '' : `; allocation: ${entry.allocationMinutes} minutes`}`;
};

/** Mounts the actual file, duplicate, whole-aggregate review and uncertainty controls on the Trip index. */
export const mountTripImport = () => {
    const root = document.getElementById('trip-import-controls');
    if (!root) return;
    const get = id => root.querySelector(`#${id}`);
    const label = { keep: 'Keep existing', use: 'Use imported planning', clear: 'Clear existing planning', remove: 'I agree to remove this object and its planning' };
    const status = { removed: 'Removed or reidentified object: consent is required.', conflict: 'Different planning. Keep existing is the default.',
        omitted: 'Planning is omitted from this file and will be preserved.', equal: 'Planning is equal. No conflict choice is needed.',
        absent: 'This target has no planning. The imported aggregate will be added.', new: 'New object: the imported aggregate will be added.' };
    const render = state => {
        root.hidden = state.cancelled && !state.uncertain;
        get('importSelectedFile').textContent = state.file?.name || 'Import';
        get('importChoiceMessage').textContent = state.uncertain ? uncertainImport : state.message || (state.review
            ? `Replace “${state.review.targetName}” with the selected file's complete Trip content. Review private planning below.`
            : 'A Trip with this ID may already be in your account. Review an update or create a separate private Trip.');
        const rows = get('importPlanningRows');
        rows.replaceChildren();
        for (const row of state.review?.planning || []) {
            const field = document.createElement('fieldset');
            field.className = 'border rounded p-3 mb-3';
            const legend = document.createElement('legend'); legend.className = 'fs-6'; legend.textContent = `${row.kind}: ${row.name}`;
            const explanation = document.createElement('p'); explanation.textContent = status[row.state] || '';
            field.append(legend, explanation);
            for (const [title, plan] of [['Existing planning', row.current], ['Imported planning', row.imported]]) {
                if (!plan) continue;
                const details = document.createElement('details'); const summary = document.createElement('summary'); summary.textContent = `${title}: ${plan.entries.length} entries`;
                const list = document.createElement('ol');
                for (const entry of plan.entries) {
                    const item = document.createElement('li');
                    item.style.whiteSpace = 'pre-wrap';
                    item.textContent = describeEntry(entry); list.append(item);
                }
                details.append(summary, list); field.append(details);
            }
            if (row.state === 'conflict') {
                const removed = row.current.entries.filter(entry => !row.imported.entries.some(next => next.id === entry.id));
                if (removed.length) {
                    const warning = document.createElement('p'); warning.textContent = 'Use imported planning removes these existing entries:';
                    const list = document.createElement('ol');
                    for (const entry of removed) { const item = document.createElement('li'); item.textContent = describeEntry(entry); list.append(item); }
                    field.append(warning, list);
                }
            }
            for (const choice of row.choices) {
                const wrapper = document.createElement('label'); wrapper.className = 'd-block mb-2';
                const input = document.createElement('input');
                input.type = row.state === 'removed' ? 'checkbox' : 'radio'; input.name = row.key;
                input.value = choice.action; input.checked = state.choices.get(row.key) === choice.action;
                input.disabled = state.busy || state.uncertain;
                input.addEventListener('change', () => importer.choose(row.key, input.checked ? choice.action : ''));
                wrapper.append(input, document.createTextNode(` ${label[choice.action]}`)); field.append(wrapper);
            }
            rows.append(field);
        }
        const blocked = state.busy || state.uncertain || !state.file;
        get('btnUpdate').disabled = blocked;
        get('btnCopy').disabled = blocked;
        get('btnApplyImport').hidden = !state.review;
        get('btnApplyImport').disabled = blocked || state.reviewStale || (state.review?.planning || []).some(r => r.state === 'removed' && state.choices.get(r.key) !== 'remove');
        get('btnUpdate').textContent = state.review ? 'Refresh review' : 'Review update';
        get('btnCancelImport').disabled = state.busy;
        get('btnAbandonImport').hidden = !state.uncertain;
        get('btnAbandonImport').disabled = state.busy;
        for (const id of ['importFile', 'importFileMyMaps']) document.getElementById(id).disabled = state.busy || state.uncertain;
    };
    const importer = createTripImporter({ fetchImpl: fetch, onState: render,
        onRedirect: url => { window.location.href = url; }, onDuplicate: () => { root.hidden = false; },
        showError: () => { root.hidden = false; },
        showNotices: notices => notices.forEach(n => {
            if (n.code === 'generic_route_simplified') wayfarer.showAlert('info', `${n.segmentName}: route reduced from ${n.originalCoordinateCount} to ${n.resultingCoordinateCount} coordinates.`);
            else if (n.code === 'generic_routes_simplified_additional') wayfarer.showAlert('info', `${n.additionalRouteCount} additional routes were simplified.`);
        })
    }, { storage: window.sessionStorage, owner: root.dataset.owner });
    for (const id of ['importFile', 'importFileMyMaps']) document.getElementById(id)?.addEventListener('change', event => {
        const file = event.target.files?.[0];
        if (file) { root.hidden = false; void importer.upload(file); }
        // The File stays in the importer; clearing permits selecting the same corrected file later.
        event.target.value = '';
    });
    get('btnUpdate').addEventListener('click', () => { void importer.update(); });
    get('btnApplyImport').addEventListener('click', () => { void importer.apply(); });
    get('btnCopy').addEventListener('click', () => { void importer.createCopy(); });
    get('btnCancelImport').addEventListener('click', importer.cancel);
    get('btnAbandonImport').addEventListener('click', () => importer.abandon(window.confirm(abandonImportConfirmation)));
    if (importer.state.uncertain) { root.hidden = false; render(importer.state); }
};
