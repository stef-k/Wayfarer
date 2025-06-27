// placeHandlers.js – full file with robust “cancel new place” fix
import {
    renderPlaceMarker, removePlaceMarker, getMapInstance, getPlaceMarkerById, clearSelectedMarker, selectMarker
} from './mapManager.js';
import {
    populateIconDropdown, populateColorDropdown, updateDropdownIconColors, clearDim, dimAll
} from './uiCore.js';
import {store} from './storeInstance.js';

/**
 * Returns a clean place title from a list-item or form element.
 * Falls back to DOM text if the data-attr is missing.
 */
const getPlaceName = (el) => {
    const raw = (el?.dataset?.placeName ??                 // preferred
        el?.querySelector('.place-name')?.textContent ?? '').trim();
    return raw || 'Unnamed';
};

export const initPlaceHandlers = () => {
    attachPlaceFormHandlers();
};

export const enhancePlaceForm = async (formEl) => {
    const placeId = formEl.dataset?.placeId || formEl.id.replace('place-form-', '');
    const selector = `#place-notes-${placeId}`;
    const inputSelector = `#Notes-${placeId}`;
    const formSelector = `#place-form-${placeId}`;

    try {
        const {setupQuill, waitForQuill} = await import('./quillNotes.js');
        await waitForQuill(selector);
        await setupQuill(selector, inputSelector, formSelector);
    } catch (err) {
        console.warn(`❌ Failed to init Quill for place ${placeId}:`, err);
    }

    const iconMenu = formEl.querySelector('.icon-dropdown-menu');
    if (iconMenu) {
        await populateIconDropdown(iconMenu);
    } else {
        console.warn('⚠️ No icon menu found in form');
    }

    await populateColorDropdown(formEl);
    const currentColor = formEl.querySelector('input[name="MarkerColor"]')?.value || 'bg-blue';
    updateDropdownIconColors(formEl, currentColor);
};

const attachPlaceFormHandlers = () => {

    document.querySelectorAll('.btn-place-cancel').forEach(btn => {
        btn.onclick = async () => {
            // 1) Find the surrounding form to read hidden inputs if needed
            const formEl = btn.closest('form[id^="place-form-"]');
            const placeId = btn.dataset.placeId || formEl?.querySelector('[name="Id"]')?.value;
            const regionId = btn.dataset.regionId || formEl?.querySelector('[name="RegionId"]')?.value;
            if (!regionId) return; // nothing we can reload

            const existingItem = document.querySelector(`.place-list-item[data-place-id="${placeId}"]`);
            const isNew = !existingItem; // no existing list item means this is truly new
            const context = store.getState().context;

            // 2) Subscribe before region reload so we don't miss the event
            store.subscribeOnce(({
                                     type,
                                     payload
                                 }) => type === 'region-dom-reloaded' && payload.regionId === regionId, () => {
                const item = document.querySelector(`.place-list-item[data-place-id="${placeId}"]`);
                if (!item) {
                    store.dispatch('clear-context');
                    return;
                }
                const name = getPlaceName(item);
                const {placeLat: lat, placeLon: lon, placeIcon: icon, placeColor: color} = item.dataset;
                if (lat && lon) {
                    renderPlaceMarker({
                        Id: placeId,
                        Name: name,
                        Latitude: lat,
                        Longitude: lon,
                        IconName: icon,
                        MarkerColor: color,
                        RegionId: regionId
                    });
                }
                store.dispatch('set-context', {
                    type: 'place', id: placeId, action: 'set-location', meta: {name, regionId}
                });
            });

            // 3) Reload the entire region accordion item
            const regionEl = document.getElementById(`region-item-${regionId}`);
            if (regionEl) {
                const resp = await fetch(`/User/Regions/GetItemPartial?regionId=${regionId}`);
                regionEl.outerHTML = await resp.text();
                store.dispatch('region-dom-reloaded', {regionId});
            }

            // 4) Remove any temporary marker
            if (placeId) {
                const {clearPreviewMarker} = await import("./mapManager.js");
                clearPreviewMarker();
            }

            // 5) If it was a new place (or context mismatches), clear the context banner
            if (isNew || context?.id !== placeId) {
                store.dispatch('clear-context');
                return;
            }
        };
    });

    /* ─────────────────────────────────────────────────────────────── *
     *  EDIT
     * ─────────────────────────────────────────────────────────────── */
    document.querySelectorAll('.btn-edit-place').forEach(btn => {
        btn.onclick = async () => {
            const {placeId, regionId} = btn.dataset;
            if (!placeId || !regionId) return;

            store.dispatch('trip-cleanup-open-forms');
            const resp = await fetch(`/User/Places/Edit/${placeId}`);
            const html = await resp.text();

            const item = document.querySelector(`.place-list-item[data-place-id="${placeId}"]`);
            if (!item) return;
            item.outerHTML = html;

            const formEl = document.getElementById(`place-form-${placeId}`);
            if (formEl) await enhancePlaceForm(formEl);
            attachPlaceFormHandlers();

            const name = formEl.querySelector('input[name="Name"]')?.value || 'Unnamed';
            store.dispatch('set-context', {
                type: 'place', id: placeId, action: 'edit', meta: {name, regionId}
            });
        };
    });

    /* ─────────────────────────────────────────────────────────────── *
     *  SAVE
     * ─────────────────────────────────────────────────────────────── */
    document.querySelectorAll('.btn-place-save').forEach(btn => {
        btn.onclick = async () => {
            const { placeId } = btn.dataset;
            const formEl = document.getElementById(`place-form-${placeId}`);
            const oldLat = parseFloat(formEl.dataset.oldLat);
            const oldLon = parseFloat(formEl.dataset.oldLon);

            const fd = new FormData(formEl);
            const token = fd.get('__RequestVerificationToken');

            const resp = await fetch('/User/Places/CreateOrUpdate', {
                method: 'POST',
                body: fd,
                credentials: 'same-origin',
                headers: token ? { RequestVerificationToken: token } : {}
            });

            const html = await resp.text();
            const wrapper = formEl.closest('.accordion-item');
            wrapper.outerHTML = html;
            attachPlaceFormHandlers();

            if (!resp.ok) {
                const dom = new DOMParser().parseFromString(html, 'text/html');
                const errors = [...dom.querySelectorAll('.place-form-errors ul li')]
                    .map(li => li.textContent.trim());
                wayfarer.showAlert('danger', errors.join('\n'));
                return;
            }

            store.dispatch('clear-context');

            // 🧭 Extend route if location changed
            const newItem = document.querySelector(`.place-list-item[data-place-id="${placeId}"]`);
            const newLat = parseFloat(newItem?.dataset.placeLat);
            const newLon = parseFloat(newItem?.dataset.placeLon);

            if (!isNaN(oldLat) && !isNaN(oldLon) && !isNaN(newLat) && !isNaN(newLon) &&
                (oldLat !== newLat || oldLon !== newLon)) {
                const { extendSegmentRouteForMovedPlace } = await import('./segmentHandlers.js');
                await extendSegmentRouteForMovedPlace(placeId, oldLat, oldLon, newLat, newLon);
            }

            // ✅ Reload and re-render segment polylines
            const tripId = document.querySelector('#trip-form [name="Id"]')?.value;
            if (tripId) {
                const segResp = await fetch(`/User/Segments/GetSegments?tripId=${tripId}`);
                if (segResp.ok) {
                    const segments = await segResp.json();
                    const { renderAllSegmentsOnMap } = await import('./segmentHandlers.js');
                    await renderAllSegmentsOnMap(segments);
                } else {
                    console.warn('⚠️ Failed to fetch segments after place update');
                }
            }
        };
    });

    /* ─────────────────────────────────────────────────────────────── *
     *  SELECT in list
     * ─────────────────────────────────────────────────────────────── */
    document.querySelectorAll('.place-list-item').forEach(li => {
        li.onclick = () => {
            const {placeId, regionId, placeLat: lat, placeLon: lon, placeIcon: icon, placeColor: color} = li.dataset;
            const name = getPlaceName(li);
            if (!placeId || !regionId) return;

            dimAll();
            document.getElementById(`region-item-${regionId}`)?.classList.remove('dimmed');
            li.classList.remove('dimmed');

            store.dispatch('set-context', {
                type: 'place', id: placeId, action: 'set-location', meta: {name, regionId}
            });

            if (lat && lon) {
                renderPlaceMarker({
                    Id: placeId,
                    Name: name,
                    Latitude: lat,
                    Longitude: lon,
                    IconName: icon,
                    MarkerColor: color,
                    RegionId: regionId
                });
                getMapInstance()?.setView([lat, lon], 10);
                clearSelectedMarker();
                const marker = getPlaceMarkerById(placeId);
                if (marker) selectMarker(marker);
            }
        };
    });

    /* ─────────────────────────────────────────────────────────────── *
     *  DELETE
     * ─────────────────────────────────────────────────────────────── */
    document.querySelectorAll('.btn-delete-place').forEach(btn => {
        btn.onclick = () => {
            const {placeId, regionId} = btn.dataset;
            if (!placeId) return;

            wayfarer.showConfirmationModal({
                title: 'Delete place?',
                message: 'This action cannot be undone.',
                confirmText: 'Delete',
                onConfirm: async () => {
                    const fd = new FormData();
                    fd.set('__RequestVerificationToken', document.querySelector('input[name="__RequestVerificationToken"]').value);

                    const resp = await fetch(`/User/Places/Delete/${placeId}`, {
                        method: 'POST',
                        body: fd,
                        headers: {RequestVerificationToken: fd.get('__RequestVerificationToken')}
                    });

                    if (resp.ok) {
                        const html = await resp.text();
                        document.getElementById(`region-item-${regionId}`).outerHTML = html;
                        store.dispatch('region-dom-reloaded', {regionId});
                        removePlaceMarker(placeId);
                        store.dispatch('clear-context');
                        const tripId = document.querySelector('#trip-form [name="Id"]')?.value;
                        if (tripId) {
                            const { initSegmentHandlers } = await import('./segmentHandlers.js');
                            await initSegmentHandlers(tripId);
                        }
                    } else {
                        wayfarer.showAlert('danger', 'Failed to delete place.');
                    }
                }
            });
        };
    });
};

/* ─────────────────────────────────────────────────────────────── *
 *  Ensure any open form is torn down on clear-context
 * ─────────────────────────────────────────────────────────────── */
store.subscribe(({type}) => {
    if (type === 'clear-context') {
        const openForm = document.querySelector('form[id^="place-form-"]');
        const placeId = openForm?.querySelector('[name="Id"]')?.value;
        const regionId = openForm?.querySelector('[name="RegionId"]')?.value;

        if (placeId && regionId) {
            (async () => {
                const regionEl = document.getElementById(`region-item-${regionId}`);
                if (!regionEl) return;
                const resp = await fetch(`/User/Regions/GetItemPartial?regionId=${regionId}`);
                regionEl.outerHTML = await resp.text();
                store.dispatch('region-dom-reloaded', {regionId});
                removePlaceMarker(placeId);
            })();
        }
    }
});
