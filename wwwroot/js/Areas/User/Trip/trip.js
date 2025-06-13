// trip.js – modular entry point for trip editing
import {rebindMainButtons} from './uiCore.js';
import {initializeMap, setupDrawingTools, renderRegionBoundary, disableDrawingTools, removeRegionBoundaryFromMap  } from './mapManager.js';
import {initRegionHandlers} from './regionHandlers.js';
import {initPlaceHandlers} from './placeHandlers.js';
import {initSegmentHandlers, loadSegmentCreateForm} from './segmentHandlers.js';
import {setupQuill} from './quillNotes.js';
import {clearMappingContext, setMappingContext} from './mappingContext.js';

let activeDrawingRegionId = null;

document.addEventListener('DOMContentLoaded', () => {
    const urlParams = new URLSearchParams(window.location.search);
    const lat = parseFloat(urlParams.get('lat'));
    const lng = parseFloat(urlParams.get('lng'));
    const zoom = parseInt(urlParams.get('zoom'), 10);
    const tripId = document.querySelector('#trip-form input[name="Id"]')?.value;

    const center = (!isNaN(lat) && !isNaN(lng)) ? [lat, lng] : [20, 0];
    const zoomLevel = (!isNaN(zoom) && zoom >= 0) ? zoom : 3;

    activeDrawingRegionId = null;
    disableDrawingTools();

    initializeMap(center, zoomLevel);
    
    // show all region boundaries
    document.querySelectorAll('input[type="hidden"][id^="region-boundary-"]').forEach(input => {
        try {
            const geoJson = JSON.parse(input.value);
            if (geoJson) {
                const regionId = input.id.replace('region-boundary-', '');
                renderRegionBoundary(geoJson, regionId);
            }
        } catch (err) {
            console.warn('Could not parse region boundary for region', input.id, err);
        }
    });
    
    rebindMainButtons(); // Setup save buttons
    initRegionHandlers(tripId);
    initPlaceHandlers();
    initSegmentHandlers();
    setupQuill();

    document.getElementById('btn-add-segment')?.addEventListener('click', () => {
        loadSegmentCreateForm(tripId);
    });

});

document.addEventListener('mapping-context-changed', (e) => {
    const {type, meta, id} = e.detail;
    const banner = document.getElementById('mapping-context-banner');
    const label = document.getElementById('mapping-context-text');

    // Clear all icons
    document.querySelectorAll('.selected-indicator').forEach(el => el.classList.add('d-none'));

    // Cancel drawing mode if switching away from drawing context
    if (e.detail.type !== 'region' || e.detail.action !== 'draw-boundary') {
        disableDrawingTools();
        activeDrawingRegionId = null;
    }

    // Then show only the active one:
    if (type === 'place') {
        document.getElementById(`place-indicator-${id}`)?.classList.remove('d-none');
    } else if (type === 'region') {
        document.getElementById(`region-indicator-${id}`)?.classList.remove('d-none');
    } else if (type === 'segment') {
        document.getElementById(`segment-indicator-${id}`)?.classList.remove('d-none');
    }

    // Clear all highlights before applying new one
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

        // Highlight place
        const placeItem = document.querySelector(`.place-list-item[data-place-id="${e.detail.id}"]`);
        placeItem?.classList.add('bg-warning-subtle');

        // Highlight containing region softly
        if (meta?.regionId) {
            const regionItem = document.getElementById(`region-item-${meta.regionId}`);
            regionItem?.classList.add('bg-info-soft');
        }

    } else if (type === 'region') {
        icon = '🗺️';

        const regionHeaderBtn = document.querySelector(`#region-item-${e.detail.id} .accordion-button`);
        regionHeaderBtn?.classList.add('bg-info-subtle');

        // Check if region has boundary in DOM
        const hiddenField = document.getElementById(`region-boundary-${id}`);
        if (hiddenField?.value) {
            try {
                const polygonGeoJson = JSON.parse(hiddenField.value);
                renderRegionBoundary(polygonGeoJson);
            } catch (e) {
                console.warn('Could not parse region boundary:', e);
            }
        }

    } else if (type === 'segment') {
        icon = '➡️';

        const segmentItem = document.querySelector(`.segment-list-item[data-segment-id="${e.detail.id}"]`);
        segmentItem?.classList.add('bg-success-subtle');
    }

    if (!type || !meta?.name) {
        banner.classList.remove('active');
        return;
    }

    if (type === 'region' && e.detail.action === 'draw-boundary') {
        if (activeDrawingRegionId !== id) {
            setupDrawingTools(id);
            activeDrawingRegionId = id;
        } else {
            disableDrawingTools();
            clearMappingContext();
            activeDrawingRegionId = null;
        }
    }

    label.textContent = `${icon} Editing: ${meta.name}`;
    banner.classList.add('active');
});

document.addEventListener('mapping-context-cleared', () => {
    const banner = document.getElementById('mapping-context-banner');
    banner?.classList.remove('active');
    activeDrawingRegionId = null;

    // Remove highlights from all object types
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

document.addEventListener('boundary-saved', async (e) => {
    const regionId = e.detail.regionId;

    try {
        const resp = await fetch(`/User/Regions/GetItemPartial?regionId=${regionId}`);
        if (!resp.ok) throw new Error("Failed to reload region partial");

        const html = await resp.text();
        const container = document.getElementById(`region-item-${regionId}`);
        if (container) {
            const wrapper = document.createElement('div');
            wrapper.innerHTML = html;
            container.replaceWith(wrapper.firstElementChild);
            document.dispatchEvent(new CustomEvent('region-dom-reloaded'));
        }

        // ✅ Re-render region boundary if present
        const updatedInput = document.getElementById(`region-boundary-${regionId}`);
        if (updatedInput && updatedInput.value) {
            try {
                const geoJson = JSON.parse(updatedInput.value);
                renderRegionBoundary(geoJson, regionId);
            } catch (err) {
                console.warn('Could not parse reloaded boundary:', err);
            }
        } else {
            // Boundary was deleted
            removeRegionBoundaryFromMap(regionId);
        }

    } catch (err) {
        console.error("Reloading region failed:", err);
    }
});


document.addEventListener('region-dom-reloaded', () => {
    initRegionHandlers();
    initPlaceHandlers();
});

document.getElementById('btn-clear-context')?.addEventListener('click', () => {
    clearMappingContext();
});


// ✅ New: handle place click globally and set context
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
