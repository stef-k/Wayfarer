// trip.js – modular entry point for trip editing
import {rebindMainButtons} from './uiCore.js';
import {initializeMap, disableDrawingTools, getMapInstance, renderPlaceMarker, applyCoordinates, renderRegionMarker } from './mapManager.js';
import {initRegionHandlers} from './regionHandlers.js';
import {initPlaceHandlers} from './placeHandlers.js';
import {initSegmentHandlers, loadSegmentCreateForm} from './segmentHandlers.js';
import {setupQuill} from './quillNotes.js';
import {clearMappingContext, setMappingContext, getMappingContext} from './mappingContext.js';
import {initOrdering} from './regionsOrder.js';

let activeDrawingRegionId = null;
let currentTripId = null;

document.addEventListener('DOMContentLoaded', () => {
    const urlParams = new URLSearchParams(window.location.search);
    const lat = parseFloat(urlParams.get('lat'));
    const lng = parseFloat(urlParams.get('lng'));
    const zoom = parseInt(urlParams.get('zoom'), 10);
    currentTripId = document.querySelector('#trip-form input[name="Id"]')?.value;

    const center = (!isNaN(lat) && !isNaN(lng)) ? [lat, lng] : [20, 0];
    const zoomLevel = (!isNaN(zoom) && zoom >= 0) ? zoom : 3;

    activeDrawingRegionId = null;
    disableDrawingTools();

    const map = initializeMap(center, zoomLevel);

    map.on('click', ({latlng}) =>
        applyCoordinates({
            lat: latlng.lat.toFixed(6),
            lon: latlng.lng.toFixed(6)
        })
    );

    initOrdering();
    document.addEventListener('region-dom-reloaded', initOrdering);
    document.addEventListener('mapping-context-cleared', initOrdering);

    rebindMainButtons();
    initRegionHandlers(currentTripId);
    initPlaceHandlers();
    initSegmentHandlers();
    setupQuill();

    document.getElementById('btn-add-segment')?.addEventListener('click', () => {
        loadSegmentCreateForm(currentTripId);
    });

    loadPersistedMarkers();
});

document.addEventListener('mapping-context-changed', (e) => {
    const {type, meta, id, action} = e.detail;
    const banner = document.getElementById('mapping-context-banner');
    const label = document.getElementById('mapping-context-text');

    document.querySelectorAll('.selected-indicator').forEach(el => el.classList.add('d-none'));

    if (type === 'place') {
        document.getElementById(`place-indicator-${id}`)?.classList.remove('d-none');
    } else if (type === 'region') {
        document.getElementById(`region-indicator-${id}`)?.classList.remove('d-none');
    } else if (type === 'segment') {
        document.getElementById(`segment-indicator-${id}`)?.classList.remove('d-none');
    }

    document.querySelectorAll('.place-list-item').forEach(el =>
        el.classList.remove('bg-warning-subtle')
    );
    document.querySelectorAll('.accordion-button').forEach(el =>
        el.classList.remove('bg-info-subtle', 'bg-info-soft')
    );
    document.querySelectorAll('.segment-list-item').forEach(el =>
        el.classList.remove('bg-success-subtle')
    );

    let icon = '';
    if (type === 'place') {
        icon = '📍';
        const placeItem = document.querySelector(`.place-list-item[data-place-id="${id}"]`);
        placeItem?.classList.add('bg-warning-subtle');

        if (meta?.regionId) {
            const regionItem = document.getElementById(`region-item-${meta.regionId}`);
            regionItem?.classList.add('bg-info-soft');
        }

    } else if (type === 'region') {
        icon = '🗺️';
        const regionHeaderBtn = document.querySelector(`#region-item-${id} .accordion-button`);
        regionHeaderBtn?.classList.add('bg-info-subtle');

        const collapse = document.querySelector(`#collapse-${id}`);
        const accordionBtn = document.querySelector(`#region-item-${id} .accordion-button`);
        if (collapse && accordionBtn && !collapse.classList.contains('show')) {
            accordionBtn.click();
        }

    } else if (type === 'segment') {
        icon = '➡️';
        const segmentItem = document.querySelector(`.segment-list-item[data-segment-id="${id}"]`);
        segmentItem?.classList.add('bg-success-subtle');
    }

    if (type === 'region') {
        document.getElementById(`region-item-${id}`)?.scrollIntoView({behavior: 'smooth', block: 'start'});
    } else if (type === 'place') {
        document.querySelector(`.place-list-item[data-place-id="${id}"]`)?.scrollIntoView({
            behavior: 'smooth', block: 'nearest'
        });
    } else if (type === 'segment') {
        document.querySelector(`.segment-list-item[data-segment-id="${id}"]`)?.scrollIntoView({
            behavior: 'smooth', block: 'start'
        });
    }

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

    if (!type || !meta?.name) {
        banner.classList.remove('active');
        return;
    }

    label.textContent = `${icon} Editing: ${meta.name}`;
    banner.classList.add('active');
});

document.addEventListener('mapping-context-cleared', () => {
    const banner = document.getElementById('mapping-context-banner');
    banner?.classList.remove('active');
    activeDrawingRegionId = null;

    document.querySelectorAll('.place-list-item').forEach(el =>
        el.classList.remove('bg-warning-subtle')
    );
    document.querySelectorAll('.accordion-item').forEach(el =>
        el.classList.remove('bg-info-subtle', 'bg-info-soft')
    );
    document.querySelectorAll('.segment-list-item').forEach(el =>
        el.classList.remove('bg-success-subtle')
    );
    document.querySelectorAll('.selected-indicator').forEach(el =>
        el.classList.add('d-none')
    );
});

document.addEventListener('region-dom-reloaded', () => {
    if (!currentTripId) return;
    initRegionHandlers(currentTripId);
    initPlaceHandlers();
    loadPersistedMarkers();
});

document.getElementById('btn-clear-context')?.addEventListener('click', () => {
    clearMappingContext();
});

document.addEventListener('place-context-selected', (e) => {
    const {placeId, regionId, name} = e.detail;

    document.querySelectorAll('.place-list-item').forEach(item =>
        item.classList.remove('bg-warning-subtle')
    );

    const selected = document.querySelector(`.place-list-item[data-place-id="${placeId}"]`);
    selected?.classList.add('bg-warning-subtle');

    setMappingContext({
        type: 'place',
        id: placeId,
        action: 'set-location',
        meta: {name, regionId}
    });
});

const loadPersistedMarkers = () => {
    document.querySelectorAll('.place-list-item').forEach(el => {
        const d = el.dataset;
        if (d.placeLat && d.placeLon) {
            renderPlaceMarker({
                Id:          d.placeId,
                Name:        el.querySelector('.place-name')?.textContent || '',
                Latitude:    d.placeLat,
                Longitude:   d.placeLon,
                IconName:    d.placeIcon,
                MarkerColor: d.placeColor,
                RegionId:    d.regionId
            });
        }
    });
    /* ---------- regions ---------- */
    document.querySelectorAll('#regions-accordion .accordion-item')
        .forEach(item => {
            const lat  = item.dataset.centerLat;
            const lon  = item.dataset.centerLon;
            const id   = item.id?.replace('region-item-', '');
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
