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
    let requiresOwnerCheck = false;
    let rechecking = false;
    const privateContent = form.closest('#location-create-private');
    const subject = document.getElementById('browser-antiforgery')?.dataset.subjectId;
    form.addEventListener('submit', async event => {
        event.preventDefault();
        if (sending || requiresOwnerCheck || !form.reportValidity()) return;
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
            if (!privateContent.isConnected) return;
            const responseSubject = page.getElementById('browser-antiforgery')?.dataset.subjectId;
            if (response.redirected || !subject || !responseSubject || subject !== responseSubject) {
                // Missing proof is uncertainty, not affirmative evidence of an account switch. Both need owner recheck.
                const foreignOwner = subject && responseSubject && subject !== responseSubject;
                requiresOwnerCheck = true;
                // Retain live map/editor state, but remove the entire private surface from display and interaction.
                privateContent.inert = true;
                privateContent.querySelectorAll('dialog[open]').forEach(dialog => dialog.close());
                privateContent.hidden = true;
                const warning = document.createElement('p');
                warning.className = 'alert alert-warning';
                warning.setAttribute('role', 'alert');
                warning.textContent = foreignOwner
                    ? 'Your session changed. This attempt remains bound to its original account. Sign back in to that account in another tab, then recheck this form. Keep this page open.'
                    : 'The save could not be confirmed, and your account could not be verified. Keep this page open and recheck the original account before retrying. If needed, sign back in to that account in another tab.';
                status.textContent = warning.textContent;
                const recheck = document.createElement('button');
                recheck.type = 'button';
                recheck.className = 'btn btn-outline-primary ms-2';
                recheck.textContent = 'Recheck original account';
                recheck.addEventListener('click', async () => {
                    if (rechecking || !requiresOwnerCheck || !privateContent.isConnected) return;
                    rechecking = true;
                    recheck.disabled = true;
                    try {
                        // Existing owner page refreshes session/antiforgery proof without allocating a create identity.
                        const proof = await fetch('/User/Location', { credentials: 'same-origin', cache: 'no-store' });
                        const ownerPage = new DOMParser().parseFromString(await proof.text(), 'text/html');
                        if (!requiresOwnerCheck || !privateContent.isConnected || !subject || !proof.ok || proof.redirected || ownerPage.getElementById('browser-antiforgery')?.dataset.subjectId !== subject) return;
                        const csrf = ownerPage.querySelector('#browser-antiforgery input[name=__RequestVerificationToken]')?.value;
                        if (!csrf) return;
                        form.elements.__RequestVerificationToken.value = csrf;
                        requiresOwnerCheck = false;
                        privateContent.inert = false;
                        privateContent.hidden = false;
                        warning.remove();
                        status.hidden = false;
                        status.textContent = 'The earlier save is unconfirmed. Retry uses its original UUID and attempted values; current form changes remain unsaved.';
                    } catch { /* Keep the private surface fenced and the original attempt intact until owner proof succeeds. */ }
                    finally { rechecking = false; recheck.disabled = false; }
                });
                warning.append(recheck);
                privateContent.before(warning);
                recheck.focus();
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
