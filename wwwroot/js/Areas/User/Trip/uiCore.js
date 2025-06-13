// uiCore.js
// Handles save trip logic and re-binding action buttons

export const saveTrip = async (action) => {
    const form = document.getElementById('trip-form');
    const formData = new FormData(form);
    formData.set('submitAction', action);

    try {
        const resp = await fetch(form.action, {
            method: 'POST',
            body: formData,
            headers: {
                'X-CSRF-TOKEN': formData.get('__RequestVerificationToken')
            }
        });

        if (resp.redirected) {
            window.location.href = resp.url;
        } else {
            const html = await resp.text();
            const parser = new DOMParser();
            const doc = parser.parseFromString(html, 'text/html');
            const newForm = doc.querySelector('#trip-form');

            if (newForm) {
                form.replaceWith(newForm);
                rebindMainButtons(); // re-attach after replacing DOM
            }
        }
    } catch (err) {
        console.error(err);
        alert('Error saving trip.');
    }
};

export const refreshTripDays = async () => {
    const tripId = document.querySelector('#trip-form input[name="Id"]')?.value;
    if (!tripId) return;

    try {
        const resp = await fetch(`/User/Trip/GetTripDays?tripId=${tripId}`);
        if (!resp.ok) throw new Error('Failed to fetch trip days');

        const days = await resp.text();
        const daysInput = document.querySelector('#trip-form input[name="Days"]');
        if (daysInput) daysInput.value = days;
    } catch (err) {
        console.warn('Could not update trip day count:', err);
    }
};


export const rebindMainButtons = () => {
    document.getElementById('btn-save-trip')?.addEventListener('click', () => saveTrip('save'));
    document.getElementById('btn-save-edit-trip')?.addEventListener('click', () => saveTrip('save-edit'));
};
