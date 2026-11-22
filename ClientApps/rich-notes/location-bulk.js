import { message, plural } from '../../wwwroot/js/localization/messages.js';
import { mountRazorNotes } from './razor';
// JavaScript for the Bulk Edit Notes view

/** Mount the advisory preview independently of the ordinary bulk POST. */
const mountLocationBulk = () => {
    // Cache filter elements and submit button
    const countrySelect = document.getElementById('Country');
    const regionSelect = document.getElementById('Region');
    const placeSelect = document.getElementById('Place');
    const fromDateInput = document.getElementById('FromDate');
    const toDateInput = document.getElementById('ToDate');
    const applyBtn = document.getElementById('applyBtn');
    const feedback = document.getElementById('bulk-notes-feedback');

    // A failed preview cannot be mistaken for a current match count.
    const read = async url => {
        const response = await fetch(url);
        if (!response.ok) throw new Error(`Bulk Notes read returned HTTP ${response.status}`);
        return response.json();
    };

    // Utility to populate a <select> with string values
    function populate(select, items, includeAll = true) {
        select.innerHTML = '';
        if (includeAll) select.appendChild(new Option(message('Shell.All', 'All'), ''));
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
            const count = await read(`/User/Location/PreviewCount?${params}`);
            if (!Number.isInteger(count)) throw new Error('Invalid preview count');
            feedback.textContent = '';
            applyBtn.textContent = plural('Locations.ApplyCount', count, 'Apply to {0} location', 'Apply to {0} locations');
        } catch (e) {
            console.error('Failed to fetch preview count', e);
            applyBtn.textContent = message('Shell.Apply', 'Apply');
            feedback.textContent = message('Locations.PreviewFailed', 'The preview count is unavailable.');
        }
    }

    // When country changes: fetch regions, clear place, update count
    countrySelect.addEventListener('change', async () => {
        try {
            const regions = await read(`/User/Location/GetRegions?country=${encodeURIComponent(countrySelect.value)}`);
            populate(regionSelect, regions);
            populate(placeSelect, []);
            await updateCount();
        } catch (e) {
            console.error('Failed to fetch regions', e);
            feedback.textContent = message('Locations.RegionsFailed', 'Could not load regions.');
        }
    });

    // When region changes: fetch places, update count
    regionSelect.addEventListener('change', async () => {
        try {
            const params = new URLSearchParams({
                country: countrySelect.value,
                region: regionSelect.value
            });
            const places = await read(`/User/Location/GetPlaces?${params}`);
            populate(placeSelect, places);
            await updateCount();
        } catch (e) {
            console.error('Failed to fetch places', e);
            feedback.textContent = message('Locations.PlacesFailed', 'Could not load places.');
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
