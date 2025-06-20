// trip.js – modular entry point for trip editing
import {rebindMainButtons, saveTrip} from './uiCore.js';
import {
    initializeMap,
    disableDrawingTools,
    getMapInstance,
    renderPlaceMarker,
    applyCoordinates,
    renderRegionMarker
} from './mapManager.js';
import {initRegionHandlers} from './regionHandlers.js';
import {initPlaceHandlers} from './placeHandlers.js';
import {initSegmentHandlers, loadSegmentCreateForm} from './segmentHandlers.js';
import {setupQuill} from './quillNotes.js';
import {clearMappingContext, setMappingContext, getMappingContext} from './mappingContext.js';
import {initOrdering} from './regionsOrder.js';

let activeDrawingRegionId = null;
let currentTripId = null;

const attachListeners = () => {
    const form = document.getElementById('trip-form');
    if (!form) return;

    document.getElementById('btn-save-trip')?.addEventListener('click', () => {
        saveTrip('save');
    });

    document.getElementById('btn-save-edit-trip')?.addEventListener('click', () => {
        saveTrip('save-edit');
    });

    document.getElementById('btn-trip-recenter')?.addEventListener('click', () => {
        const lat = parseFloat(form.querySelector('[name="CenterLat"]')?.dataset.default || '');
        const lon = parseFloat(form.querySelector('[name="CenterLon"]')?.dataset.default || '');
        const zoom = parseInt(form.querySelector('[name="Zoom"]')?.dataset.default || '3', 10);

        if (!isNaN(lat) && !isNaN(lon)) {
            const map = getMapInstance();
            if (map) map.setView([lat, lon], zoom || 3);
        }
        // Scroll trip form into view
        document.getElementById('trip-form')?.scrollIntoView({
            behavior: 'smooth',
            block: 'start'
        });
        // Clear any existing mapping context (places/regions/segments)
        document.dispatchEvent(new CustomEvent('mapping-context-cleared'));
    });

    document.getElementById('btn-add-segment')?.addEventListener('click', () => {
        loadSegmentCreateForm(currentTripId);
    });

    document.getElementById('btn-clear-context')?.addEventListener('click', () => {
        clearMappingContext();
    });

    document.addEventListener('mapping-context-changed', (e) => {
        const { type, meta, id } = e.detail;
        const banner = document.getElementById('mapping-context-banner');
        const label = document.getElementById('mapping-context-text');

        // Clear all indicators
        document.querySelectorAll('.selected-indicator').forEach(el =>
            el.classList.add('d-none')
        );
        
        // Full clean before applying highlights
        document.querySelectorAll('.place-list-item').forEach(el =>
            el.classList.remove('bg-warning-subtle', 'bg-info-subtle', 'dimmed', 'region-place-neutral')
        );
        document.querySelectorAll('.accordion-button').forEach(el =>
            el.classList.remove('bg-info-subtle', 'bg-info-soft')
        );
        document.querySelectorAll('.segment-list-item').forEach(el =>
            el.classList.remove('bg-success-subtle')
        );
        document.querySelectorAll('.accordion-item').forEach(el =>
            el.classList.remove('bg-info-soft', 'bg-primary-subtle', 'dimmed')
        );

        if (type === 'place') {
            const placeItem = document.querySelector(`.place-list-item[data-place-id="${id}"]`);
            placeItem?.classList.add('bg-warning-subtle');

            if (meta?.regionId) {
                const regionItem = document.getElementById(`region-item-${meta.regionId}`);
                regionItem?.classList.add('bg-info-soft');
                regionItem?.querySelector('.accordion-button')?.classList.add('bg-info-soft');

                // Dim siblings
                regionItem?.querySelectorAll('.place-list-item').forEach(el => {
                    if (el !== placeItem) el.classList.add('dimmed');
                });

                // Dim other regions
                document.querySelectorAll('.accordion-item').forEach(item => {
                    if (item.id !== `region-item-${meta.regionId}`) {
                        item.classList.add('dimmed');
                    }
                });
            }

        } else if (type === 'region') {
            const regionItem = document.getElementById(`region-item-${id}`);
            regionItem?.classList.add('bg-primary-subtle');
            regionItem?.querySelector('.accordion-button')?.classList.add('bg-info-subtle');

            // Reset all places inside the selected region
            regionItem?.querySelectorAll('.place-list-item').forEach(el =>
                el.classList.add('region-place-neutral')
            );

            // Dim other regions
            document.querySelectorAll('.accordion-item').forEach(el => {
                const isRegionItem = el.id === `region-item-${id}`;
                const isRegionForm = el.id === `region-form-${id}`;
                if (!isRegionItem && !isRegionForm) {
                    el.classList.add('dimmed');
                }
            });

            const collapse = regionItem?.querySelector('.accordion-collapse');
            if (collapse && !collapse.classList.contains('show')) {
                regionItem.querySelector('.accordion-button')?.click();
            }

        } else if (type === 'segment') {
            const segmentItem = document.querySelector(`.segment-list-item[data-segment-id="${id}"]`);
            segmentItem?.classList.add('bg-success-subtle');
        }

        // Scroll selected item into view
        if (type === 'region') {
            document.getElementById(`region-item-${id}`)?.scrollIntoView({ behavior: 'smooth', block: 'start' });
        } else if (type === 'place') {
            document.querySelector(`.place-list-item[data-place-id="${id}"]`)?.scrollIntoView({
                behavior: 'smooth', block: 'nearest'
            });
        } else if (type === 'segment') {
            document.querySelector(`.segment-list-item[data-segment-id="${id}"]`)?.scrollIntoView({
                behavior: 'smooth', block: 'start'
            });
        }

        // Recenter map if place
        const map = getMapInstance();
        if (type === 'place') {
            const latInput = document.querySelector(`#place-form-${id} input[name="Latitude"]`);
            const lonInput = document.querySelector(`#place-form-${id} input[name="Longitude"]`);
            const lat = parseFloat(latInput?.value);
            const lon = parseFloat(lonInput?.value);
            if (!isNaN(lat) && !isNaN(lon)) {
                map?.setView([lat, lon], 14);
            }
        }

        // Show context banner
        if (!type || !meta?.name) {
            banner.classList.remove('active');
            return;
        }

        const cleanName = meta.name.replace(/^[^\w]+/, '').trim();
        const actionLabel = e.detail.action === 'edit' ? 'Editing' : 'Selected';
        label.innerHTML = `<span class="me-1"></span>${actionLabel}: ${cleanName}`;
        banner.classList.add('active');
    });


    document.addEventListener('mapping-context-cleared', () => {
        const banner = document.getElementById('mapping-context-banner');
        banner?.classList.remove('active');
        activeDrawingRegionId = null;

        // Clear all selected indicators
        document.querySelectorAll('.selected-indicator').forEach(el =>
            el.classList.add('d-none')
        );

        // Clear all place highlights and dimming
        document.querySelectorAll('.place-list-item').forEach(el =>
            el.classList.remove(
                'bg-warning-subtle',
                'bg-info-subtle',
                'dimmed',
                'region-place-neutral'
            )
        );

        // Clear segment highlights
        document.querySelectorAll('.segment-list-item').forEach(el =>
            el.classList.remove('bg-success-subtle')
        );

        // Clear region highlights and dimming
        document.querySelectorAll('.accordion-item').forEach(el =>
            el.classList.remove('bg-primary-subtle', 'bg-info-subtle', 'bg-info-soft', 'dimmed')
        );

        // Clear region header backgrounds
        document.querySelectorAll('.accordion-button').forEach(el =>
            el.classList.remove('bg-primary-subtle', 'bg-info-subtle', 'bg-info-soft')
        );
    });


    document.addEventListener('region-dom-reloaded', () => {
        if (!currentTripId) return;
        initRegionHandlers(currentTripId);
        initPlaceHandlers();
        loadPersistedMarkers();
    });

    document.addEventListener('place-context-selected', (e) => {
        const { placeId, regionId, name } = e.detail;

        // Remove highlights from all
        document.querySelectorAll('.place-list-item').forEach(item =>
            item.classList.remove('bg-warning-subtle', 'bg-info-subtle')
        );

        // Highlight selected place row
        const selected = document.querySelector(`.place-list-item[data-place-id="${placeId}"]`);
        selected?.classList.add('bg-warning-subtle');

        // Highlight region wrapper (soft background)
        const regionCard = document.getElementById(`region-item-${regionId}`);
        regionCard?.classList.add('bg-info-soft');

        // ✅ Highlight region header too
        regionCard?.querySelector('.accordion-button')?.classList.add('bg-info-subtle');

        // Show 📍 icon
        document.querySelectorAll('.selected-indicator').forEach(el =>
            el.classList.add('d-none')
        );
        document.getElementById(`place-indicator-${placeId}`)?.classList.remove('d-none');

        // Set context
        setMappingContext({
            type: 'place',
            id: placeId,
            action: 'set-location',
            meta: { name, regionId }
        });
    });

};

const loadPersistedMarkers = () => {
    document.querySelectorAll('.place-list-item').forEach(el => {
        const d = el.dataset;
        if (d.placeLat && d.placeLon) {
            renderPlaceMarker({
                Id: d.placeId,
                Name: el.querySelector('.place-name')?.textContent || '',
                Latitude: d.placeLat,
                Longitude: d.placeLon,
                IconName: d.placeIcon,
                MarkerColor: d.placeColor,
                RegionId: d.regionId
            });
        }
    });
    /* ---------- regions ---------- */
    document.querySelectorAll('#regions-accordion .accordion-item')
        .forEach(item => {
            const lat = item.dataset.centerLat;
            const lon = item.dataset.centerLon;
            const id = item.id?.replace('region-item-', '');
            const name = item.dataset.regionName || 'Unnamed Region';
            if (id && lat && lon) {
                renderRegionMarker({
                    Id: id,
                    CenterLat: lat,
                    CenterLon: lon,
                    Name: name
                });
            }
        });
};

// Entry point
document.addEventListener('DOMContentLoaded', async() => {
    const urlParams = new URLSearchParams(window.location.search);
    const centerLat = parseFloat(urlParams.get('lat'));
    const centerLon = parseFloat(urlParams.get('lng'));
    const zoomParam = parseInt(urlParams.get('zoom'), 10);

    const form = document.getElementById('trip-form');
    const modelLat = parseFloat(form?.querySelector('[name="CenterLat"]')?.dataset.default || '0');
    const modelLon = parseFloat(form?.querySelector('[name="CenterLon"]')?.dataset.default || '0');
    const modelZoom = parseInt(form?.querySelector('[name="Zoom"]')?.dataset.default || '3', 10);

    const lat = !isNaN(centerLat) ? centerLat : modelLat;
    const lon = !isNaN(centerLon) ? centerLon : modelLon;
    const zoom = !isNaN(zoomParam) && zoomParam >= 0 ? zoomParam : modelZoom;

    const center = [lat, lon];
    const zoomLevel = zoom;

    const tripIdField = form.querySelector('[name="Id"]');
    if (tripIdField) {
        currentTripId = tripIdField.value;
    }

    
    activeDrawingRegionId = null;
    disableDrawingTools();

    const map = initializeMap(center, zoomLevel);

    if (form) {
        const latInput = form.querySelector('[name="CenterLat"]');
        const lonInput = form.querySelector('[name="CenterLon"]');
        const zoomInput = form.querySelector('[name="Zoom"]');

        if (latInput) latInput.value = center[0].toFixed(6);
        if (lonInput) lonInput.value = center[1].toFixed(6);
        if (zoomInput) zoomInput.value = zoomLevel;
    }

    map.on('moveend zoomend', () => {
        const c = map.getCenter();
        const z = map.getZoom();

        const params = new URLSearchParams(window.location.search);
        params.set('lat', c.lat.toFixed(6));
        params.set('lng', c.lng.toFixed(6));
        params.set('zoom', z);
        history.replaceState(null, '', `${window.location.pathname}?${params.toString()}`);

        if (form) {
            const latInput = form.querySelector('[name="CenterLat"]');
            const lonInput = form.querySelector('[name="CenterLon"]');
            const zoomInput = form.querySelector('[name="Zoom"]');

            if (latInput) latInput.value = c.lat.toFixed(6);
            if (lonInput) lonInput.value = c.lng.toFixed(6);
            if (zoomInput) zoomInput.value = z;
        }
    });

    map.on('click', ({latlng}) =>
        applyCoordinates({
            lat: latlng.lat.toFixed(6),
            lon: latlng.lng.toFixed(6)
        })
    );

    initOrdering();
    rebindMainButtons();
    initRegionHandlers(currentTripId);
    initPlaceHandlers();
    initSegmentHandlers();
    await setupQuill();
    attachListeners();
    loadPersistedMarkers();
});