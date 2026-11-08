/** Ordinary form retry state: one UUID and one attempted payload, with current authored values kept separately. */
const numeric = ['Latitude', 'Longitude', 'SelectedActivityId', 'Accuracy', 'Altitude', 'Speed'];
const text = ['LocalTimestamp', 'Notes', 'Address', 'FullAddress', 'AddressNumber', 'StreetName', 'PostCode', 'Place', 'Region', 'Country'];
const lower = name => name[0].toLowerCase() + name.slice(1);

/** Freezes the permitted Web creation fields before sending; empty measurements remain unknown rather than zero. */
export const createValues = data => Object.fromEntries([...numeric, ...text].map(name => [lower(name),
    numeric.includes(name) ? data.get(name) === '' || data.get(name) == null ? null : Number(data.get(name)) : data.get(name) ?? null]));

/** Uses the existing POST and returned MVC page; no persistent queue or alternate creation endpoint. */
export const attachLocationCreate = (form, notesValue, redisplay) => {
    let sending = false;
    let accountChanged = false;
    form.addEventListener('submit', async event => {
        event.preventDefault();
        if (sending || accountChanged || !form.reportValidity()) return;
        sending = true;
        const read = () => {
            form.elements.Notes.value = notesValue();
            return new FormData(form);
        };
        const current = read();
        const attempted = form.elements.AttemptedValues;
        if (!attempted.value) attempted.value = JSON.stringify(createValues(current));
        current.set('AttemptedValues', attempted.value);
        // The outgoing flag describes earlier uncertainty. The live form is uncertain as soon as transport starts.
        form.elements.CreateMayHaveEffect.value = 'true';
        const status = form.querySelector('#create-status');
        status.hidden = false;
        status.textContent = 'Saving. Current form changes remain available until the result is confirmed.';
        try {
            const response = await fetch(form.action, { method: 'POST', body: current, credentials: 'same-origin', cache: 'no-store' });
            const html = await response.text();
            const page = new DOMParser().parseFromString(html, 'text/html');
            const subject = document.getElementById('browser-antiforgery')?.dataset.subjectId;
            if (response.redirected || subject !== page.getElementById('browser-antiforgery')?.dataset.subjectId) {
                accountChanged = true;
                form.hidden = true;
                const warning = document.createElement('p');
                warning.className = 'alert alert-warning';
                warning.setAttribute('role', 'alert');
                warning.textContent = 'Your session changed. This attempt remains bound to its original account. Sign in to that account before recovering it; keep this page open.';
                form.before(warning);
                return;
            }
            const next = page.querySelector('form[action$="Create"]');
            if (!response.ok || !next) throw new Error('Unconfirmed save');
            const latest = read();
            if (JSON.stringify(createValues(latest)) !== JSON.stringify(createValues(current))) {
                // Typing during transport must survive even when the server already confirmed the original payload.
                for (const name of [...numeric, ...text]) if (next.elements[name]) next.elements[name].value = latest.get(name) ?? '';
                const notes = page.getElementById('notes');
                if (notes) notes.dataset.notesContent = latest.get('Notes') ?? '';
                const message = page.getElementById('create-status');
                message.hidden = false;
                message.textContent = 'Your current form changes were not applied and remain unsaved. Open the saved location and use Edit, or retry the original save if it is still unconfirmed.';
            }
            // Reuse the rendered MVC content and the existing Create mount owner; unrelated layout state stays intact.
            const content = page.querySelector('.site-content');
            if (!content) throw new Error('Unconfirmed save');
            redisplay(content);
        } catch {
            status.hidden = false;
            form.querySelector('button[type=submit]').textContent = 'Retry original save';
            status.textContent = 'The save could not be confirmed. Retry uses the original attempted values and UUID. Current form changes remain unsaved.';
        } finally { sending = false; }
    });
};
