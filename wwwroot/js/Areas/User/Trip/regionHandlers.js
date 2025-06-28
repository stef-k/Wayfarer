// regionHandlers.js – pure store architecture (new region prepend + rebind place handlers)
/**
 * Manages region-related UI and interactions.
 * - Add/Edit/Delete regions.
 * - Add/Cancel/Save region forms.
 * - Listeners for selecting region areas.
 * - Integrates with central store for context and events.
 */
import { focusMapView } from './mapZoom.js'
import { enhancePlaceForm, initPlaceHandlers } from './placeHandlers.js';
import { getMapInstance, getRegionMarkerById, removeRegionMarker, selectMarker, clearSelectedMarker } from './mapManager.js';
import { store } from './storeInstance.js';

let cachedTripId = null;

/**
 * Initializes all region-related handlers for the given trip.
 * @param {string} tripId
 */
export const initRegionHandlers = (tripId) => {
    cachedTripId = tripId;

    // “Add region” button
    const addBtn = document.getElementById('btn-add-region');
    if (addBtn) addBtn.onclick = async () => {
        store.dispatch('trip-cleanup-open-forms');

        const resp = await fetch(`/User/Regions/CreateOrUpdate?tripId=${tripId}`);
        const html = await resp.text();

        const list = document.getElementById('regions-accordion');
        if (!list) return;

        // PREPEND new region form to top
        list.insertAdjacentHTML('afterbegin', html);

        const newForm = list.firstElementChild;
        const newRegionId = newForm?.id?.replace('region-form-', '');
        if (newRegionId) {
            try {
                const { setupQuill, waitForQuill } = await import('./quillNotes.js');
                await waitForQuill(`#region-notes-${newRegionId}`);
                await setupQuill(
                    `#region-notes-${newRegionId}`,
                    `#Notes-${newRegionId}`,
                    `#region-form-${newRegionId}`
                );
            } catch (err) {
                console.error(`❌ Failed to init Quill for new region ${newRegionId}:`, err);
            }

            store.dispatch('set-context', {
                type: 'region',
                id: newRegionId,
                action: 'set-center',
                meta: { name: 'New region' }
            });
        }

        // Rebind handlers after DOM insertion
        initRegionHandlers(tripId);
        initPlaceHandlers();
        attachRegionFormHandlers();
    };

    // Edit region buttons
    document.querySelectorAll('.btn-edit-region').forEach(btn => {
        if (btn.dataset.bound) return;
        btn.dataset.bound = '1';
        btn.onclick = () => handleEditRegion(btn.dataset.regionId, tripId);
    });

    // Delete region buttons
    document.querySelectorAll('.btn-delete-region').forEach(btn => {
        if (btn.dataset.bound) return;
        btn.dataset.bound = '1';
        btn.onclick = () => handleDeleteRegion(btn.dataset.regionId);
    });

    // Add place buttons
    document.querySelectorAll('.btn-add-place').forEach(btn => {
        if (btn.dataset.bound) return;
        btn.dataset.bound = '1';
        btn.onclick = () => handleAddPlace(btn.dataset.regionId);
    });

    // Region area selection
    document.querySelectorAll('.region-select-area').forEach(btn => {
        if (btn.dataset.bound) return;
        btn.dataset.bound = '1';
        btn.onclick = (e) => {
            e.preventDefault();
            e.stopPropagation();

            const wrapper = btn.closest('.accordion-item');
            const lat = parseFloat(wrapper?.dataset.centerLat);
            const lon = parseFloat(wrapper?.dataset.centerLon);
            if (!Number.isNaN(lat) && !Number.isNaN(lon)) {
                const map = getMapInstance();
                focusMapView('region', [lat, lon], map);
            }

            const regionId = btn.dataset.regionId;
            const regionName = btn.dataset.regionName || 'Unnamed Region';
            if (!regionId) return;

            document.querySelectorAll('.accordion-item').forEach(el => el.classList.add('dimmed'));
            wrapper.classList.remove('dimmed');

            store.dispatch('set-context', {
                type: 'region',
                id: regionId,
                action: 'set-center',
                meta: { name: regionName }
            });

            clearSelectedMarker();
            const marker = getRegionMarkerById(regionId);
            if (marker) selectMarker(marker);
        };
    });

    attachRegionFormHandlers();
};

/**
 * Handles deleting a region after user confirmation.
 * @param {string} regionId
 */
const handleDeleteRegion = (regionId) => {
    showConfirmationModal({
        title: 'Delete Region?',
        message: 'Are you sure you want to permanently delete this region and all its places?',
        confirmText: 'Delete',
        onConfirm: async () => {
            const fd = new FormData();
            fd.set('__RequestVerificationToken', document.querySelector('input[name="__RequestVerificationToken"]').value);

            const resp = await fetch(`/User/Regions/Delete/${regionId}`, {
                method: 'POST',
                body: fd
            });

            if (resp.ok) {
                document.getElementById(`region-item-${regionId}`)?.remove();
                removeRegionMarker(regionId);
                store.dispatch('clear-context');
                const tripId = store.getState().tripId;
                if (tripId) {
                    const { initSegmentHandlers } = await import('./segmentHandlers.js');
                    await initSegmentHandlers(tripId);
                }
                showAlert('success', 'Region deleted.');
            } else {
                showAlert('danger', 'Failed to delete region.');
            }
        }
    });
};

/**
 * Adds a new place form to the specified region and rebinds handlers.
 * @param {string} regionId
 */
const handleAddPlace = async (regionId) => {
    store.dispatch('trip-cleanup-open-forms');

    const resp = await fetch(`/User/Places/CreateOrUpdate?regionId=${regionId}`);
    const html = await resp.text();
    const regionItem = document.getElementById(`region-item-${regionId}`);
    const placesContainer = regionItem.querySelector('[data-region-places]');
    if (placesContainer) {
        placesContainer.insertAdjacentHTML('afterbegin', html);  // ✅ correctly inserts within the region
    } else {
        console.warn('⚠️ Region places container not found, fallback used.');
        regionItem.insertAdjacentHTML('beforeend', html);  // fallback to safe default
    }

    const formEl = regionItem.querySelector('[id^="place-form-"]');
    let newPlaceId = null;
    if (formEl) {
        await enhancePlaceForm(formEl);
        newPlaceId = formEl.id.replace('place-form-', '');
    }

    // Rebind place handlers so Cancel works correctly
    initPlaceHandlers();

    if (newPlaceId) {
        store.dispatch('set-context', {
            type: 'place',
            id: newPlaceId,
            action: 'set-location',
            meta: { name: 'New place', regionId }
        });
    }

    store.dispatch('region-dom-reloaded', { regionId });
};

/**
 * Loads and enhances the region edit form.
 * @param {string} regionId
 * @param {string} tripId
 */
const handleEditRegion = async (regionId, tripId) => {
    if (!regionId || !tripId) return;

    const wrapper = document.getElementById(`region-item-${regionId}`);
    const resp = await fetch(`/User/Regions/CreateOrUpdate?regionId=${regionId}&tripId=${tripId}`);
    const html = await resp.text();
    wrapper.outerHTML = html;

    // Rebind handlers on new markup
    initRegionHandlers(tripId);
    initPlaceHandlers();

    // Quill setup
    try {
        const { setupQuill, waitForQuill } = await import('./quillNotes.js');
        await waitForQuill(`#region-notes-${regionId}`);
        await setupQuill(`#region-notes-${regionId}`, `#Notes-${regionId}`, `#region-form-${regionId}`);
    } catch (err) {
        console.error(`❌ Failed to init Quill for region ${regionId}:`, err);
    }

    store.dispatch('region-dom-reloaded', { regionId });
    store.dispatch('set-context', {
        type: 'region',
        id: regionId,
        action: 'edit',
        meta: { name: wrapper?.getAttribute('data-region-name')?.trim() || 'Unnamed Region' }
    });
};

/**
 * Binds cancel & save buttons inside region forms.
 */
const attachRegionFormHandlers = () => {
    document.querySelectorAll('.btn-region-cancel').forEach(btn => {
        if (btn.dataset.bound) return;
        btn.dataset.bound = '1';
        btn.onclick = async () => {
            const regionId = btn.dataset.regionId;
            const wrapper = btn.closest('.accordion-item');

            // Cancel new region
            if (!regionId) {
                wrapper.remove();
                store.dispatch('clear-context');
                return;
            }

            // Reload original region item
            const resp = await fetch(`/User/Regions/GetItemPartial?regionId=${regionId}`);
            wrapper.outerHTML = await resp.text();

            store.dispatch('region-dom-reloaded', { regionId });
            store.dispatch('clear-context');
        };
    });

    document.querySelectorAll('.btn-region-save').forEach(btn => {
        if (btn.dataset.bound) return;
        btn.dataset.bound = '1';
        btn.onclick = async () => {
            const regionId = btn.dataset.regionId;
            const formEl = document.getElementById(`region-form-${regionId}`);
            const fd = new FormData(formEl);

            const resp = await fetch('/User/Regions/CreateOrUpdate', {
                method: 'POST',
                body: fd,
                headers: { 'X-CSRF-TOKEN': fd.get('__RequestVerificationToken') }
            });

            const html = await resp.text();
            const wrapper = formEl.closest('.accordion-item, form');

            if (resp.ok) {
                wrapper.outerHTML = html;
                const newRegionId = wrapper.id.replace('region-item-', '');
                if (newRegionId) store.dispatch('region-dom-reloaded', { regionId: newRegionId });
                store.dispatch('clear-context');
            } else {
                wrapper.outerHTML = html;
                attachRegionFormHandlers();

                const dom = new DOMParser().parseFromString(html, 'text/html');
                const errors = Array.from(dom.querySelectorAll('.region-form-errors ul li'))
                    .map(li => li.textContent.trim());
                if (errors.length) showAlert('danger', errors.join('\n'));
            }
        };
    });
};

/**
 * Store subscribers for auto-scrolling and form restore on clear.
 */
store.subscribe(({ type, payload }) => {
    // Scroll to region on select
    if (type === 'set-context' && payload?.type === 'region') {
        document.getElementById(`region-item-${payload.id}`)
            ?.scrollIntoView({ behavior: 'smooth', block: 'center' });
    }

    // Restore open form on clear-context
    if (type === 'clear-context') {
        const openForm = document.querySelector('form[id^="region-form-"]');
        const regionId = openForm?.querySelector('[name="Id"]')?.value;
        if (!regionId) return;
        (async () => {
            const resp = await fetch(`/User/Regions/GetItemPartial?regionId=${regionId}`);
            openForm.outerHTML = await resp.text();
            store.dispatch('region-dom-reloaded', { regionId });
        })();
    }
});
