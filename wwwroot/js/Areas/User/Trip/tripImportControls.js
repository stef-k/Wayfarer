import { message as tr, number, plural } from '../../../localization/messages.js';
import { enableWorkspaceLanguage, holdLanguageNavigation, registerLanguageDraft } from '../../../localization/languageNavigation.js';
import { createTripImporter, uncertainImport, abandonImportConfirmation } from './tripImportClient.js';

/** Presents literal authored civil values and optional qualifiers without inferred dates or zones. */
const describeEntry = entry => {
    const precision = { month: tr('Editor.PlanningMonth', 'Month'), date: tr('Editor.PlanningDate', 'Date'), dateTime: tr('Editor.TripsImportDateAndTime', 'Date and time') };
    const form = { unknown: tr('Editor.CheckStatus.unknown', 'Unknown'), flexible: tr('Editor.TripsImportFlexible', 'Flexible') };
    const value = v => {
        if (!v) return tr('Editor.TripsImportNotSupplied', 'not supplied');
        const civil = tr('Editor.PlanningCivil', '{0} ({1})', v.local, [precision[v.precision] ?? v.precision, v.zone].filter(Boolean).join(' · '));
        return v.selectedOffsetSeconds == null ? civil : tr('Editor.TripsImport0SelectedUTCOffset1Seconds', '{0} · selected UTC offset {1} seconds', civil, number(v.selectedOffsetSeconds));
    };
    const bound = b => !b ? tr('Editor.TripsImportNotSupplied', 'not supplied') : b.state === 'known' ? value(b.value) : form[b.state] ?? b.state;
    const when = entry.when;
    let text = when.form === 'at' ? value(when.value) : when.form === 'period'
        ? tr('Editor.TripsImportStart0End1', 'Start: {0}; End: {1}', bound(when.start), bound(when.end)) : form[when.form] ?? when.form;
    if (entry.label != null) text = tr('Editor.TripsImport01', '{0}: {1}', entry.label, text);
    if (entry.kind != null) text = tr('Editor.TripsImport0Meaning1', '{0} · meaning: {1}', text, entry.kind);
    if (entry.allocationMinutes != null) text = tr('Editor.TripsImport0Allocation1Minutes', '{0} · allocation: {1} minutes', text, number(entry.allocationMinutes));
    return text;
};

/** Mounts the actual file, duplicate, whole-aggregate review and uncertainty controls on the Trip index. */
export const mountTripImport = () => {
    const root = document.getElementById('trip-import-controls');
    if (!root) return;
    const get = id => root.querySelector(`#${id}`);
    const label = { keep: tr('Editor.TripsImportKeepExisting', 'Keep existing'), use: tr('Editor.TripsImportUseImportedPlanning', 'Use imported planning'), clear: tr('Editor.TripsImportClearExistingPlanning', 'Clear existing planning'), remove: tr('Editor.TripsImportIAgreeToRemoveThisObject', 'I agree to remove this object and its planning') };
    const status = { removed: tr('Editor.TripsImportRemovedOrReidentifiedObjectConsentIs', 'Removed or reidentified object: consent is required.'), conflict: tr('Editor.TripsImportDifferentPlanningKeepExistingIsThe', 'Different planning. Keep existing is the default.'),
        omitted: tr('Editor.TripsImportPlanningIsOmittedFromThisFile', 'Planning is omitted from this file and will be preserved.'), equal: tr('Editor.TripsImportPlanningIsEqualNoConflictChoice', 'Planning is equal. No conflict choice is needed.'),
        absent: tr('Editor.TripsImportThisTargetHasNoPlanningThe', 'This target has no planning. The imported aggregate will be added.'), new: tr('Editor.TripsImportNewObjectTheImportedAggregateWill', 'New object: the imported aggregate will be added.') };
    let releaseLanguageHold;
    const kinds = { trip: tr('Editor.Trip', 'Trip'), region: tr('Editor.Region', 'Region'), place: tr('Editor.CompositionPlace', 'Place'), area: tr('Editor.Area', 'Area'), segment: tr('Editor.CompositionSegment', 'Segment') };
    const fields = { planning: tr('Editor.Planning', 'Planning'), title: tr('Editor.TripsImportTitle', 'Title'), 'credits / contribution terms': tr('Editor.TripsImportCreditsContributionTerms', 'Credits / contribution terms') };
    const render = state => {
        // A mounted request holds switching. Session storage owns unresolved attempts after it settles.
        if (state.busy) releaseLanguageHold ??= holdLanguageNavigation();
        else if (releaseLanguageHold) {
            releaseLanguageHold();
            releaseLanguageHold = null;
        }
        root.hidden = state.cancelled && !state.uncertain;
        get('importSelectedFile').textContent = state.file?.name || tr('Editor.TripsImportImport', 'Import');
        get('importChoiceMessage').textContent = state.uncertain ? uncertainImport : state.busy
            ? (state.reviewing ? tr('Editor.TripsImportReviewingTheSelectedKML', 'Reviewing the selected KML…') : tr('Editor.TripsImportImportingTheSelectedKML', 'Importing the selected KML…'))
            : state.message || (state.review
                ? tr('Editor.TripsImportReplace0WithTheSelectedFile', 'Replace “{0}” with the selected file\'s complete Trip content. Review private planning below.', state.review.targetName)
                : tr('Editor.TripsImportATripWithThisIDMay', 'A Trip with this ID may already be in your account. Review an update or create a separate private Trip.'));
        const rows = get('importPlanningRows');
        rows.replaceChildren();
        for (const row of state.review?.planning || []) {
            const field = document.createElement('fieldset');
            field.className = 'border rounded p-3 mb-3';
            const legend = document.createElement('legend'); legend.className = 'fs-6'; legend.textContent = tr('Editor.TripsImport012', '{0}: {1} — {2}', kinds[row.kind] ?? row.kind, row.name, fields[row.field ?? 'planning'] ?? row.field);
            const explanation = document.createElement('p'); explanation.textContent = status[row.state] || '';
            field.append(legend, explanation);
            if (row.field && row.field !== 'planning') {
                explanation.textContent = row.state === 'removed' ? tr('Editor.TripsImportRemovingThisObjectAlsoRemovesIts', 'Removing this object also removes its portable content. Explicit consent is required.')
                    : tr('Editor.TripsImportKeepExistingIsTheDefaultUse', 'Keep existing is the default. Use or Clear applies only to these reviewed portable fields. Retain all still-applicable inherited notices.');
                const text = document.createElement('pre');
                text.style.whiteSpace = 'pre-wrap'; text.textContent = [tr('Editor.TripsImportExisting0', 'Existing: {0}', row.currentText ?? tr('Editor.TripsImportAbsent', '(absent)')), tr('Editor.TripsImportImported0', 'Imported: {0}', row.importedText ?? tr('Editor.TripsImportAbsent', '(absent)'))].join('\n');
                field.append(text);
            }
            for (const [title, plan] of [[tr('Editor.TripsImportExistingPlanning', 'Existing planning'), row.current], [tr('Editor.TripsImportImportedPlanning', 'Imported planning'), row.imported]]) {
                if (!plan) continue;
                const details = document.createElement('details'); const summary = document.createElement('summary'); summary.textContent = tr('Editor.TripsImport013', '{0} · {1}', title, plural('Editor.TripsImportPlanningEntries', plan.entries.length, '{0} entry', '{0} entries'));
                const list = document.createElement('ol');
                for (const entry of plan.entries) {
                    const item = document.createElement('li');
                    item.style.whiteSpace = 'pre-wrap';
                    item.textContent = describeEntry(entry); list.append(item);
                }
                details.append(summary, list); field.append(details);
            }
            if (row.state === 'conflict' && row.current && row.imported) {
                const removed = row.current.entries.filter(entry => !row.imported.entries.some(next => next.id === entry.id));
                if (removed.length) {
                    const warning = document.createElement('p'); warning.textContent = tr('Editor.TripsImportUseImportedPlanningRemovesTheseExisting', 'Use imported planning removes these existing entries:');
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
                wrapper.append(input, document.createTextNode(` ${(row.field && row.field !== 'planning' ? { keep: tr('Editor.TripsImportKeepExisting', 'Keep existing'), use: tr('Editor.TripsImportUseImportedValue', 'Use imported value'), clear: tr('Editor.TripsImportExplicitlyClearThisValue', 'Explicitly clear this value'), remove: tr('Editor.TripsImportIAgreeToRemoveThisObject2', 'I agree to remove this object and its portable content') }[choice.action] : label[choice.action])}`)); field.append(wrapper);
            }
            rows.append(field);
        }
        const blocked = state.busy || state.uncertain || !state.file;
        get('btnUpdate').disabled = blocked;
        get('btnCopy').disabled = blocked;
        get('btnApplyImport').hidden = !state.review;
        get('btnApplyImport').disabled = blocked || state.reviewStale || (state.review?.planning || []).some(r => r.state === 'removed' && state.choices.get(r.key) !== 'remove');
        get('btnUpdate').textContent = state.review ? tr('Editor.TripsImportRefreshReview', 'Refresh review') : tr('Editor.TripsReviewUpdate', 'Review update');
        get('btnCancelImport').disabled = state.busy;
        get('btnAbandonImport').hidden = !state.uncertain;
        get('btnAbandonImport').disabled = state.busy;
        for (const id of ['importFile', 'importFileMyMaps']) document.getElementById(id).disabled = state.busy || state.uncertain;
    };
    const importer = createTripImporter({ fetchImpl: fetch, onState: render,
        onRedirect: url => { window.location.href = url; }, onDuplicate: () => { root.hidden = false; },
        showError: () => { root.hidden = false; },
        showNotices: notices => notices.forEach(n => {
            if (n.code === 'generic_route_simplified') wayfarer.showAlert('info', tr('Editor.TripsImport0RouteReducedFrom1To', '{0}: route reduced from {1} to {2} coordinates.', n.segmentName, number(n.originalCoordinateCount), number(n.resultingCoordinateCount)));
            else if (n.code === 'generic_routes_simplified_additional') wayfarer.showAlert('info', plural('Editor.TripsImportSimplifiedRoutes', n.additionalRouteCount, '{0} additional route was simplified.', '{0} additional routes were simplified.'));
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
    registerLanguageDraft(() => Boolean(importer.state.file && (!importer.state.cancelled || importer.state.uncertain)));
    if (importer.state.uncertain) { root.hidden = false; render(importer.state); }
    enableWorkspaceLanguage();
};
