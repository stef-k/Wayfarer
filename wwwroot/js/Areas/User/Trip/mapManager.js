// mapManager.js – refactored to use store
import {
    addZoomLevelControl,
    latestLocationMarker
} from '../../../map-utils.js';

import { store } from './storeInstance.js';

/* ------------------------------------------------------------------ *
 *  Private state
 * ------------------------------------------------------------------ */
let mapContainer = null;
let drawControl = null;
let drawnLayerGroup = null;
let selectedMarker = null;
let previewMarker = null;

const placeMarkersById = {};
const regionMarkersById = {};
const regionPreviewById = {};

const WF_WIDTH = 28;
const WF_HEIGHT = 45;
const WF_ANCHOR = [14, 45];

export const getRegionMarkerById = (id) => regionMarkersById[id] || null;
export const getPlaceMarkerById = (id) => placeMarkersById[id] || null;

export const clearPreviewMarker = () => {
    if (previewMarker) {
        mapContainer.removeLayer(previewMarker);
        previewMarker = null;
    }
};


const buildPngIconUrl = (iconName, bgClass) =>
    `/icons/wayfarer-map-icons/dist/png/marker/${bgClass}/${iconName}.png`;

export const applyCoordinates = ({ lat, lon }) => {
    const ctx = store.getState().context;
    if (!ctx?.type || !ctx?.action) return;

    const latNum = parseFloat(lat);
    const lonNum = parseFloat(lon);

    const fill = (selector, fldLat, fldLon) => {
        const form = document.querySelector(selector);
        if (!form) return;

        const latInp = form.querySelector(`input[name="${fldLat}"]`);
        const latDisp = form.querySelector(`input[name="${fldLat}_display"]`);
        if (!isNaN(latNum)) {
            if (latInp) latInp.value = latNum;
            if (latDisp) latDisp.value = latNum;
        }

        const lonInp = form.querySelector(`input[name="${fldLon}"]`);
        const lonDisp = form.querySelector(`input[name="${fldLon}_display"]`);
        if (!isNaN(lonNum)) {
            if (lonInp) lonInp.value = lonNum;
            if (lonDisp) lonDisp.value = lonNum;
        }

        // ✅ Place marker preview
        if (ctx.type === 'place') {
            // remove saved marker before creating preview
            const existing = placeMarkersById[ctx.id];
            if (existing) {
                mapContainer.removeLayer(existing);
                delete placeMarkersById[ctx.id];
            }

            if (previewMarker) {
                previewMarker.setLatLng([lat, lon]);
            } else {
                const icon = form.querySelector('input[name="IconName"]')?.value || 'marker';
                const color = form.querySelector('input[name="MarkerColor"]')?.value || 'bg-blue';
                const iconUrl = buildPngIconUrl(icon, color);
                previewMarker = L.marker([lat, lon], {
                    icon: L.icon({
                        iconUrl,
                        iconSize: [WF_WIDTH, WF_HEIGHT],
                        iconAnchor: WF_ANCHOR,
                        className: 'map-icon'
                    })
                }).addTo(mapContainer);
            }

            selectMarker(previewMarker);
        }

        //  Region marker update
        if (ctx.type === 'region') {
            const regionMarker = regionMarkersById[ctx.id];
            if (regionMarker && !isNaN(latNum) && !isNaN(lonNum)) {
                regionMarker.setLatLng([latNum, lonNum]);
            }
        }
    };

    if (ctx.type === 'place' && ['set-location', 'edit', 'create'].includes(ctx.action)) {
        fill(`#place-form-${ctx.id}`, 'Latitude', 'Longitude');
    }

    if (ctx.type === 'region' && ctx.action === 'set-center') {
        fill(`#region-form-${ctx.id}`, 'CenterLat', 'CenterLon');
    }

    // ✅ Context banner coordinates
    const coordsEl = document.getElementById('context-coords');
    if (coordsEl && !isNaN(latNum) && !isNaN(lonNum)) {
        coordsEl.innerHTML = `Coordinates (Lat, Lon): <code class="user-select-all">${latNum.toFixed(5)}, ${lonNum.toFixed(5)}</code>`;
        coordsEl.classList.remove('d-none');
    }

    // ✅ Sync visible Lat/Lon fields
    const latInput = document.getElementById('contextLat');
    const lonInput = document.getElementById('contextLon');
    if (latInput && lonInput && !isNaN(latNum) && !isNaN(lonNum)) {
        latInput.value = latNum.toFixed(6);
        lonInput.value = lonNum.toFixed(6);
    }
};

export const renderRegionMarker = async ({ Id, CenterLat, CenterLon, Name }) => {
    if (!CenterLat || !CenterLon) return;
    const lat = +CenterLat, lon = +CenterLon;
    if (isNaN(lat) || isNaN(lon)) return;

    if (regionMarkersById[Id]) mapContainer.removeLayer(regionMarkersById[Id]);

    const iconUrl = buildPngIconUrl('map', 'bg-red');
    const marker = L.marker([lat, lon], {
        icon: L.icon({ iconUrl, iconSize: [WF_WIDTH, WF_HEIGHT], iconAnchor: WF_ANCHOR, className: 'map-icon' })
    }).addTo(mapContainer);

    marker.on('click', () => {
        clearSelectedMarker();
        selectMarker(marker);
        store.dispatch('set-context', {
            type: 'region', id: Id, action: 'set-center', meta: { name: Name || 'Unnamed Region' }
        });
    });

    regionMarkersById[Id] = marker;
};

export const removeRegionMarker = (id) => {
    if (regionMarkersById[id]) {
        mapContainer.removeLayer(regionMarkersById[id]);
        delete regionMarkersById[id];
    }
};

export const renderPlaceMarker = async (p) => {
    if (!p?.Latitude || !p?.Longitude) return;
    const lat = +p.Latitude, lon = +p.Longitude;
    if (isNaN(lat) || isNaN(lon)) return;

    if (placeMarkersById[p.Id]) mapContainer.removeLayer(placeMarkersById[p.Id]);

    const iconUrl = buildPngIconUrl(p.IconName || 'marker', p.MarkerColor || 'bg-blue');
    const marker = L.marker([lat, lon], {
        icon: L.icon({ iconUrl, iconSize: [WF_WIDTH, WF_HEIGHT], iconAnchor: WF_ANCHOR, className: 'map-icon' })
    }).addTo(mapContainer);

    marker.on('click', () => {
        clearSelectedMarker();
        selectMarker(marker);
        store.dispatch('set-context', {
            type: 'place', id: p.Id, action: 'set-location', meta: { name: p.Name, regionId: p.RegionId }
        });
    });

    placeMarkersById[p.Id] = marker;
};

export const removePlaceMarker = (id) => {
    if (placeMarkersById[id]) {
        mapContainer.removeLayer(placeMarkersById[id]);
        delete placeMarkersById[id];
    }
};

export const disableDrawingTools = () => {
    if (!mapContainer || !drawControl) return;
    try { mapContainer.removeControl(drawControl); } catch { }
    drawControl = null;
    drawnLayerGroup?.clearLayers();
};

export const initializeMap = (center = [20, 0], zoom = 3) => {
    if (mapContainer) { mapContainer.off(); mapContainer.remove(); }
    mapContainer = L.map('mapContainer', {
        zoomAnimation: true,
        editable: true
    }).setView(center, zoom);
    
    L.tileLayer(`${location.origin}/Public/tiles/{z}/{x}/{y}.png`, {
        maxZoom: 19,
        attribution: '© OpenStreetMap contributors'
    }).addTo(mapContainer);

    mapContainer.attributionControl.setPrefix('&copy; <a href="https://leafletjs.com/">Leaflet</a>');
    addZoomLevelControl(mapContainer);

    window.addEventListener('resize', () => mapContainer.invalidateSize());

    mapContainer.createPane('region-boundary');
    Object.assign(mapContainer.getPane('region-boundary').style, {
        zIndex: 250,
        pointerEvents: 'auto'
    });

    window.wayfarer = window.wayfarer || {};
    window.wayfarer._leaflet_map = mapContainer;
    return mapContainer;
};

export const clearSelectedMarker = () => {
    if (!selectedMarker) return;

    const el = selectedMarker.getElement?.();
    if (el) {
        el.classList.remove('selected-marker');
        el.style.removeProperty('--selected-shadow-color');
    }

    selectedMarker = null;
};

export const selectMarker = (marker) => {
    clearSelectedMarker();
    const el = marker.getElement?.();
    if (!el || !(el instanceof HTMLImageElement)) {
        requestAnimationFrame(() => selectMarker(marker));
        return;
    }

    const src = el.getAttribute('src') || '';
    const match = src.match(/\/(bg-[a-z]+)\//i);
    const bgClass = match?.[1] || 'bg-blue';

    const colorMap = {
        'bg-black': '#000000',
        'bg-purple': '#6f42c1',
        'bg-blue': '#0d6efd',
        'bg-green': '#198754',
        'bg-red': '#dc3545',
    };

    const color = colorMap[bgClass] || '#0d6efd';
    el.style.setProperty('--selected-shadow-color', color);
    el.classList.add('selected-marker');
    selectedMarker = marker;
};

export const addLayer = (layer) => {
    const map = getMapInstance();
    if (!map) return;
    map.addLayer(layer);
};

export const removeLayer = (layer) => {
    const map = getMapInstance();
    if (!map) return;
    map.removeLayer(layer);
};

export const fitBounds = (bounds, options = {}) => {
    const map = getMapInstance();
    if (!map) return;
    map.fitBounds(bounds, options);
};

store.subscribe(({ type }) => {
    if (type === 'context-cleared') {
        Object.values(regionPreviewById).forEach(m => mapContainer?.removeLayer(m));
        Object.keys(regionPreviewById).forEach(k => delete regionPreviewById[k]);

        try {
            clearSelectedMarker?.();
        } catch (err) {
            console.warn('⚠️ Failed to clear selected marker from mapManager', err);
        }

        try {
            if (previewMarker) {
                mapContainer?.removeLayer(previewMarker);
                previewMarker = null;
            }
        } catch (err) {
            console.warn('⚠️ Failed to clear preview marker', err);
        }

        const coordsEl = document.getElementById('context-coords');
        if (coordsEl) {
            coordsEl.classList.add('d-none');
            coordsEl.innerHTML = '';
        }
    }
});

export const getMapInstance = () => mapContainer;
