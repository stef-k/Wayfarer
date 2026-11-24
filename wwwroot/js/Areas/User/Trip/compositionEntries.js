import { message as tr } from '../../../localization/messages.js';
/** Unfinished work is independent of the ordinary paginated list: filtering never hides the resume entry point. */
const section = document.getElementById('unfinished-compositions');
const load = async () => {
    if (!section) return;
    section.replaceChildren();
    const status = document.createElement('p');
    status.textContent = tr('Editor.TripsImportLoadingUnfinishedCompositions', 'Loading unfinished compositions…');
    section.append(status);
    try {
        const response = await fetch('/api/trip-compositions', { credentials: 'same-origin', cache: 'no-store', redirect: 'error' });
        if (!response.ok) throw new Error(tr('Editor.TripsImportSavedComparisonsUnavailableSignInOr', 'Saved comparisons unavailable. Sign in or refresh to try again.'));
        const entries = await response.json();
        section.replaceChildren();
        if (!entries.length) return;
        const heading = document.createElement('h3');
        heading.className = 'h5';
        heading.textContent = tr('Editor.CompositionUnfinishedCompositions', 'Unfinished compositions');
        section.append(heading);
        for (const entry of entries) {
            const row = document.createElement('p');
            const name = document.createElement('strong');
            name.textContent = entry.name;
            const link = document.createElement('a');
            link.href = `/User/Trip/Compose/${encodeURIComponent(entry.compositionId)}`;
            link.textContent = tr('Editor.CompositionContinueComposition', 'Continue Composition');
            row.append(name, ' · ', tr('Editor.TripsImportUnfinished', 'Unfinished'), ' · ', link);
            section.append(row);
        }
    } catch {
        status.textContent = tr('Editor.TripsImportSavedComparisonsUnavailableSignInOr', 'Saved comparisons unavailable. Sign in or refresh to try again.');
        const retry = document.createElement('button');
        retry.type = 'button';
        retry.textContent = tr('Editor.TripsImportRefreshComparisons', 'Refresh comparisons');
        retry.addEventListener('click', load);
        section.replaceChildren(status, retry);
    }
};
void load();
