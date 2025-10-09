// trip.js – modular entry point for trip editing (pure-store, banner + new-region fix)

import {
    clearDim, dimAll, hideAllIndicators, rebindMainButtons, saveTrip
} from './uiCore.js';

import {
    applyCoordinates,
    clearPreviewMarker,
    disableDrawingTools,
    getMapInstance,
    initializeMap,
    renderAreaPolygon,
    renderPlaceMarker,
    renderRegionMarker
} from './mapManager.js';

import {initRegionHandlers} from './regionHandlers.js';
import {initPlaceHandlers} from './placeHandlers.js';
import {
    initSegmentHandlers, loadSegmentCreateForm
} from './segmentHandlers.js';
import {initAreaHandlers} from './areaHandlers.js';
import {setupQuill} from './quillNotes.js';
import {store} from './storeInstance.js';
import {initOrdering} from './regionsOrder.js';


let currentTripId = null;
let activeDrawingRegionId = null;
// search handling vars
const SEARCH_DELAY_MS = 500;
let debounceTimeout = null;
const SHADOW_REGION_NAME = 'Unassigned Places';

/* ------------------------------------------------------------------ *
 *  Helpers – banner
 * ------------------------------------------------------------------ */
const bannerEl = () => document.getElementById('mapping-context-banner');
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
    const label = bannerLbl();
    if (!banner || !label) return;

    const clean = name?.replace(/^[^\w]+/, '').trim() || 'Unnamed';
    const prefix = action === 'edit' ? 'Editing' : 'Selected';
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
            const lat = parseFloat(form.querySelector('[name="CenterLat"]')?.dataset.default ?? '');
            const lon = parseFloat(form.querySelector('[name="CenterLon"]')?.dataset.default ?? '');
            const zoom = parseInt(form.querySelector('[name="Zoom"]')?.dataset.default ?? '3', 10);

            if (!Number.isNaN(lat) && !Number.isNaN(lon)) getMapInstance()?.setView([lat, lon], zoom || 3);

            form.scrollIntoView({behavior: 'smooth', block: 'start'});
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
    store.subscribe(({type, payload}) => {

        /* ========== CONTEXT SET ========== */
        if (type === 'set-context') {
            const {id, type: ctxType, meta, action} = payload;
            hideAllIndicators();

            if (ctxType === 'place') {
                const placeItem = document.querySelector(`.place-list-item[data-place-id="${id}"]`);
                const placeForm = document.getElementById(`place-form-${id}`);
                const isEdit = action === 'edit';

                if (meta?.regionId) {
                    const regionItem = document.getElementById(`region-item-${meta.regionId}`);
                    const collapseEl = document.getElementById(`collapse-${meta.regionId}`);
                    if (collapseEl && !collapseEl.classList.contains('show')) {
                        try { bootstrap.Collapse.getOrCreateInstance(collapseEl, { toggle: false }).show(); } catch {}
                    }
                    if (regionItem) {
                        dimAll();
                        regionItem.querySelectorAll('.place-list-item')
                            .forEach(el => {
                                if (el !== placeItem) el.classList.add('dimmed');
                            });
                        regionItem.classList.remove('dimmed');
                        regionItem.querySelector('.accordion-button')?.classList.remove('dimmed');
                    }
                }

                // Remove dim effect from the selected item/form
                (placeItem || placeForm)?.classList.remove('dimmed');

                // Smooth-scroll the sidebar to the selected place
                const sidebar = document.getElementById('sidebarContent');
                const target = placeForm || placeItem;
                if (target && sidebar) {
                    // Use requestAnimationFrame to ensure collapse state is applied before scrolling
                    requestAnimationFrame(() => {
                        target.scrollIntoView({ behavior: 'smooth', block: 'center', inline: 'nearest' });
                    });
                }
            }

            if (ctxType === 'region') {
                /* Wrapper may be either the normal list item OR an open form */
                const regionItem = document.getElementById(`region-item-${id}`);
                const regionForm = document.getElementById(`region-form-${id}`);
                const wrapper = regionItem || regionForm;

                wrapper?.classList.remove('dimmed');
                wrapper?.querySelector('.accordion-button')?.classList?.remove('dimmed');

                document.querySelectorAll('.accordion-item')
                    .forEach(el => (el.id === `region-item-${id}` || el.id === `region-form-${id}`) ? el.classList.remove('dimmed') : el.classList.add('dimmed'));
            }

            if (ctxType === 'segment') {
                document.getElementById(`segment-item-${id}`)
                    ?.scrollIntoView({behavior: 'smooth', block: 'center'});
            }

            /* ----- banner ----- */
            (meta?.name && ctxType) ? setBanner(action, meta.name) : hideBanner();

            // 🧭 Sync lat/lon to inputs + form when context is set
            const formSelector = ctxType === 'place' ? `#place-form-${id}` : ctxType === 'region' ? `#region-form-${id}` : null;

            const latField = ctxType === 'place' ? 'Latitude' : 'CenterLat';
            const lonField = ctxType === 'place' ? 'Longitude' : 'CenterLon';

            const form = document.querySelector(formSelector);
            if (form) {
                const latRaw = form.querySelector(`input[name="${latField}"]`)?.value;
                const lonRaw = form.querySelector(`input[name="${lonField}"]`)?.value;

                const lat = parseFloat(latRaw);
                const lon = parseFloat(lonRaw);

                if (!isNaN(lat) && !isNaN(lon)) {
                    applyCoordinates({lat, lon});
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
            document.getElementById('add-to-trip-feedback')?.remove();
            clearDim();
            hideBanner();
            hideAllIndicators();

            document.querySelectorAll('form[id^="area-form-"]').forEach(form => {
                const regionId = form.querySelector('[name="RegionId"]')?.value;
                if (!regionId) return;

                fetch(`/User/Regions/GetItemPartial?regionId=${regionId}`)
                    .then(r => r.text())
                    .then(html => {
                        document.getElementById(`region-item-${regionId}`).outerHTML = html;
                        store.dispatch('region-dom-reloaded', {regionId});
                    });
            });

        }

        /**
         * re attach handlers after reload
         */
        if (type === 'region-dom-reloaded') {
            initRegionHandlers(currentTripId);
            initPlaceHandlers();
            initSegmentHandlers(currentTripId);
            initOrdering();
            // re-draw all Areas on the main map
            document.querySelectorAll(`.area-list-item[data-region-id="${payload.regionId}"]`).forEach(el => {
                const geom = JSON.parse(el.dataset.areaGeom || 'null');
                const fill = el.dataset.areaFill;
                if (geom) {
                    renderAreaPolygon({
                        Id: el.dataset.areaId, Geometry: geom, FillHex: fill
                    });
                }
            });

            // wire up area buttons & map drawing
            initAreaHandlers(currentTripId);
        }

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
                    store.dispatch('region-dom-reloaded', {regionId});
                }
            });

            // Places: remove open form and reload from server if needed
            document.querySelectorAll('form[id^="place-form-"]').forEach(async (form) => {
                const placeId = form.querySelector('[name="Id"]')?.value;
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
                        store.dispatch('region-dom-reloaded', {regionId});

                        // 🔁 Restore marker for the saved place
                        const placeItem = newRegionEl.querySelector(`.place-list-item[data-place-id="${placeId}"]`);
                        if (placeItem) {
                            const d = placeItem.dataset;
                            if (d.placeLat && d.placeLon) {
                                renderPlaceMarker({
                                    Id: d.placeId,
                                    Name: d.placeName || '',
                                    Latitude: d.placeLat,
                                    Longitude: d.placeLon,
                                    IconName: d.placeIcon,
                                    MarkerColor: d.placeColor,
                                    RegionId: d.regionId
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
                    store.dispatch('segment-dom-reloaded', {segmentId});
                }
            });

            // Areas: remove open form and reload from server if needed
            document.querySelectorAll('form[id^="area-form-"]').forEach(async (form) => {
                const areaId = form.querySelector('[name="Id"]')?.value;
                const regionId = form.querySelector('[name="RegionId"]')?.value;

                // 🆕 New Area form (not yet saved) → just remove the form
                if (!areaId || areaId.length !== 36) {
                    form.remove();
                } else {
                    // 📦 Existing Area → re-render its parent Region accordion item
                    const regionEl = document.getElementById(`region-item-${regionId}`);
                    if (regionEl) {
                        const resp = await fetch(`/User/Regions/GetItemPartial?regionId=${regionId}`);
                        const html = await resp.text();

                        // Use the same replaceOuterHtmlAndWait helper you use for Places
                        const newRegionEl = await replaceOuterHtmlAndWait(regionEl, html);
                        store.dispatch('region-dom-reloaded', {regionId});
                    }
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
                Id: d.placeId,
                Name: (d.placeName || el.querySelector('.place-name')?.textContent || '').trim(),
                Latitude: d.placeLat,
                Longitude: d.placeLon,
                IconName: d.placeIcon,
                MarkerColor: d.placeColor,
                RegionId: d.regionId
            });
        }
    });

    /* regions */
    document.querySelectorAll('#regions-accordion .accordion-item').forEach(item => {
        const lat = item.dataset.centerLat;
        const lon = item.dataset.centerLon;
        const id = item.id?.replace('region-item-', '');
        const name = item.dataset.regionName || 'Unnamed Region';
        if (id && lat && lon) renderRegionMarker({Id: id, CenterLat: lat, CenterLon: lon, Name: name});
    });

    /* areas */
    document.querySelectorAll('.area-list-item').forEach(el => {
        try {
            const geom = JSON.parse(el.dataset.areaGeom || 'null');
            const fill = el.dataset.areaFill;
            if (geom) {
                renderAreaPolygon({
                    Id: el.dataset.areaId, Geometry: geom, FillHex: fill
                });
            }
        } catch (err) {
            console.warn('⚠️ Failed to render persisted area polygon', err);
        }
    });
};

/**
 * Search handling
 */

/* ------------------------------------------------------------------ *
 *  Search handlers – place search via Nominatim
 * ------------------------------------------------------------------ */
const initSearchHandlers = () => {
    const input = document.getElementById('place-search');
    if (!input) return;

    input.addEventListener('input', () => {
        clearTimeout(debounceTimeout);

        const query = input.value.trim();
        if (!query) return;

        debounceTimeout = setTimeout(() => {
            searchNominatim(query);
        }, SEARCH_DELAY_MS);
    });
};

/* ------------------------------------------------------------------ *
 *  Sidebar Trip-Items Search (regions, places, areas, segments)
 * ------------------------------------------------------------------ */
const initSidebarTripItemSearch = () => {
    const input = document.getElementById('sidebar-search');
    if (!input) return;

    const norm = (s) => (s || '')
        .toString()
        .normalize('NFD').replace(/\p{Diacritic}/gu, '')
        .toLowerCase();

    const collect = () => {
        const items = [];
        // regions
        document.querySelectorAll('#regions-accordion .accordion-item[id^="region-item-"]').forEach(r => {
            const id = r.id.replace('region-item-', '');
            const name = r.dataset.regionName || r.querySelector('.accordion-button')?.textContent || '';
            items.push({ type: 'region', id, label: name, el: r, collapseId: `collapse-${id}` });
            // places within region
            r.querySelectorAll('.place-list-item').forEach(p => {
                items.push({ type: 'place', id: p.dataset.placeId, regionId: id, label: p.dataset.placeName || p.textContent || '', el: p, collapseId: `collapse-${id}` });
            });
            // areas within region
            r.querySelectorAll('.area-list-item').forEach(a => {
                const nm = a.querySelector('.area-name')?.textContent || '';
                items.push({ type: 'area', id: a.dataset.areaId, regionId: id, label: nm, el: a, collapseId: `collapse-${id}` });
            });
        });
        // segments (outside accordion)
        document.querySelectorAll('.segment-list-item').forEach(s => {
            items.push({ type: 'segment', id: s.dataset.segmentId, label: s.dataset.segmentName || s.textContent || '', el: s });
        });
        items.forEach(it => it.normal = norm(it.label));
        return items;
    };

    let index = collect();
    const refreshIndex = () => { index = collect(); };

    const clearHighlights = () => document.querySelectorAll('.search-hit').forEach(e => e.classList.remove('search-hit'));

    const ensureExpanded = (collapseId) => {
        if (!collapseId) return;
        const el = document.getElementById(collapseId);
        if (el && !el.classList.contains('show')) {
            try { bootstrap.Collapse.getOrCreateInstance(el, { toggle: false }).show(); } catch {}
        }
    };

    const doSearch = (q) => {
        clearHighlights();
        if (!q) return;
        refreshIndex();
        const tokens = norm(q).split(/\s+/).filter(Boolean);
        const matches = index.filter(it => tokens.every(t => it.normal.includes(t)));
        matches.forEach(m => m.el.classList.add('search-hit'));
        if (matches.length) {
            const first = matches[0];
            ensureExpanded(first.collapseId);
            requestAnimationFrame(() => first.el.scrollIntoView({ behavior: 'smooth', block: 'center', inline: 'nearest' }));
        }
    };

    input.addEventListener('input', () => doSearch(input.value));
};

const searchNominatim = async (query) => {
    const url = new URL('https://nominatim.openstreetmap.org/search');
    url.searchParams.set('q', query);
    url.searchParams.set('format', 'json');
    url.searchParams.set('limit', '6');
    url.searchParams.set('addressdetails', '1');

    try {
        const resp = await fetch(url);
        const results = await resp.json();
        renderSuggestions(results);
    } catch (err) {
        console.error('❌ Error querying Nominatim:', err);
        renderSuggestions([]);
    }
};

const renderSuggestions = (results) => {
    const list = document.getElementById('place-search-results');
    if (!list) return;

    list.innerHTML = '';
    list.style.display = results.length ? 'block' : 'none';

    results.forEach((r) => {
        const li = document.createElement('li');
        li.className = 'list-group-item list-group-item-action';
        li.title = 'Click to add this location as a temporary marker';
        li.tabIndex = 0;
        li.textContent = r.display_name;

        li.addEventListener('click', () => handleSelectResult(r));
        list.appendChild(li);
    });
};

const handleSelectResult = async (result) => {
    const lat = parseFloat(result.lat);
    const lon = parseFloat(result.lon);
    const name = result.display_name;

    store.dispatch('set-context', {
        type: 'search-temp', id: `temp-${Date.now()}`, action: 'preview', meta: {name, lat, lon}
    });

    getMapInstance()?.setView([lat, lon], 14);
    clearSuggestions();
    showAddToTripUI(result);
};

const clearSuggestions = () => {
    const list = document.getElementById('place-search-results');
    if (list) {
        list.innerHTML = '';
        list.style.display = 'none';
    }
};

/**
 * Returns every region the user can drop a searched place into,
 * always prepending the “Un-assigned” bucket even when its
 * accordion panel is still hidden.
 */
const getRegionOptions = () => {
    const opts = [];

    /* 1️⃣ visible accordion items */
    document.querySelectorAll('#regions-accordion .accordion-item')
        .forEach(item => opts.push({
            id: item.id.replace(/^region-item-/, ''),
            name: item.dataset.regionName?.trim() || 'Unnamed region',
            isShadow: item.dataset.shadowRegion === '1'   // ← detects “Un-assigned” if already rendered
        }));

    /* 2️⃣ hidden shadow region (from <input>) */
    const shadowId = document.getElementById('shadow-region-id')?.value;
    if (shadowId && !opts.some(o => o.id === shadowId)) opts.unshift({
        id: shadowId, name: SHADOW_REGION_NAME, isShadow: true
    });

    return opts;
};

/**
 * Shows a Bootstrap modal with the region picker instead of an inline alert.
 * Keeps the page layout fixed – no jumpy shift.
 */
const showAddToTripUI = (result) => {
    const regions = getRegionOptions();
    if (!regions.length) return;

    // 1️⃣ populate <select>
    const sel = document.getElementById('add-place-region-select');
    sel.replaceChildren();                       // clear previous options

    // Shadow first (if present)
    regions.filter(r => r.isShadow)
        .forEach(r => sel.append(new Option(SHADOW_REGION_NAME, r.id)));

    // Then normal regions
    regions.filter(r => !r.isShadow)
        .forEach(r => sel.append(new Option(r.name, r.id)));

    // 2️⃣ wire the Add button (fresh each time)
    const btn = document.getElementById('btn-confirm-add-place');
    btn.onclick = () => {
        addSearchedPlaceToTrip(result, sel.value);
        bsModal.hide();
    };

    // 3️⃣ show modal (lazy-initialise once)
    if (!window._addPlaceModal) window._addPlaceModal = new bootstrap.Modal(document.getElementById('addPlaceModal'), {
        backdrop: 'static'
    });
    const bsModal = window._addPlaceModal;
    bsModal.show();
};

/**
 * Turns a Nominatim result into a Place form, inserts it into the chosen
 * region and pre-fills coordinates so the user only needs to press Save.
 *
 * @param {object} result  Raw JSON from Nominatim.
 * @param {string} regionId Destination region Guid (may be the shadow id).
 */
const addSearchedPlaceToTrip = async (result, regionId) => {
    if (!regionId) return;

    try {
        /* close any other open editors */
        store.dispatch('trip-cleanup-open-forms');

        /* 🅰 ensure the region DOM exists (shadow may still be hidden) */
        let regionEl = document.getElementById(`region-item-${regionId}`);
        if (!regionEl) {
            const rResp = await fetch(`/User/Regions/GetItemPartial?regionId=${regionId}`);
            const rHtml = await rResp.text();
            document.getElementById('regions-accordion')
                .insertAdjacentHTML('afterbegin', rHtml);
            regionEl = document.getElementById(`region-item-${regionId}`);
            store.dispatch('region-dom-reloaded', {regionId});
        }

        /* 🅱 fetch a blank place form */
        const pResp = await fetch(`/User/Places/CreateOrUpdate?regionId=${regionId}`);
        const pHtml = await pResp.text();

        const container = regionEl.querySelector('[data-region-places]') || regionEl;
        container.insertAdjacentHTML('afterbegin', pHtml);

        /* locate the just-added form */
        const formEl = container.querySelector('form[id^="place-form-"]');

        /* pre-fill */
        formEl.querySelector('[name="Name"]').value = result.display_name;
        formEl.querySelector('[name="Latitude"]').value = (+result.lat).toFixed(6);
        formEl.querySelector('[name="Longitude"]').value = (+result.lon).toFixed(6);
        const addrInput = formEl.querySelector('[name="Address"]');
        if (addrInput) addrInput.value = result.display_name;

        /* enhance & wire handlers */
        const {enhancePlaceForm, initPlaceHandlers} = await import('./placeHandlers.js');
        await enhancePlaceForm(formEl);
        initPlaceHandlers();

        /* context, map, UI cleanup */
        const placeId = formEl.id.replace('place-form-', '');
        store.dispatch('set-context', {
            type: 'place', id: placeId, action: 'set-location', meta: {name: result.display_name, regionId}
        });
        document.getElementById('add-to-trip-feedback')?.remove();

        /* 🔽 UX niceties */
        formEl.scrollIntoView({behavior: 'smooth', block: 'center'});
        document.getElementById('place-search').value = '';
        clearSuggestions();
    } catch (err) {
        console.error('❌ addSearchedPlaceToTrip failed:', err);
        wayfarer.showAlert('danger', 'Could not add place – please try again.');
    }
};
/* ------------------------------------------------------------------ *
 *  Bootstrap
 * ------------------------------------------------------------------ */
document.addEventListener('DOMContentLoaded', async () => {

    /* ----- map initial centre (URL has priority) ----- */
    const urlParams = new URLSearchParams(window.location.search);
    const centerLat = parseFloat(urlParams.get('lat'));
    const centerLon = parseFloat(urlParams.get('lng'));
    const zoomParam = parseInt(urlParams.get('zoom'), 10);

    const form = document.getElementById('trip-form');
    const modelLat = parseFloat(form?.querySelector('[name="CenterLat"]')?.dataset.default ?? '0');
    const modelLon = parseFloat(form?.querySelector('[name="CenterLon"]')?.dataset.default ?? '0');
    const modelZoom = parseInt(form?.querySelector('[name="Zoom"]')?.dataset.default ?? '3', 10);

    const validUrlLat = !Number.isNaN(centerLat);
    const validUrlLon = !Number.isNaN(centerLon);
    const validModelLat = !Number.isNaN(modelLat);
    const validModelLon = !Number.isNaN(modelLon);
    const validZoom = !Number.isNaN(zoomParam) && zoomParam >= 0;

    const finalLat = validUrlLat ? centerLat : (validModelLat ? modelLat : 20);
    const finalLon = validUrlLon ? centerLon : (validModelLon ? modelLon : 0);
    const finalZoom = validZoom ? zoomParam : (!Number.isNaN(modelZoom) ? modelZoom : 3);

    /* ----- trip id ----- */
    currentTripId = form.querySelector('[name="Id"]')?.value;
    store.dispatch('set-trip-id', currentTripId);

    store.subscribe(({type}) => {
        if (type === 'clear-context') {
            store.dispatch('context-cleared');
        }
    });

    /* ----- map ----- */
    activeDrawingRegionId = null;
    disableDrawingTools();
    const map = initializeMap([finalLat, finalLon], finalZoom);

    /* keep hidden inputs + URL in sync */
    if (form) {
        form.querySelector('[name="CenterLat"]').value = finalLat.toFixed(6);
        form.querySelector('[name="CenterLon"]').value = finalLon.toFixed(6);
        form.querySelector('[name="Zoom"]').value = finalZoom;
    }

    map.on('moveend zoomend', () => {
        const c = map.getCenter();
        const z = map.getZoom();

        const params = new URLSearchParams(window.location.search);
        params.set('lat', c.lat.toFixed(6));
        params.set('lng', c.lng.toFixed(6));
        params.set('zoom', z);
        history.replaceState(null, '', `${window.location.pathname}?${params.toString()}`);

        form.querySelector('[name="CenterLat"]').value = c.lat.toFixed(6);
        form.querySelector('[name="CenterLon"]').value = c.lng.toFixed(6);
        form.querySelector('[name="Zoom"]').value = z;
    });

    map.on('click', ({latlng}) => applyCoordinates({lat: latlng.lat.toFixed(6), lon: latlng.lng.toFixed(6)}));
    const syncToContext = () => {
        const latInput = document.getElementById('contextLat');
        const lonInput = document.getElementById('contextLon');
        const lat = parseFloat(latInput?.value);
        const lon = parseFloat(lonInput?.value);
        if (!isNaN(lat) && !isNaN(lon)) {
            applyCoordinates({lat, lon});
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
    initAreaHandlers(currentTripId);

    await setupQuill();
    attachListeners();
    loadPersistedMarkers();
    initSearchHandlers();
    initSidebarTripItemSearch();
});
