import { notesHtmlForDisplay, notesHtmlForSave } from './notes-images.js';
import { locationWork, retainLocationDraft, saveLocationMutation, readLocationDetail, adoptLocationOriginals } from './location-mutation.js';
import { locationEditCommand } from './location-edit-state.js';

/** Connects the actual Web form to dirty originals, durable drafts, immutable attempts and visible conflict recovery. */
export const mountProtectedLocationForm = (form, quill, clearNotes) => {
    // Even unavailable/corrupt local proof must never fall through to a native unconditional form submission.
    form.addEventListener('submit', event => event.preventDefault());
    const id = Number(form.dataset.locationId);
    let detail = JSON.parse(form.dataset.locationDetail);
    const status = document.getElementById('location-save-status');
    const names = ['Latitude', 'Longitude', 'Altitude', 'SelectedActivityId', 'LocalTimestamp', 'Address', 'FullAddress', 'AddressNumber', 'StreetName', 'PostCode', 'Place', 'Region', 'Country'];
    const input = name => form.elements.namedItem(name);
    const values = () => ({ ...Object.fromEntries(names.map(name => [name, input(name).value])), Notes: notesHtmlForSave(quill.root.innerHTML) });
    let initial = values();
    let inputTimeZone = form.dataset.timeZone;
    const timeAmbiguity = document.getElementById('timeAmbiguity');
    const manual = new Set();
    let restoredClear = false;
    let inspected = null;
    const state = locationWork(id);
    const stored = state.draft?.value?.draft || state.draft?.value;
    if (state.currentObservation?.generation === state.settledGeneration)
        status.textContent = 'Current state satisfied this draft; historical outcome remains unknown.';
    if (stored?.initial && state.draft.generation !== state.settledGeneration) {
        initial = stored.initial; detail = stored.detail;
        for (const name of names) input(name).value = stored.current[name];
        quill.root.innerHTML = notesHtmlForDisplay(stored.current.Notes);
        for (const name of stored.explicitManual) manual.add(name);
        restoredClear = stored.clearNotes;
        // A refreshed row may have moved; retained wall strings keep their edit-start interpretation.
        inputTimeZone = stored.inputTimeZone;
        timeAmbiguity.value = stored.timeAmbiguity || '';
        status.textContent = 'Restored your retained draft. Save checks any earlier uncertain action first.';
    }
    if (stored?.activityDetail && state.draft.generation !== state.settledGeneration) {
        detail = stored.activityDetail;
        const activity = state.draft.value.changes?.activity;
        if (activity) input('SelectedActivityId').value = activity.op === 'clear' ? '' : String(activity.id);
        status.textContent = 'Restored your retained Activity draft. Save recovers its earlier action before any new command.';
    }
    const timeZoneLabel = document.getElementById('location-input-time-zone');
    if (timeZoneLabel) timeZoneLabel.textContent = inputTimeZone || 'unavailable for this retained draft';
    const draft = () => ({ initial, current: values(), explicitManual: [...manual], clearNotes: clearNotes() || restoredClear,
        detail, inputTimeZone, timeAmbiguity: timeAmbiguity.value });
    const retain = () => {
        try { retainLocationDraft(id, draft()); }
        catch (error) { status.textContent = `Not saved locally: ${error.message}`; }
    };
    form.addEventListener('input', event => {
        if (['Address', 'FullAddress', 'AddressNumber', 'StreetName', 'PostCode', 'Place', 'Region', 'Country'].includes(event.target.name)) manual.add(event.target.name);
        retain();
    });
    form.addEventListener('change', retain);
    quill.on('text-change', () => {
        // Toolbar Clear and image insertion use Quill's API source, but are still new user intent.
        restoredClear = false; retain();
    });
    form.addEventListener('submit', async event => {
        event.preventDefault();
        const buttons = [...form.querySelectorAll('button[type="submit"]')];
        if (buttons.some(button => button.disabled)) return;
        const returnAfterSave = event.submitter?.value === 'return';
        try {
            if (input('LocalTimestamp').value !== initial.LocalTimestamp && !inputTimeZone)
                throw new Error('This retained draft has no input time zone. Keep its time for review before saving.');
            const selection = locationEditCommand(initial, values(), [...manual], clearNotes() || restoredClear, inputTimeZone, timeAmbiguity.value);
            if (!Object.keys(selection.changes).length) { status.textContent = 'No changes selected.'; return; }
            const expected = Object.fromEntries(selection.groups.map(group => [group, detail.locationMutation.baseValues[group]]));
            buttons.forEach(button => { button.disabled = true; });
            status.textContent = 'Saving…';
            const result = await saveLocationMutation(id, detail, 'update', selection.changes, expected, draft());
            status.textContent = result.message;
            if (result.success) {
                if (returnAfterSave) {
                    const target = new URL(form.elements.namedItem('ReturnUrl').value || '/User/Location', location.origin);
                    location.assign(target.origin === location.origin ? target.href : '/User/Location');
                } else location.reload();
            } else if (result.terminal || result.message.includes('excluded')) {
                try {
                    inspected = await readLocationDetail(id);
                    const panel = document.getElementById('location-conflict'); panel.hidden = false; panel.open = true;
                    panel.querySelector('pre').textContent = JSON.stringify({ originals: expected, desired: selection.changes,
                        current: inspected.locationMutation.baseValues, originalCreateAddressPublication: inspected.locationMutation.originalCreateAddressPublication }, null, 2);
                } catch { /* Known applied evidence remains retained if a fresh read fails. */ }
            }
        } catch (error) { status.textContent = error.message; }
        finally { buttons.forEach(button => { button.disabled = false; }); }
    });
    document.getElementById('location-adopt-current').addEventListener('click', async () => {
        try {
            if (!inspected) return;
            await adoptLocationOriginals(id, inspected);
            detail = inspected; retain();
            status.textContent = 'You adopted the displayed current facts as new originals. Your draft is retained. Save to submit a new conditional action.';
            document.getElementById('location-conflict').hidden = true;
        } catch (error) { status.textContent = error.message; }
    });
};
