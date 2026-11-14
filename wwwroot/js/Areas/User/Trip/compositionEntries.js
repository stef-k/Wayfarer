/** Unfinished work is independent of the ordinary paginated list: filtering never hides the resume entry point. */
const section = document.getElementById('unfinished-compositions');
const load = async () => {
    if (!section) return;
    section.replaceChildren();
    const status = document.createElement('p');
    status.textContent = 'Loading unfinished compositions…';
    section.append(status);
    try {
        const response = await fetch('/api/trip-compositions', { credentials: 'same-origin', cache: 'no-store', redirect: 'error' });
        if (!response.ok) throw new Error('Saved comparisons unavailable. Sign in or refresh to try again.');
        const entries = await response.json();
        section.replaceChildren();
        if (!entries.length) return;
        const heading = document.createElement('h3');
        heading.className = 'h5';
        heading.textContent = 'Unfinished compositions';
        section.append(heading);
        for (const entry of entries) {
            const row = document.createElement('p');
            const name = document.createElement('strong');
            name.textContent = entry.name;
            const link = document.createElement('a');
            link.href = `/User/Trip/Compose/${encodeURIComponent(entry.compositionId)}`;
            link.textContent = 'Continue Composition';
            row.append(name, ' · Unfinished · ', link);
            section.append(row);
        }
    } catch (error) {
        status.textContent = error instanceof Error ? error.message : 'Saved comparisons unavailable.';
        const retry = document.createElement('button');
        retry.type = 'button';
        retry.textContent = 'Refresh comparisons';
        retry.addEventListener('click', load);
        section.replaceChildren(status, retry);
    }
};
void load();
