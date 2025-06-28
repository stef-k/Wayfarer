// segmentHandlers.js
//-------------------------------------------------------------

import { store } from './storeInstance.js';
import { focusMapView } from './mapZoom.js'
import { calculateLineDistanceKm } from '../../../map-utils.js';
import { setupQuill, waitForQuill } from './quillNotes.js';

let cachedInitOrdering = null;
let cachedTripId = null;
const drawnSegmentPolylines = new Map();

const scrollToSegment = (segId) => {
    const el = document.querySelector(`.segment-list-item[data-segment-id="${segId}"]`);
    if (!el) return;

    const collapse = el.closest('.accordion-collapse');
    if (collapse && !collapse.classList.contains('show')) {
        const bsCollapse = bootstrap.Collapse.getOrCreateInstance(collapse);
        bsCollapse.show();
    }

    setTimeout(() => {
        el.scrollIntoView({ behavior: 'smooth', block: 'center' });
    }, 50);
};

/**
 * Renders a static (non-editable) segment route on the map.
 * Uses RouteJson if available, else falls back to From/To coordinates.
 *
 * @param {string} segId - Segment ID
 * @param {HTMLElement} wrapperEl - The DOM element containing segment data
 */
const renderStaticSegmentRoute = async (segId, wrapperEl) => {
    if (!wrapperEl) return;

    const rawJson = wrapperEl.querySelector('input[name="RouteJson"]')?.value;
    let coords = [];

    if (rawJson && rawJson.trim() && rawJson !== '[]') {
        try {
            const parsed = JSON.parse(rawJson);
            if (Array.isArray(parsed) && parsed.length >= 2) {
                coords = parsed;
            }
        } catch {
            coords = [];
        }
    }

    const { addLayer, removeLayer } = await import('./mapManager.js');

    // 🔄 Remove old polyline if it exists
    if (segmentPolylines.has(segId)) {
        removeLayer(segmentPolylines.get(segId));
        segmentPolylines.delete(segId);
    }

    // ✅ Render polyline from RouteJson
    if (coords.length >= 2) {
        const poly = L.polyline(coords, {
            color: 'blue',
            weight: 3,
            className: 'segment-polyline'
        });

        // 🏷️ Tooltip from data attributes
        const mode = wrapperEl.dataset.segmentMode || 'unknown';
        const from = wrapperEl.dataset.segmentFrom || 'Start';
        const to = wrapperEl.dataset.segmentTo || 'End';
        const dist = wrapperEl.dataset.segmentDistance || '?';

        let mins = null;
        const durStr = wrapperEl.dataset.segmentDuration;
        if (durStr) {
            const [hh, mm, ss] = durStr.split(':').map(Number);
            mins = Math.round(hh * 60 + mm + ss / 60);
        }

        const time = mins ? `in ~${mins} min` : '';
        const capitalizedName = mode.charAt(0).toUpperCase() + mode.slice(1);
        const tooltipText = `${capitalizedName} <br>From: ${from} <br>To: ${to}<br>Distance: ${dist} km ${time}`;

        poly.bindTooltip(tooltipText, {
            sticky: true,
            direction: 'top',
            className: 'segment-tooltip'
        });

        poly.on('click', async () => {
            const name = `${wrapperEl.dataset.segmentMode || 'Segment'}: ${wrapperEl.dataset.segmentFrom || 'Start'} → ${wrapperEl.dataset.segmentTo || 'End'}`;

            store.dispatch('set-context', {
                type: 'segment',
                id: segId,
                action: 'edit',
                meta: { name }
            });

            const el = document.getElementById(`segment-item-${segId}`);
            scrollToSegment(segId);

            if (el) {
                el.classList.add('segment-highlight');
                setTimeout(() => el.classList.remove('segment-highlight'), 1000);
            }

            // Map focus and flash
            const { getMapInstance } = await import('./mapManager.js');
            const map = getMapInstance();
            if (map && poly.getBounds) {
                map.fitBounds(poly.getBounds(), { padding: [50, 50] });
                const originalColor = poly.options.color;
                poly.setStyle({ color: 'yellow', weight: 5 });
                setTimeout(() => poly.setStyle({ color: originalColor, weight: 3 }), 500);
            }
        });

        addLayer(poly);
        segmentPolylines.set(segId, poly);
        return;
    }

    // 🔁 Fallback to from→to straight line
    const fromEl = wrapperEl.hasAttribute('data-from-lat') ? wrapperEl : wrapperEl.querySelector('[data-from-lat]');
    const toEl = wrapperEl.hasAttribute('data-to-lat') ? wrapperEl : wrapperEl.querySelector('[data-to-lat]');
    if (fromEl && toEl) {
        const from = [parseFloat(fromEl.dataset.fromLat), parseFloat(fromEl.dataset.fromLon)];
        const to = [parseFloat(toEl.dataset.toLat), parseFloat(toEl.dataset.toLon)];
        if (from.every(c => !isNaN(c)) && to.every(c => !isNaN(c))) {
            await updateSegmentPolyline(segId, from, to, false);
        }
    }
};

/**
 * Render an editable polyline for the current segment being edited.
 * Falls back to From/To straight line if no RouteJson exists.
 */
const renderEditableSegmentRoute = async (segId, formEl) => {
    const { getMapInstance, addLayer, removeLayer } = await import('./mapManager.js');
    const map = getMapInstance();
    if (!map || !formEl) return;

    // 🧼 Remove any previously drawn red editable route
    if (drawnSegmentPolylines.has(segId)) {
        removeLayer(drawnSegmentPolylines.get(segId));
        drawnSegmentPolylines.delete(segId);
    }

    // 🧼 Also remove default blue route if it exists
    if (segmentPolylines.has(segId)) {
        removeLayer(segmentPolylines.get(segId));
        segmentPolylines.delete(segId);
    }

    document.getElementById('segment-route-toolbar')?.classList.remove('d-none');

    const jsonInput = formEl.querySelector('input[name="RouteJson"]');
    let coords = [];
    const rawJson = jsonInput?.value?.trim();

    if (rawJson && rawJson !== '[]') {
        try {
            const parsed = JSON.parse(rawJson);
            if (Array.isArray(parsed) && parsed.length >= 2) {
                coords = parsed;
            }
        } catch {
            coords = [];
        }
    }

    // Fallback: use From/To coords
    if (!coords.length) {
        // ONLY fallback if we're editing and RouteJson is empty
        const fromOpt = formEl.querySelector('select[name="FromPlaceId"] option:checked');
        const toOpt = formEl.querySelector('select[name="ToPlaceId"] option:checked');
        if (fromOpt && toOpt) {
            const fromLat = parseFloat(fromOpt.dataset.lat);
            const fromLon = parseFloat(fromOpt.dataset.lon);
            const toLat = parseFloat(toOpt.dataset.lat);
            const toLon = parseFloat(toOpt.dataset.lon);
            if ([fromLat, fromLon, toLat, toLon].every(c => !isNaN(c))) {
                coords = [[fromLat, fromLon], [toLat, toLon]];
            }
        }
    }

    if (coords.length < 2) return;

    const poly = L.polyline(coords, {
        color: 'red', weight: 4, dashArray: '6,3'
    }).addTo(map);

    poly.enableEdit();
    drawnSegmentPolylines.set(segId, poly);

    // Watch for edits to update hidden input + distance
    poly.on('editable:dragend editable:vertex:dragend editable:vertex:deleted', () => {
        const latlngs = poly.getLatLngs().map(p => [p.lat, p.lng]);
        if (jsonInput) jsonInput.value = JSON.stringify(latlngs);
        updateDistanceAndDuration(formEl, poly);
    });

    // Immediately sync inputs
    const latlngs = poly.getLatLngs().map(p => [p.lat, p.lng]);
    if (jsonInput) jsonInput.value = JSON.stringify(latlngs);
    updateDistanceAndDuration(formEl, poly);

    map.fitBounds(poly.getBounds(), { padding: [50, 50] });
};

/**
 * Saves the Segment
 * @param segId Segment ID
 * @returns {Promise<void>}
 */
const saveSegment = async (segId) => {
    const formEl = document.getElementById(`segment-form-${segId}`);
    if (!formEl) return;

    const fd = new FormData(formEl);
    const token = fd.get('__RequestVerificationToken');

    const resp = await fetch('/User/Segments/CreateOrUpdate', {
        method: 'POST',
        body: fd,
        headers: token ? { RequestVerificationToken: token } : {}
    });

    const html = await resp.text();
    const tempDiv = document.createElement('div');
    tempDiv.innerHTML = html.trim();
    const newItem = tempDiv.firstElementChild;
    if (!newItem) return;

    // 🛑 EARLY CHECK: If form has validation errors, stop here
    if (newItem.querySelector('.segment-form-errors')) {
        const wrapper = document.querySelector(`#segment-form-${segId}`)?.closest('.accordion-item');
        if (wrapper) wrapper.replaceWith(newItem);
        attachSegmentFormHandlers();
        console.warn('⚠️ Validation errors shown for segment:', segId);
        return;
    }

    const list = document.getElementById('segments-inner-list');
    if (!list) return;

    const { removeLayer } = await import('./mapManager.js');

    // 🧹 Remove red editable line
    const redLine = drawnSegmentPolylines.get(segId);
    if (redLine) {
        removeLayer(redLine);
        drawnSegmentPolylines.delete(segId);
    }

    // 🧹 Remove blue default polyline
    const blueLine = segmentPolylines.get(segId);
    if (blueLine) {
        removeLayer(blueLine);
        segmentPolylines.delete(segId);
    }

    // 🛑 Hide the editing toolbar
    document.getElementById('segment-route-toolbar')?.classList.add('d-none');

    // 🧽 Remove old DOM items only now — we know it's a real success
    document.getElementById(`segment-item-${segId}`)?.remove();
    document.querySelector(`.segment-list-item[data-segment-id="${segId}"]`)?.remove();

    list.appendChild(newItem);

    await renderStaticSegmentRoute(segId, newItem);
    bindSegmentActions();
    attachSegmentFormHandlers();
    await callInitOrdering();
    store.dispatch('clear-context');
};

// lazy load exported method
const loadInitOrdering = async () => {
    if (!cachedInitOrdering) {
        const mod = await import('./regionsOrder.js');
        cachedInitOrdering = mod.initOrdering;
    }
    return cachedInitOrdering;
};
const callInitOrdering = async () => {
    const fn = await loadInitOrdering();
    return fn();
};
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
    walk: 5, bicycle: 15, bike: 40, car: 60, bus: 35, train: 100, ferry: 30, boat: 25, flight: 800, helicopter: 200
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
const updateSegmentPolyline = async (segId, fromCoords, toCoords, fitMap = true, segment = null) => {
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
        const latlngs = [fromCoords, toCoords];
        const polyline = L.polyline(latlngs, {
            color: 'blue',
            weight: 3,
            dashArray: '4',
            className: 'segment-polyline'
        });

        // Tooltip text
        const mode = segment?.mode || 'unknown';
        const from = segment?.fromPlace?.name || 'Start';
        const to = segment?.toPlace?.name || 'End';
        const dist = segment?.estimatedDistanceKm?.toFixed(1) || '?';
        const mins = segment?.estimatedDuration
            ? Math.round(segment.estimatedDuration.totalMinutes ?? segment.estimatedDuration / 60)
            : null;
        const time = mins ? `in ~${mins} min` : '';

        const capitalizedName = mode.charAt(0).toUpperCase() + mode.slice(1);
        const tooltipText = `${capitalizedName} <br>From: ${from} <br>To: ${to}<br>Distance: ${dist} km ${time}`;

        polyline.bindTooltip(tooltipText, {
            sticky: true,
            direction: 'top',
            className: 'segment-tooltip'
        });
        polyline.on('click', async () => {
            const name = `${segment?.mode || 'Segment'}: ${segment?.fromPlace?.name || 'Start'} → ${segment?.toPlace?.name || 'End'}`;

            store.dispatch('set-context', {
                type: 'segment',
                id: segId,
                action: 'edit',
                meta: { name }
            });

            const el = document.getElementById(`segment-item-${segId}`);
            scrollToSegment(segId);

            if (el) {
                el.classList.add('segment-highlight');
                setTimeout(() => el.classList.remove('segment-highlight'), 1000);
            }

            // ✨ Flash polyline
            const originalColor = polyline.options.color;
            polyline.setStyle({ color: 'yellow', weight: 5 });
            setTimeout(() => polyline.setStyle({ color: originalColor, weight: 3 }), 500);

            const { getMapInstance } = await import('./mapManager.js');
            getMapInstance()?.fitBounds(polyline.getBounds(), { padding: [50, 50] });
        });

        addLayer(polyline);
        if (fitMap) {
            if (MAP_ZOOM.segment === 'fit') {
                fitBounds(polyline.getBounds(), { padding: [50, 50] });
            } else {
                const center = polyline.getBounds().getCenter();
                const map = getMapInstance();
                focusMapView('segment', [lat, lon], map);
            }
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

export const renderAllSegmentsOnMap = async (segments) => {
    for (const segment of segments) {
        const segId = segment.id;
        const routeJson = segment.routeJson;

        // If custom route exists, use it
        if (routeJson?.trim() && routeJson !== '[]') {
            try {
                const coords = JSON.parse(routeJson);
                if (Array.isArray(coords) && coords.length >= 2) {
                    const polyline = L.polyline(coords, {
                        color: 'blue',
                        weight: 3,
                        className: 'segment-polyline'
                    });

                    // Tooltip for RouteJson
                    const mode = segment?.mode || 'unknown';
                    const from = segment?.fromPlace?.name || 'Start';
                    const to = segment?.toPlace?.name || 'End';
                    const dist = segment?.estimatedDistanceKm?.toFixed(1) || '?';
                    let mins = null;
                    if (typeof segment?.estimatedDuration === 'string') {
                        const [hh, mm, ss] = segment.estimatedDuration.split(':').map(Number);
                        mins = Math.round(hh * 60 + mm + ss / 60);
                    } else if (typeof segment?.estimatedDuration === 'number') {
                        mins = Math.round(segment.estimatedDuration / 60);
                    }
                    const time = mins ? `in ~${mins} min` : '';

                    const capitalizedName = mode.charAt(0).toUpperCase() + mode.slice(1);
                    const tooltipText = `${capitalizedName} <br>From: ${from} <br>To: ${to}<br>Distance: ${dist} km ${time}`;
                    polyline.bindTooltip(tooltipText, {
                        sticky: true,
                        direction: 'top',
                        className: 'segment-tooltip'
                    });

                    polyline.on('click', async () => {
                        const name = `${segment?.mode || 'Segment'}: ${segment?.fromPlace?.name || 'Start'} → ${segment?.toPlace?.name || 'End'}`;

                        store.dispatch('set-context', {
                            type: 'segment',
                            id: segId,
                            action: 'edit',
                            meta: { name }
                        });

                        const el = document.getElementById(`segment-item-${segId}`);
                        scrollToSegment(segId);

                        if (el) {
                            el.classList.add('segment-highlight');
                            setTimeout(() => el.classList.remove('segment-highlight'), 1000);
                        }

                        const originalColor = polyline.options.color;
                        polyline.setStyle({ color: 'yellow', weight: 5 });
                        setTimeout(() => polyline.setStyle({ color: originalColor, weight: 3 }), 500);

                        const { getMapInstance } = await import('./mapManager.js');
                        getMapInstance()?.fitBounds(polyline.getBounds(), { padding: [50, 50] });
                    });

                    const { addLayer } = await import('./mapManager.js');
                    addLayer(polyline);
                    segmentPolylines.set(segId, polyline);
                    continue;
                }
            } catch {
                console.warn(`Invalid RouteJson for segment ${segId}`);
            }
        }

        // Fallback to From→To line
        const fromLoc = segment.fromPlace?.location;
        const toLoc = segment.toPlace?.location;

        if (!fromLoc || !toLoc || fromLoc.latitude === undefined || fromLoc.longitude === undefined || toLoc.latitude === undefined || toLoc.longitude === undefined) {
            console.warn(`Skipping segment ${segId} due to missing coordinates`);
            continue;
        }

        const fromLatLon = [fromLoc.latitude, fromLoc.longitude];
        const toLatLon = [toLoc.latitude, toLoc.longitude];
        const polyline = await updateSegmentPolyline(segId, fromLatLon, toLatLon, false, segment);
        if (polyline) {
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

    focusMapView('place', [lat, lon], map);

    const placeId = selectEl.value;
    const marker = getPlaceMarkerById(placeId);
    if (marker) {
        clearSelectedMarker();
        selectMarker(marker);
    }

    const currentCtx = store.getState().context;
    if (currentCtx?.type !== 'segment') {
        store.dispatch('set-context', {
            type: 'place', id: placeId, action: 'edit', meta: { name: selectedOption.text }
        });
    }
};

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

    if (!document.getElementById('segment-route-toolbar')) {
        const toolbar = document.createElement('div');
        toolbar.id = 'segment-route-toolbar';
        toolbar.className = 'leaflet-bar route-toolbar d-none';
        toolbar.innerHTML = `
      <button class="btn-reset-route" title="Reset to straight line">🔄</button>
      <button class="btn-clear-route" title="Clear route">🗑️</button>
      <button class="btn-done-route" title="Done editing">✅</button>
    `;
        document.getElementById('mapContainer')?.appendChild(toolbar);
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
            type: 'segment', id: segId, action: 'edit', meta: { name: mode }
        });
    }

    bindSegmentActions();
    attachSegmentFormHandlers();
};

/**
 * Binds event handlers for editing, saving, deleting, and selecting segments.
 */
const bindSegmentActions = () => {

    // Toggle all segments visibility
    const masterToggle = document.getElementById('toggle-all-segments');
    masterToggle.addEventListener('click', e => {
        e.stopPropagation(); // prevent focus
    });
    masterToggle.addEventListener('change', () => {
        const show = masterToggle.checked;
        document.querySelectorAll('.btn-segment-toggle-visibility').forEach(checkbox => {
            if (checkbox.checked !== show) checkbox.click(); // triggers existing logic
        });
    });
    /* Toggle single segment visibility */
    document.querySelectorAll('.btn-segment-toggle-visibility').forEach(checkbox => {
        const segId = checkbox.dataset.segmentId;
        if (!segId) return;

        // Initialize checkbox checked state from store or default true
        const visibility = store.getState().segmentVisibility[segId];
        checkbox.checked = visibility === undefined ? true : visibility;

        checkbox.onchange = () => {
            const newVisibility = checkbox.checked;
            store.dispatch('set-segment-visibility', {
                segmentId: segId, visible: newVisibility
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

            const existing = document.getElementById(`segment-item-${segId}`);
            if (existing) {
                existing.outerHTML = html;
            } else {
                const list = document.getElementById('segments-list');
                if (list) list.insertAdjacentHTML('beforeend', html);
            }

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

            const form = document.getElementById(`segment-form-${segId}`);
            const mode = form?.querySelector('select[name="Mode"]')?.value || 'Segment';
            const from = form?.querySelector('select[name="FromPlaceId"] option:checked')?.textContent?.trim() || '...';
            const to = form?.querySelector('select[name="ToPlaceId"] option:checked')?.textContent?.trim() || '...';

            const name = `${mode}: ${from} → ${to}`;

            store.dispatch('set-context', {
                type: 'segment', id: segId, action: 'edit', meta: { name }
            });

            bindSegmentActions();
            attachSegmentFormHandlers();
        };
    });

    /* ---------- SAVE ---------- */
    document.querySelectorAll('.btn-segment-save').forEach(btn => {
        btn.onclick = () => saveSegment(btn.dataset.segmentId);
    });

    /* ---------- DELETE ---------- */
    document.querySelectorAll('.btn-delete-segment').forEach(btn => {
        btn.onclick = () => {
            const segId = btn.dataset.segmentId;
            if (!segId) return;

            wayfarer.showConfirmationModal({
                title: 'Delete segment?',
                message: 'This action cannot be undone.',
                confirmText: 'Delete',
                onConfirm: async () => {
                    const fd = new FormData();
                    fd.set('__RequestVerificationToken', document.querySelector('input[name="__RequestVerificationToken"]').value);

                    const resp = await fetch(`/User/Segments/Delete/${segId}`, {
                        method: 'POST',
                        body: fd,
                        headers: { RequestVerificationToken: fd.get('__RequestVerificationToken') }
                    });

                    if (resp.ok) {
                        document.getElementById(`segment-item-${segId}`)?.remove();

                        // ✅ REMOVE POLYLINE FROM MAP
                        const polyline = segmentPolylines.get(segId);
                        if (polyline) {
                            const { removeLayer } = await import('./mapManager.js');
                            removeLayer(polyline);
                            segmentPolylines.delete(segId);
                        }
                        await callInitOrdering();
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
        item.onclick = async (e) => {
            // 🔒 Ignore clicks on interactive controls
            if (e.target.closest('button') || e.target.closest('a') || e.target.closest('input') || e.target.classList.contains('btn') || e.target.classList.contains('form-check-input')) {
                return;
            }

            const segId = item.dataset.segmentId;
            const name = item.dataset.segmentName || 'Unnamed segment';
            if (!segId) return;

            document.querySelectorAll('.segment-list-item')
                .forEach(i => i.classList.add('dimmed'));
            item.classList.remove('dimmed');

            store.dispatch('set-context', {
                type: 'segment', id: segId, action: 'edit', meta: { name }
            });

            // Focus map on polyline bounds
            const polyline = segmentPolylines.get(segId);
            if (polyline) {
                const { fitBounds } = await import('./mapManager.js');
                fitBounds(polyline.getBounds(), { padding: [50, 50] });
            }
        };
    });
};

/**
 * Attaches handlers for segment form cancel and realtime updates for distance/time inputs.
 */
const attachSegmentFormHandlers = () => {

    /* ---------- DRAW ROUTE ---------- */
    document.querySelector('.btn-reset-route')?.addEventListener('click', async () => {
        const segId = store.getState().context?.id;
        const formEl = document.getElementById(`segment-form-${segId}`);
        if (!segId || !formEl) return;

        const fromOpt = formEl.querySelector('select[name="FromPlaceId"] option:checked');
        const toOpt = formEl.querySelector('select[name="ToPlaceId"] option:checked');
        if (!fromOpt || !toOpt) return;

        const fromLat = parseFloat(fromOpt.dataset.lat);
        const fromLon = parseFloat(fromOpt.dataset.lon);
        const toLat = parseFloat(toOpt.dataset.lat);
        const toLon = parseFloat(toOpt.dataset.lon);
        if ([fromLat, fromLon, toLat, toLon].some(isNaN)) return;

        const { getMapInstance, removeLayer } = await import('./mapManager.js');
        const map = getMapInstance();

        if (drawnSegmentPolylines.has(segId)) {
            removeLayer(drawnSegmentPolylines.get(segId));
            drawnSegmentPolylines.delete(segId);
        }

        const poly = L.polyline([[fromLat, fromLon], [toLat, toLon]], {
            color: 'red', weight: 4, dashArray: '6,3'
        }).addTo(map);
        poly.enableEdit();
        drawnSegmentPolylines.set(segId, poly);

        const jsonInput = formEl.querySelector('input[name="RouteJson"]');
        if (jsonInput) jsonInput.value = JSON.stringify([[fromLat, fromLon], [toLat, toLon]]);
        updateDistanceAndDuration(formEl, poly);
        map.fitBounds(poly.getBounds(), { padding: [50, 50] });
    });

    // draw toolbar buttons listeners
    document.querySelector('.btn-done-route')?.addEventListener('click', async () => {
        const segId = store.getState().context?.id;
        if (!segId) return;

        await saveSegment(segId);
        document.getElementById('segment-route-toolbar')?.classList.add('d-none');
    });

    document.querySelector('.btn-clear-route')?.addEventListener('click', async () => {
        const segId = store.getState().context?.id;
        const formEl = document.getElementById(`segment-form-${segId}`);
        if (!segId || !formEl) return;

        const { removeLayer } = await import('./mapManager.js');
        if (drawnSegmentPolylines.has(segId)) {
            removeLayer(drawnSegmentPolylines.get(segId));
            drawnSegmentPolylines.delete(segId);
        }

        const jsonInput = formEl.querySelector('input[name="RouteJson"]');
        if (jsonInput) jsonInput.value = '';
    });

    /* ---------- CANCEL ---------- */
    document.querySelectorAll('.btn-segment-cancel').forEach(btn => {
        btn.onclick = async () => {
            document.getElementById('segment-route-toolbar')?.classList.add('d-none');

            const segId = btn.dataset.segmentId;
            if (!segId) return;

            const { removeLayer } = await import('./mapManager.js');
            const redLine = drawnSegmentPolylines.get(segId);
            if (redLine) {
                removeLayer(redLine);
                drawnSegmentPolylines.delete(segId);
            }

            store.dispatch('clear-context'); // 🧠 Let store handle full restoration
        };
    });

    // Add realtime duration calculation and place select change handling on all open segment forms
    document.querySelectorAll('form[id^="segment-form-"]').forEach(async (form) => {
        const segId = form.querySelector('input[name="Id"]')?.value;
        if (!segId) return;

        // ✅ Make the route editable (or fall back to straight line)
        await renderEditableSegmentRoute(segId, form);

        // Existing bindings
        const distInput = form.querySelector('input[name="EstimatedDistanceKm"]');
        const modeSelect = form.querySelector('select[name="Mode"]');
        const durationInput = form.querySelector('input[name="EstimatedDurationMinutes"]');

        if (distInput && modeSelect && durationInput) {
            const updateDuration = () => {
                const dist = parseFloat(distInput.value);
                const mode = modeSelect.value;
                if (isNaN(dist) || !mode) return;

                if (!durationInput.value || durationInput.value === '0') {
                    const mins = calculateDurationMinutes(dist, mode);
                    if (mins !== null) {
                        durationInput.value = mins;
                    }
                }
            };
            distInput.addEventListener('input', updateDuration);
            modeSelect.addEventListener('change', updateDuration);
        }

        bindPlaceSelectChange(form);
    });

};

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
        const openForm = document.querySelector('form[id^="segment-form-"]');
        const segId = openForm?.querySelector('[name="Id"]')?.value;
        if (!segId) return;

        try {
            // ✅ Remove both form and any leftover display item
            const formItem = openForm.closest('.accordion-item');
            const displayItem = document.querySelector(`.segment-list-item[data-segment-id="${segId}"]`);

            if (formItem) formItem.remove();
            if (displayItem) displayItem.remove();

            // ✅ Load fresh partial
            const resp = await fetch(`/User/Segments/GetItemPartial?segmentId=${segId}`);
            const html = await resp.text();
            const temp = document.createElement('div');
            temp.innerHTML = html.trim();
            const newItem = temp.firstElementChild;
            if (!newItem) return;

            // ✅ Append to the correct parent
            const list = document.getElementById('segments-inner-list');
            list.appendChild(newItem);

            // ✅ Wait for element to be laid out (not display: none / detached)
            await new Promise(resolve => {
                const check = () => {
                    if (newItem.offsetParent !== null) resolve();
                    else requestAnimationFrame(check);
                };
                check();
            });

            // ✅ Remove old line if any
            if (segmentPolylines.has(segId)) {
                const old = segmentPolylines.get(segId);
                const { removeLayer } = await import('./mapManager.js');
                removeLayer(old);
                segmentPolylines.delete(segId);
            }

            // ✅ Always draw the proper route
            await renderStaticSegmentRoute(segId, newItem);

            bindSegmentActions();
            attachSegmentFormHandlers();
        } catch (err) {
            console.error('Failed to restore segment after cancel:', err);
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
/**
 * Extend a route
 * @param placeId
 * @param oldLat
 * @param oldLon
 * @param newLat
 * @param newLon
 * @returns {Promise<void>}
 */
export const extendSegmentRouteForMovedPlace = async (placeId, oldLat, oldLon, newLat, newLon) => {
    if (!placeId || isNaN(oldLat) || isNaN(oldLon) || isNaN(newLat) || isNaN(newLon)) return;

    const tripId = store.getState().tripId;
    if (!tripId) return;

    const resp = await fetch(`/User/Segments/GetSegments?tripId=${tripId}`);
    if (!resp.ok) return;

    const segments = await resp.json();
    for (const segment of segments) {
        const segId = segment.id;
        const isFrom = segment.fromPlace?.id === placeId;
        const isTo = segment.toPlace?.id === placeId;
        if (!isFrom && !isTo) continue;

        let coords = [];
        if (segment.routeJson?.trim()) {
            try {
                coords = JSON.parse(segment.routeJson);
            } catch (err) {
                console.warn('⚠️ Failed to parse routeJson:', segment.routeJson, err);
            }
        }

        const extension = [newLat, newLon];

        // 🧠 Improved check to avoid duplicate extensions
        const alreadyExtended =
            (isTo && JSON.stringify(coords.at(-1)) === JSON.stringify(extension)) ||
            (isFrom && JSON.stringify(coords.at(0)) === JSON.stringify(extension));

        if (alreadyExtended) {
            console.log('⏭️ Already extended to new location — skipping', segId);
            continue;
        }

        console.debug('📈 Original coords:', coords);

        if (coords.length >= 2) {
            if (isTo) coords.push(extension);
            if (isFrom) coords.unshift(extension);
        } else {
            // Fallback for initial straight line
            coords = isTo
                ? [[segment.fromPlace.location.latitude, segment.fromPlace.location.longitude], extension]
                : [extension, [segment.toPlace.location.latitude, segment.toPlace.location.longitude]];
        }

        console.debug('📌 Extended coords:', coords);

        const fd = new FormData();
        fd.set('Id', segId);
        fd.set('TripId', tripId); // from store.getState().tripId
        fd.set('FromPlaceId', segment.fromPlace.id);
        fd.set('ToPlaceId', segment.toPlace.id);
        fd.set('Mode', segment.mode);
        fd.set('EstimatedDistanceKm', segment.estimatedDistanceKm || '');
        fd.set('EstimatedDurationMinutes', segment.estimatedDuration || '');
        fd.set('Notes', segment.notes || '');
        fd.set('RouteJson', JSON.stringify(coords));
        fd.set('__RequestVerificationToken', document.querySelector('input[name="__RequestVerificationToken"]').value);

        await fetch('/User/Segments/CreateOrUpdate', {
            method: 'POST',
            body: fd,
            headers: { 'RequestVerificationToken': fd.get('__RequestVerificationToken') }
        });

        console.log(`✅ Segment ${segId} route updated.`);
    }
};
