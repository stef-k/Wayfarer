// segmentHandlers.js – pure store architecture
//-------------------------------------------------------------

import { store } from './storeInstance.js';
import { calculateLineDistanceKm } from '../../../map-utils.js';
import { setupQuill, waitForQuill } from './quillNotes.js';

let cachedTripId = null;
/**
 * Stores Leaflet polylines keyed by segment ID, to manage and update polylines on the map.
 * @type {Map<string, L.Polyline>}
 */
const segmentPolylines = new Map();

/**
 * Transport mode speeds in km/h used for duration estimation.
 * @constant {Object.<string, number>}
 */
const ModeSpeedsKmh = {
    walk: 5,
    bicycle: 15,
    bike: 40,
    car: 60,
    bus: 35,
    train: 100,
    ferry: 30,
    boat: 25,
    flight: 800,
    helicopter: 200
};
/**
 * Calculates estimated duration in minutes given distance and mode of transport.
 * Returns null if inputs are invalid or mode not recognized.
 *
 * @param {number} distanceKm - Distance in kilometers
 * @param {string} mode - Transport mode key (e.g., 'car', 'walk')
 * @returns {?number} Estimated duration in minutes or null if invalid
 */
const calculateDurationMinutes = (distanceKm, mode) => {
    if (!distanceKm || !mode) return null;
    const speed = ModeSpeedsKmh[mode.toLowerCase()];
    if (!speed) return null;
    return Math.round((distanceKm / speed) * 60); // minutes
};


/**
 * Adds or updates a polyline on the map for a given segment, representing the route
 * between two geographic coordinates.
 *
 * Lazy loads required mapManager methods for performance.
 *
 * @param {string} segId - Segment identifier
 * @param {[number, number]} fromCoords - [lat, lon] of start point
 * @param {[number, number]} toCoords - [lat, lon] of end point
 * @returns {Promise<L.Polyline|null>} The created polyline or null if coords missing
 */
const updateSegmentPolyline = async (segId, fromCoords, toCoords, fitMap = true) => {
    const { getMapInstance, removeLayer, addLayer, fitBounds } = await import('./mapManager.js');
    const map = getMapInstance();
    if (!map) return;

    // Remove old polyline if exists
    if (segmentPolylines.has(segId)) {
        const oldPolyline = segmentPolylines.get(segId);
        removeLayer(oldPolyline);
        segmentPolylines.delete(segId);
    }

    if (fromCoords && toCoords) {
        const latlngs = [
            [fromCoords[0], fromCoords[1]],
            [toCoords[0], toCoords[1]]
        ];

        const polyline = L.polyline(latlngs, { color: 'blue', weight: 3, dashArray: '4' });

        addLayer(polyline);
        if (fitMap) {
            fitBounds(polyline.getBounds(), { padding: [50, 50] });
        }

        segmentPolylines.set(segId, polyline);
        return polyline;
    }

    return null;
};

/**
 * Updates the estimated distance and duration inputs in the segment form
 * based on the given polyline's length and selected transport mode.
 *
 * @param {HTMLFormElement} form - The segment form element
 * @param {L.Polyline} polyline - The polyline representing the segment route
 * @returns {Promise<void>}
 */
const updateDistanceAndDuration = async (form, polyline) => {
    if (!polyline) return;

    const coords = polyline.getLatLngs().map(latlng => [latlng.lat, latlng.lng]);

    const lengthInKm = calculateLineDistanceKm(coords);

    const distInput = form.querySelector('input[name="EstimatedDistanceKm"]');
    if (distInput) distInput.value = lengthInKm.toFixed(2);

    const modeSelect = form.querySelector('select[name="Mode"]');
    const durationInput = form.querySelector('input[name="EstimatedDurationMinutes"]');

    if (modeSelect && durationInput) {
        const mode = modeSelect.value;
        const duration = calculateDurationMinutes(lengthInKm, mode);
        if (duration !== null) {
            durationInput.value = duration;
        }
    }
};

const renderAllSegmentsOnMap = async (segments) => {

    for (const segment of segments) {
        const segId = segment.id;  // also lowercase 'id'

        // Use camelCase keys exactly as returned by API
        const fromLoc = segment.fromPlace?.location;
        const toLoc = segment.toPlace?.location;
        
        if (
            !fromLoc || !toLoc ||
            fromLoc.latitude === undefined || fromLoc.longitude === undefined ||
            toLoc.latitude === undefined || toLoc.longitude === undefined
        ) {
            console.warn(`Skipping segment ${segId} due to missing coordinates`);
            continue;
        }

        const fromLatLon = [fromLoc.latitude, fromLoc.longitude];
        const toLatLon = [toLoc.latitude, toLoc.longitude];
        
        const polyline = await updateSegmentPolyline(segId, fromLatLon, toLatLon, false);

        if (polyline) {
            segmentPolylines.set(segId, polyline);

            const visibility = store.getState().segmentVisibility[segId];
            if (visibility === undefined) {
                store.dispatch('set-segment-visibility', { segmentId: segId, visible: true });
            }
        }
    }
};

/**
 * Attaches change event listeners to the FromPlace and ToPlace selects
 * in a segment form to redraw the polyline and update distance/duration.
 *
 * @param {HTMLFormElement} form - The segment form element
 */
const bindPlaceSelectChange = (form) => {
    const fromSelect = form.querySelector('select[name="FromPlaceId"]');
    const toSelect = form.querySelector('select[name="ToPlaceId"]');
    const segId = form.querySelector('input[name="Id"]')?.value;

    const redrawPolyline = async () => {
        if (!fromSelect || !toSelect) return;

        const fromOption = fromSelect.selectedOptions[0];
        const toOption = toSelect.selectedOptions[0];
        if (!fromOption || !toOption) return;

        const fromLat = parseFloat(fromOption.dataset.lat);
        const fromLon = parseFloat(fromOption.dataset.lon);
        const toLat = parseFloat(toOption.dataset.lat);
        const toLon = parseFloat(toOption.dataset.lon);

        if ([fromLat, fromLon, toLat, toLon].some(isNaN)) return;

        const polyline = await updateSegmentPolyline(segId, [fromLat, fromLon], [toLat, toLon]);
        await updateDistanceAndDuration(form, polyline);
    };

    if (fromSelect) fromSelect.addEventListener('change', redrawPolyline);
    if (toSelect) toSelect.addEventListener('change', redrawPolyline);
};


/**
 * Pans the map to the selected place in a dropdown and selects its marker.
 *
 * Lazy-loads necessary mapManager functions on demand.
 *
 * @param {HTMLSelectElement} selectEl - The place select element
 * @returns {Promise<void>}
 */
const panToPlace = async (selectEl) => {
    const { getMapInstance, getPlaceMarkerById, clearSelectedMarker, selectMarker } = await import('./mapManager.js');
    const selectedOption = selectEl.selectedOptions[0];
    if (!selectedOption) return;

    const lat = parseFloat(selectedOption.dataset.lat);
    const lon = parseFloat(selectedOption.dataset.lon);
    if (isNaN(lat) || isNaN(lon)) return;

    const map = getMapInstance();
    if (!map) return;

    map.setView([lat, lon], 13);

    const placeId = selectEl.value;
    const marker = getPlaceMarkerById(placeId);
    if (marker) {
        clearSelectedMarker();
        selectMarker(marker);
    }

    store.dispatch('set-context', {
        type: 'place',
        id: placeId,
        action: 'edit',
        meta: { name: selectedOption.text }
    });
};

/* ------------------------------------------------------------------ *
 *  Public API
 * ------------------------------------------------------------------ */

/**
 * Initializes segment handlers for the specified trip.
 * Binds add button and sets up existing segments.
 *
 * @param {string} tripId - Trip identifier
 */
export const initSegmentHandlers = async (tripId) => {
    cachedTripId = tripId;

    try {
        const resp = await fetch(`/User/Segments/GetSegments?tripId=${tripId}`);
        if (!resp.ok) throw new Error('Failed to fetch segments');

        const segments = await resp.json();

        if (segments?.length) {
            await renderAllSegmentsOnMap(segments);
        }
    } catch (err) {
        console.error('Error fetching segments:', err);
    }

    const addBtn = document.getElementById('btn-add-segment');
    if (addBtn && !addBtn.dataset.bound) {
        addBtn.dataset.bound = '1';
        addBtn.onclick = async () => {
            await loadSegmentCreateForm(tripId);
        };
    }

    bindSegmentActions?.();
    attachSegmentFormHandlers();
};

/**
 * Loads and inserts the segment creation form for a trip.
 *
 * @param {string} tripId - Trip identifier
 * @returns {Promise<void>}
 */
export const loadSegmentCreateForm = async (tripId) => {
    if (!tripId) return;

    store.dispatch('trip-cleanup-open-forms'); // 🔔

    const resp = await fetch(`/User/Segments/CreateOrUpdate?tripId=${tripId}`);
    const html = await resp.text();

    const list = document.getElementById('segments-list');
    if (!list) return;

    // Remove any open forms
    list.querySelectorAll('form[id^="segment-form-"]')
        .forEach(f => f.closest('.accordion-item')?.remove());

    // Append the new form wrapped in full accordion item
    list.insertAdjacentHTML('beforeend', html);

    // Extract new segment ID from the form
    const wrapper = list.lastElementChild;
    const segId = wrapper?.querySelector('[name="Id"]')?.value || null;
    const mode = wrapper?.querySelector('[name="Mode"]')?.value || 'New segment';

    const segmentNotesSelector = `#segment-notes-${segId}`;
    const segmentNotesInputSelector = `#Notes-${segId}`;
    const segmentFormSelector = `#segment-form-${segId}`;

    try {
        await waitForQuill(segmentNotesSelector);
        await setupQuill(segmentNotesSelector, segmentNotesInputSelector, segmentFormSelector);
    } catch (err) {
        console.error(`Failed to initialize Quill for segment ${segId}:`, err);
    }

    if (segId) {
        store.dispatch('set-context', {
            type: 'segment',
            id: segId,
            action: 'edit',
            meta: { name: mode }
        });
    }

    bindSegmentActions();
    attachSegmentFormHandlers();
};

/* ------------------------------------------------------------------ *
 *  Internal helpers
 * ------------------------------------------------------------------ */

/**
 * Binds event handlers for editing, saving, deleting, and selecting segments.
 */
const bindSegmentActions = () => {
    
    /* Toggle segment visibility */
    document.querySelectorAll('.btn-segment-toggle-visibility').forEach(checkbox => {
        const segId = checkbox.dataset.segmentId;
        if (!segId) return;

        // Initialize checkbox checked state from store or default true
        const visibility = store.getState().segmentVisibility[segId];
        checkbox.checked = visibility === undefined ? true : visibility;

        checkbox.onchange = () => {
            const newVisibility = checkbox.checked;
            store.dispatch('set-segment-visibility', {
                segmentId: segId,
                visible: newVisibility
            });
        };
    });

    /* ---------- EDIT ---------- */
    document.querySelectorAll('.btn-edit-segment').forEach(btn => {
        btn.onclick = async () => {
            const segId = btn.dataset.segmentId;
            if (!segId) return;

            store.dispatch('trip-cleanup-open-forms');

            const resp = await fetch(`/User/Segments/CreateOrUpdate?segmentId=${segId}&tripId=${cachedTripId}`);
            const html = await resp.text();

            document.getElementById(`segment-item-${segId}`).outerHTML = html;

            // Quill setup for the new form DOM
            const segmentNotesSelector = `#segment-notes-${segId}`;
            const segmentNotesInputSelector = `#Notes-${segId}`;
            const segmentFormSelector = `#segment-form-${segId}`;

            try {
                await waitForQuill(segmentNotesSelector);
                await setupQuill(segmentNotesSelector, segmentNotesInputSelector, segmentFormSelector);
            } catch (err) {
                console.error(`Failed to initialize Quill for segment ${segId}:`, err);
            }

            bindSegmentActions();
            attachSegmentFormHandlers();
        };
    });

    /* ---------- SAVE ---------- */
    document.querySelectorAll('.btn-segment-save').forEach(btn => {
        btn.onclick = async () => {
            const segId  = btn.dataset.segmentId;
            const formEl = document.getElementById(`segment-form-${segId}`);
            const fd     = new FormData(formEl);
            const token  = fd.get('__RequestVerificationToken');

            const resp = await fetch('/User/Segments/CreateOrUpdate', {
                method : 'POST',
                body   : fd,
                headers: token ? { RequestVerificationToken: token } : {}
            });

            const html    = await resp.text();
            const wrapper = formEl.closest('.accordion-item');
            wrapper.outerHTML = html;

            bindSegmentActions();
            attachSegmentFormHandlers();
            store.dispatch('clear-context');
        };
    });

    /* ---------- DELETE ---------- */
    document.querySelectorAll('.btn-delete-segment').forEach(btn => {
        btn.onclick = () => {
            const segId = btn.dataset.segmentId;
            if (!segId) return;

            wayfarer.showConfirmationModal({
                title      : 'Delete segment?',
                message    : 'This action cannot be undone.',
                confirmText: 'Delete',
                onConfirm  : async () => {
                    const fd = new FormData();
                    fd.set('__RequestVerificationToken',
                        document.querySelector('input[name="__RequestVerificationToken"]').value);

                    const resp = await fetch(`/User/Segments/Delete/${segId}`, {
                        method : 'POST',
                        body   : fd,
                        headers: { RequestVerificationToken: fd.get('__RequestVerificationToken') }
                    });

                    if (resp.ok) {
                        document.getElementById(`segment-item-${segId}`)?.remove();
                        store.dispatch('clear-context');
                    } else {
                        wayfarer.showAlert('danger', 'Failed to delete segment.');
                    }
                }
            });
        };
    });

    /* ---------- SELECT ---------- */
    document.querySelectorAll('.segment-list-item').forEach(item => {
        item.onclick = () => {
            const segId = item.dataset.segmentId;
            const name  = item.dataset.segmentName || 'Unnamed segment';
            if (!segId) return;

            // dim others
            document.querySelectorAll('.segment-list-item')
                .forEach(i => i.classList.add('dimmed'));
            item.classList.remove('dimmed');

            store.dispatch('set-context', {
                type  : 'segment',
                id    : segId,
                action: 'edit',
                meta  : { name }
            });
        };
    });
};

/**
 * Attaches handlers for segment form cancel and realtime updates for distance/time inputs.
 */
const attachSegmentFormHandlers = () => {
    /* ---------- CANCEL ---------- */
    document.querySelectorAll('.btn-segment-cancel').forEach(btn => {
        btn.onclick = async () => {
            const segId = btn.dataset.segmentId;
            if (!segId) return;

            const resp = await fetch(`/User/Segments/GetItemPartial?segmentId=${segId}`);
            const html = await resp.text();

            document.getElementById(`segment-item-${segId}`).outerHTML = html;

            bindSegmentActions();
            attachSegmentFormHandlers();
            store.dispatch('clear-context');
        };
    });

    // Add realtime duration calculation and place select change handling on all open segment forms
    document.querySelectorAll('form[id^="segment-form-"]').forEach(form => {
        const distInput = form.querySelector('input[name="EstimatedDistanceKm"]');
        const modeSelect = form.querySelector('select[name="Mode"]');
        const durationInput = form.querySelector('input[name="EstimatedDurationMinutes"]');

        if (!distInput || !modeSelect || !durationInput) return;

        const updateDuration = () => {
            const dist = parseFloat(distInput.value);
            const mode = modeSelect.value;
            if (isNaN(dist) || !mode) return;

            // Only auto-set if duration input is empty or zero
            if (!durationInput.value || durationInput.value === '0') {
                const mins = calculateDurationMinutes(dist, mode);
                if (mins !== null) {
                    durationInput.value = mins;
                }
            }
        };

        distInput.addEventListener('input', updateDuration);
        modeSelect.addEventListener('change', updateDuration);
        // also attach the pan to map to place handler
        bindPlaceSelectChange(form);
    });
};

/* ------------------------------------------------------------------ *
 *  Store listeners
 * ------------------------------------------------------------------ */


/**
 * Subscribe to store events for context changes to sync UI and scroll to segments.
 */
store.subscribe(async ({ type, payload }) => {

    // Scroll the currently-selected segment into view
    if (type === 'set-context' && payload?.type === 'segment') {
        document.getElementById(`segment-item-${payload.id}`)
            ?.scrollIntoView({ behavior: 'smooth', block: 'center' });
    }

    // If context is cleared while a form is open, restore list item
    if (type === 'clear-context') {
        const openForm  = document.querySelector('form[id^="segment-form-"]');
        const segId     = openForm?.querySelector('[name="Id"]')?.value;

        if (segId) {
            (async () => {
                const resp = await fetch(`/User/Segments/GetItemPartial?segmentId=${segId}`);
                const html = await resp.text();

                openForm.outerHTML = html;
                bindSegmentActions();
                attachSegmentFormHandlers();
            })();
        }
    }

    if ((type === 'toggle-segment-visibility' || type === 'set-segment-visibility') && payload?.segmentId) {
        const segId = payload.segmentId;
        const visible = store.getState().segmentVisibility[segId];
        const polyline = segmentPolylines.get(segId);
        if (!polyline) return;
        const { addLayer, removeLayer } = await import('./mapManager.js');
        if (visible) {
            addLayer(polyline);
        } else {
            removeLayer(polyline);
        }
    }
});
