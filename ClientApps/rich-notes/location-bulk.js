import { mountRazorNotes } from './razor';
// JavaScript for the Bulk Edit Notes view

const mountLocationBulk = () => {
    // Cache filter elements and submit button
    const countrySelect = document.getElementById('Country');
    const regionSelect = document.getElementById('Region');
    const placeSelect = document.getElementById('Place');
    const fromDateInput = document.getElementById('FromDate');
    const toDateInput = document.getElementById('ToDate');
    const applyBtn = document.getElementById('applyBtn');

    // Utility to populate a <select> with string values
    function populate(select, items, includeAll = true) {
        select.innerHTML = '';
        if (includeAll) select.appendChild(new Option('All', ''));
        items.forEach(text => select.appendChild(new Option(text, text)));
    }

    // Call preview-count endpoint and update button
    async function updateCount() {
        const params = new URLSearchParams();
        if (countrySelect.value) params.append('country', countrySelect.value);
        if (regionSelect.value) params.append('region', regionSelect.value);
        if (placeSelect.value) params.append('place', placeSelect.value);

        const fromDate = fromDateInput.value;
        const toDate = toDateInput.value;

        // Only include if valid ISO date string
        if (fromDate && !isNaN(Date.parse(fromDate))) params.append('fromDate', fromDate);
        if (toDate && !isNaN(Date.parse(toDate))) params.append('toDate', toDate);

        try {
            const resp = await fetch(`/User/Location/PreviewCount?${params}`);
            const count = await resp.json();
            applyBtn.textContent = `Apply to ${count} locations`;
        } catch (e) {
            console.error('Failed to fetch preview count', e);
        }
    }

    // When country changes: fetch regions, clear place, update count
    countrySelect.addEventListener('change', async () => {
        try {
            const resp = await fetch(`/User/Location/GetRegions?country=${encodeURIComponent(countrySelect.value)}`);
            const regions = await resp.json();
            populate(regionSelect, regions);
            populate(placeSelect, []);
            await updateCount();
        } catch (e) {
            console.error('Failed to fetch regions', e);
        }
    });

    // When region changes: fetch places, update count
    regionSelect.addEventListener('change', async () => {
        try {
            const params = new URLSearchParams({
                country: countrySelect.value,
                region: regionSelect.value
            });
            const resp = await fetch(`/User/Location/GetPlaces?${params}`);
            const places = await resp.json();
            populate(placeSelect, places);
            await updateCount();
        } catch (e) {
            console.error('Failed to fetch places', e);
        }
    });

    // When place or dates change: update count
    [placeSelect, fromDateInput, toDateInput].forEach(el => el.addEventListener('change', updateCount));

    // Initial count
    updateCount();

    // Filters and advisory counts are independent of the ordinary Apply-time POST.
    // Clearing the editor does not select the separate ClearNotes operation.
    const notes = mountRazorNotes(document.getElementById('notes'));
    document.getElementById('locationForm').addEventListener('submit', () => {
        document.getElementById('hiddenNotes').value = notes.isClear() ? '' : notes.getValue();
    });
    window.addEventListener('pagehide', event => { if (!event.persisted) notes.dispose(); });
};

if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', mountLocationBulk, { once: true });
else mountLocationBulk();
