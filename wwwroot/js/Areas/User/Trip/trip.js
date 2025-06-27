// trip.js – modular entry point for trip editing (pure-store, banner + new-region fix)

import {
    clearDim,
    dimAll,
    hideAllIndicators,
    rebindMainButtons,
    saveTrip
} from './uiCore.js';

import {
    applyCoordinates, clearPreviewMarker,
    disableDrawingTools,
    getMapInstance,
    initializeMap,
    renderPlaceMarker,
    renderRegionMarker
} from './mapManager.js';

import { initRegionHandlers }   from './regionHandlers.js';
import { initPlaceHandlers }    from './placeHandlers.js';
import { initSegmentHandlers,
    loadSegmentCreateForm } from './segmentHandlers.js';
import { setupQuill }           from './quillNotes.js';
import { store }                from './storeInstance.js';
import { initOrdering }         from './regionsOrder.js';

let currentTripId        = null;
let activeDrawingRegionId = null;

/* ------------------------------------------------------------------ *
 *  Helpers – banner
 * ------------------------------------------------------------------ */
const bannerEl  = () => document.getElementById('mapping-context-banner');
const bannerLbl = () => document.getElementById('mapping-context-text');

const replaceOuterHtmlAndWait = async (el, html) => {
    return new Promise(resolve => {
        const parent = el.parentNode;
        const placeholder = document.createElement('div');
        placeholder.innerHTML = html;
        const newEl = placeholder.firstElementChild;
        parent.replaceChild(newEl, el);
        requestAnimationFrame(() => resolve(newEl)); // safe one-frame defer
    });
};

const setBanner = (action, name) => {
    const banner = bannerEl();
    const label  = bannerLbl();
    if (!banner || !label) return;

    const clean   = name?.replace(/^[^\w]+/, '').trim() || 'Unnamed';
    const prefix  = action === 'edit' ? 'Editing' : 'Selected';
    label.innerHTML = `<span class="me-1"></span>${prefix}: ${clean}`;
    banner.classList.add('active');
};

const hideBanner = () => bannerEl()?.classList.remove('active');

/* ------------------------------------------------------------------ *
 *  Bind UI buttons & store events
 * ------------------------------------------------------------------ */
const attachListeners = () => {
    const form = document.getElementById('trip-form');
    if (!form) return;

    /* ----- main action buttons ----- */
    document.getElementById('btn-save-trip')
        ?.addEventListener('click', () => saveTrip('save'));
    document.getElementById('btn-save-edit-trip')
        ?.addEventListener('click', () => saveTrip('save-edit'));

    /* ----- recenter map ----- */
    document.getElementById('btn-trip-recenter')
        ?.addEventListener('click', () => {
            const lat  = parseFloat(form.querySelector('[name="CenterLat"]')?.dataset.default ?? '');
            const lon  = parseFloat(form.querySelector('[name="CenterLon"]')?.dataset.default ?? '');
            const zoom = parseInt( form.querySelector('[name="Zoom"]')?.dataset.default ?? '3', 10 );

            if (!Number.isNaN(lat) && !Number.isNaN(lon))
                getMapInstance()?.setView([lat, lon], zoom || 3);

            form.scrollIntoView({ behavior: 'smooth', block: 'start' });
            store.dispatch('clear-context');
        });

    /* ----- add segment shortcut ----- */
    document.getElementById('btn-add-segment')
        ?.addEventListener('click', () => loadSegmentCreateForm(currentTripId));

    /* ----- clear context banner (❌) ----- */
    document.getElementById('btn-clear-context')
        ?.addEventListener('click', () => store.dispatch('clear-context'));

    /* ------------------------------------------------------------------ *
     *  Store listener – react to context changes
     * ------------------------------------------------------------------ */
    store.subscribe(({ type, payload }) => {

        /* ========== CONTEXT SET ========== */
        if (type === 'set-context') {
            const { id, type: ctxType, meta, action } = payload;
            hideAllIndicators();

            if (ctxType === 'place') {
                const placeItem = document.querySelector(`.place-list-item[data-place-id="${id}"]`);

                if (meta?.regionId) {
                    const regionItem = document.getElementById(`region-item-${meta.regionId}`);
                    if (regionItem) {
                        dimAll();
                        regionItem.querySelectorAll('.place-list-item')
                            .forEach(el => { if (el !== placeItem) el.classList.add('dimmed'); });
                        regionItem.classList.remove('dimmed');
                        regionItem.querySelector('.accordion-button')?.classList.remove('dimmed');
                    }
                }
                placeItem?.classList.remove('dimmed');
            }

            if (ctxType === 'region') {
                /* Wrapper may be either the normal list item OR an open form */
                const regionItem  = document.getElementById(`region-item-${id}`);
                const regionForm  = document.getElementById(`region-form-${id}`);
                const wrapper     = regionItem || regionForm;

                wrapper?.classList.remove('dimmed');
                wrapper?.querySelector('.accordion-button')?.classList?.remove('dimmed');

                document.querySelectorAll('.accordion-item')
                    .forEach(el => (el.id === `region-item-${id}` || el.id === `region-form-${id}`)
                        ? el.classList.remove('dimmed')
                        : el.classList.add('dimmed'));
            }

            if (ctxType === 'segment') {
                document.getElementById(`segment-item-${id}`)
                    ?.scrollIntoView({ behavior: 'smooth', block: 'center' });
            }

            /* ----- banner ----- */
            (meta?.name && ctxType) ? setBanner(action, meta.name) : hideBanner();

            // 🧭 Sync lat/lon to inputs + form when context is set
            const formSelector = ctxType === 'place'
                ? `#place-form-${id}`
                : ctxType === 'region'
                    ? `#region-form-${id}`
                    : null;

            const latField = ctxType === 'place' ? 'Latitude' : 'CenterLat';
            const lonField = ctxType === 'place' ? 'Longitude' : 'CenterLon';

            const form = document.querySelector(formSelector);
            if (form) {
                const latRaw = form.querySelector(`input[name="${latField}"]`)?.value;
                const lonRaw = form.querySelector(`input[name="${lonField}"]`)?.value;

                const lat = parseFloat(latRaw);
                const lon = parseFloat(lonRaw);

                if (!isNaN(lat) && !isNaN(lon)) {
                    applyCoordinates({ lat, lon });
                }
            }
        }

        /* ========== CONTEXT CLEARED ========== */
        if (type === 'clear-context') {
            const latInput = document.getElementById('contextLat');
            const lonInput = document.getElementById('contextLon');
            const coordsEl = document.getElementById('context-coords');

            if (latInput) latInput.value = '';
            if (lonInput) lonInput.value = '';
            if (coordsEl) {
                coordsEl.classList.add('d-none');
                coordsEl.querySelector('code')?.replaceChildren();
            }
            clearDim();
            hideBanner();
            hideAllIndicators();
        }

        /**
         * re attach handlers after reload
         */
        store.subscribe(({ type, payload }) => {
            if (type === 'region-dom-reloaded') {
                initRegionHandlers(currentTripId);
                initPlaceHandlers();
                initSegmentHandlers(currentTripId);
                initOrdering();
            }
        });

        /**
         * ensure only one active form at all times
         */
        if (type === 'trip-cleanup-open-forms') {
            // Regions: remove open form and reload from server if needed
            document.querySelectorAll('form[id^="region-form-"]').forEach(async (form) => {
                const regionId = form.querySelector('[name="Id"]')?.value;
                if (!regionId || regionId.length !== 36) {
                    form.closest('.accordion-item')?.remove(); // new form, remove entirely
                } else {
                    const resp = await fetch(`/User/Regions/GetItemPartial?regionId=${regionId}`);
                    form.outerHTML = await resp.text();
                    store.dispatch('region-dom-reloaded', { regionId });
                }
            });

            // Places: remove open form and reload from server if needed
            document.querySelectorAll('form[id^="place-form-"]').forEach(async (form) => {
                const placeId  = form.querySelector('[name="Id"]')?.value;
                const regionId = form.querySelector('[name="RegionId"]')?.value;

                if (!placeId || placeId.length !== 36) {
                    form.closest('.place-list-item')?.remove(); // new form, remove entirely
                    clearPreviewMarker(); // 🧹 cleanup unsaved preview marker
                } else {
                    const regionEl = document.getElementById(`region-item-${regionId}`);
                    if (regionEl) {
                        const resp = await fetch(`/User/Regions/GetItemPartial?regionId=${regionId}`);
                        const html = await resp.text();

                        const newRegionEl = await replaceOuterHtmlAndWait(regionEl, html);
                        store.dispatch('region-dom-reloaded', { regionId });

                        // 🔁 Restore marker for the saved place
                        const placeItem = newRegionEl.querySelector(`.place-list-item[data-place-id="${placeId}"]`);
                        if (placeItem) {
                            const d = placeItem.dataset;
                            if (d.placeLat && d.placeLon) {
                                renderPlaceMarker({
                                    Id          : d.placeId,
                                    Name        : d.placeName || '',
                                    Latitude    : d.placeLat,
                                    Longitude   : d.placeLon,
                                    IconName    : d.placeIcon,
                                    MarkerColor : d.placeColor,
                                    RegionId    : d.regionId
                                });
                            }
                        }

                        clearPreviewMarker(); // ✅ Clear AFTER redrawing the saved marker
                    }
                }

            });

            // Segments: remove open form and reload from server if needed
            document.querySelectorAll('form[id^="segment-form-"]').forEach(async (form) => {
                const segmentId = form.querySelector('[name="Id"]')?.value;
                if (!segmentId || segmentId.length !== 36) {
                    form.closest('.accordion-item')?.remove(); // new form, remove entirely
                } else {
                    const resp = await fetch(`/User/Segments/GetItemPartial?segmentId=${segmentId}`);
                    form.outerHTML = await resp.text();
                    store.dispatch('segment-dom-reloaded', { segmentId });
                }
            });

            store.dispatch('clear-context');
        }
                
    });
};

/* ------------------------------------------------------------------ *
 *  Render markers present in HTML on load
 * ------------------------------------------------------------------ */
const loadPersistedMarkers = () => {
    /* places */
    document.querySelectorAll('.place-list-item').forEach(el => {
        const d = el.dataset;
        if (d.placeLat && d.placeLon) {
            renderPlaceMarker({
                Id       : d.placeId,
                Name     : (d.placeName ||
                    el.querySelector('.place-name')?.textContent ||
                    '').trim(),
                Latitude    : d.placeLat,
                Longitude   : d.placeLon,
                IconName    : d.placeIcon,
                MarkerColor : d.placeColor,
                RegionId    : d.regionId
            });
        }
    });

    /* regions */
    document.querySelectorAll('#regions-accordion .accordion-item').forEach(item => {
        const lat  = item.dataset.centerLat;
        const lon  = item.dataset.centerLon;
        const id   = item.id?.replace('region-item-', '');
        const name = item.dataset.regionName || 'Unnamed Region';
        if (id && lat && lon)
            renderRegionMarker({ Id: id, CenterLat: lat, CenterLon: lon, Name: name });
    });
};

/* ------------------------------------------------------------------ *
 *  Bootstrap
 * ------------------------------------------------------------------ */
document.addEventListener('DOMContentLoaded', async () => {

    /* ----- map initial centre (URL has priority) ----- */
    const urlParams  = new URLSearchParams(window.location.search);
    const centerLat  = parseFloat(urlParams.get('lat'));
    const centerLon  = parseFloat(urlParams.get('lng'));
    const zoomParam  = parseInt(urlParams.get('zoom'), 10);

    const form       = document.getElementById('trip-form');
    const modelLat   = parseFloat(form?.querySelector('[name="CenterLat"]')?.dataset.default ?? '0');
    const modelLon   = parseFloat(form?.querySelector('[name="CenterLon"]')?.dataset.default ?? '0');
    const modelZoom  = parseInt(form?.querySelector('[name="Zoom"]')?.dataset.default ?? '3', 10);

    const lat  = !Number.isNaN(centerLat) ? centerLat : modelLat;
    const lon  = !Number.isNaN(centerLon) ? centerLon : modelLon;
    const zoom = !Number.isNaN(zoomParam) && zoomParam >= 0 ? zoomParam : modelZoom;

    /* ----- trip id ----- */
    currentTripId = form.querySelector('[name="Id"]')?.value;
    store.dispatch('set-trip-id', currentTripId);

    /* ----- map ----- */
    activeDrawingRegionId = null;
    disableDrawingTools();
    const map = initializeMap([lat, lon], zoom);

    /* keep hidden inputs + URL in sync */
    if (form) {
        form.querySelector('[name="CenterLat"]').value = lat.toFixed(6);
        form.querySelector('[name="CenterLon"]').value = lon.toFixed(6);
        form.querySelector('[name="Zoom"]').value      = zoom;
    }

    map.on('moveend zoomend', () => {
        const c = map.getCenter();
        const z = map.getZoom();

        const params = new URLSearchParams(window.location.search);
        params.set('lat',  c.lat.toFixed(6));
        params.set('lng',  c.lng.toFixed(6));
        params.set('zoom', z);
        history.replaceState(null, '', `${window.location.pathname}?${params.toString()}`);

        form.querySelector('[name="CenterLat"]').value = c.lat.toFixed(6);
        form.querySelector('[name="CenterLon"]').value = c.lng.toFixed(6);
        form.querySelector('[name="Zoom"]').value      = z;
    });

    map.on('click', ({ latlng }) =>
        applyCoordinates({ lat: latlng.lat.toFixed(6), lon: latlng.lng.toFixed(6) })
    );
    const syncToContext = () => {
        const latInput = document.getElementById('contextLat');
        const lonInput = document.getElementById('contextLon');
        const lat = parseFloat(latInput?.value);
        const lon = parseFloat(lonInput?.value);
        if (!isNaN(lat) && !isNaN(lon)) {
            applyCoordinates({ lat, lon });
        }
    };

    document.getElementById('contextLat')?.addEventListener('change', syncToContext);
    document.getElementById('contextLon')?.addEventListener('change', syncToContext);

    /* ----- sub-modules ----- */
    initOrdering();
    rebindMainButtons();
    initRegionHandlers(currentTripId);
    initPlaceHandlers();
    initSegmentHandlers(currentTripId);

    await setupQuill();
    attachListeners();
    loadPersistedMarkers();
});
