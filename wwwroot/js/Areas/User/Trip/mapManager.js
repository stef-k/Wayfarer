// mapManager.js
import {
    addZoomLevelControl,
    latestLocationMarker            // little green pin for “centre here”
} from '../../../map-utils.js';

import { setMappingContext, getMappingContext } from './mappingContext.js';

/* ------------------------------------------------------------------ *
 *  Private state
 * ------------------------------------------------------------------ */
let mapContainer   = null;
let drawControl    = null;
let drawnLayerGroup= null;
let selectedMarker = null;
let previewMarker = null;

const placeMarkersById  = {};
const regionMarkersById = {};
const regionPreviewById = {};

/* 25×41 is Leaflet’s reference pin size. 12×41 = bottom-centre tip. */
const WF_WIDTH  = 28;
const WF_HEIGHT = 45;
const WF_ANCHOR = [14, 45];

export const getRegionMarkerById = (id) => regionMarkersById[id] || null;
export const getPlaceMarkerById  = (id) => placeMarkersById[id]  || null;

/* ------------------------------------------------------------------ *
 *  Build PNG URL
 * ------------------------------------------------------------------ */
const buildPngIconUrl = (iconName, bgClass) =>
    `/icons/wayfarer-map-icons/dist/png/marker/${bgClass}/${iconName}.png`;

export const applyCoordinates = ({ lat, lon }) => {
    const ctx = getMappingContext();
    if (!ctx?.type || !ctx?.action) return;

    const fill = (selector, fldLat, fldLon) => {
        const form = document.querySelector(selector);
        if (!form) return;

        const latInp = form.querySelector(`input[name="${fldLat}"]`);
        const lonInp = form.querySelector(`input[name="${fldLon}"]`);

        if (latInp) {
            latInp.value = lat;
            const latDisp = form.querySelector(`input[name="${fldLat}_display"]`);
            if (latDisp) latDisp.value = lat;
        }

        if (lonInp) {
            lonInp.value = lon;
            const lonDisp = form.querySelector(`input[name="${fldLon}_display"]`);
            if (lonDisp) lonDisp.value = lon;
        }

        // Remove previous temp marker if exists
        if (previewMarker) {
            mapContainer?.removeLayer(previewMarker);
            previewMarker = null;
        }

        // Show marker only in place context
        if (ctx.type === 'place') {
            const iconInput = form.querySelector('input[name="IconName"]');
            const colorInput = form.querySelector('input[name="MarkerColor"]');

            const icon = iconInput?.value || 'marker';
            const color = colorInput?.value || 'bg-blue';

            const iconUrl = buildPngIconUrl(icon, color);

            previewMarker = L.marker([lat, lon], {
                icon: L.icon({
                    iconUrl,
                    iconSize: [WF_WIDTH, WF_HEIGHT],
                    iconAnchor: WF_ANCHOR,
                    className: 'map-icon'
                })
            }).addTo(mapContainer);

            selectMarker(previewMarker);
        }
    };

    if (ctx.type === 'place' && ctx.action === 'set-location') {
        const formEl = document.querySelector(`#place-form-${ctx.id}`);
        if (!formEl) {
            console.warn(`⚠️ Cannot find form #place-form-${ctx.id} to apply coordinates`);
            return;
        }

        fill(`#place-form-${ctx.id}`, 'Latitude', 'Longitude');
    }
    if (ctx.type === 'region' && ctx.action === 'set-center')
        fill(`#region-form-${ctx.id}`, 'CenterLat', 'CenterLon');

    const coordsEl = document.getElementById('context-coords');
    if (coordsEl) {
        const latNum = parseFloat(lat);
        const lonNum = parseFloat(lon);

        if (!isNaN(latNum) && !isNaN(lonNum)) {
            coordsEl.innerHTML = `Coordinates (Lat, Lon): <code class="user-select-all">${latNum.toFixed(5)}, ${lonNum.toFixed(5)}</code>`;
            coordsEl.classList.remove('d-none');
        }
    }
};

/* ------------------------------------------------------------------ *
 *  REGION  – render / remove
 * ------------------------------------------------------------------ */
export const renderRegionMarker = async ({ Id, CenterLat, CenterLon, Name }) => {
    if (!CenterLat || !CenterLon) return;
    const lat = +CenterLat, lon = +CenterLon;
    if (isNaN(lat) || isNaN(lon)) return;

    if (regionMarkersById[Id]) mapContainer.removeLayer(regionMarkersById[Id]);

    const iconUrl = buildPngIconUrl('map', 'bg-red');
    const marker = L.marker([lat, lon], {
        icon: L.icon({
            iconUrl,
            iconSize: [WF_WIDTH, WF_HEIGHT],
            iconAnchor: WF_ANCHOR,
            className: 'map-icon'
        })
    }).addTo(mapContainer);

    marker.on('click', () => {
        selectedMarker?.getElement()?.classList.remove('selected-marker');
        selectedMarker = marker;
        selectMarker(marker);
        setMappingContext({
            type: 'region',
            id: Id,
            action: 'set-center',
            meta: { name: Name || 'Unnamed Region' }
        })
    });

    regionMarkersById[Id] = marker;
};

export const removeRegionMarker = (id) => {
    if (regionMarkersById[id]) {
        mapContainer.removeLayer(regionMarkersById[id]);
        delete regionMarkersById[id];
    }
};

/* ------------------------------------------------------------------ *
 *  PLACE – render / remove
 * ------------------------------------------------------------------ */
export const renderPlaceMarker = async (p) => {
    if (!p?.Latitude || !p?.Longitude) return;
    const lat = +p.Latitude, lon = +p.Longitude;
    if (isNaN(lat) || isNaN(lon)) return;

    if (placeMarkersById[p.Id]) mapContainer.removeLayer(placeMarkersById[p.Id]);

    const iconUrl = buildPngIconUrl(p.IconName || 'marker', p.MarkerColor || 'bg-blue');
    const marker = L.marker([lat, lon], {
        icon: L.icon({
            iconUrl,
            iconSize: [WF_WIDTH, WF_HEIGHT],
            iconAnchor: WF_ANCHOR,
            className: 'map-icon'
        })
    }).addTo(mapContainer);

    marker.on('click', () => {
        selectedMarker?.getElement()?.classList.remove('selected-marker');
        selectedMarker = marker;
        selectMarker(marker);
        setMappingContext({
            type: 'place', id: p.Id, action: 'set-location',
            meta: { name: p.Name, regionId: p.RegionId }
        })
    });

    placeMarkersById[p.Id] = marker;
};

export const removePlaceMarker = (id) => {
    if (placeMarkersById[id]) {
        mapContainer.removeLayer(placeMarkersById[id]);
        delete placeMarkersById[id];
    }
};

/* ------------------------------------------------------------------ *
 *  disableDrawingTools, initializeMap, getMapInstance – unchanged
 * ------------------------------------------------------------------ */
export const disableDrawingTools = () => {
    if (!mapContainer || !drawControl) return;
    try { mapContainer.removeControl(drawControl); } catch { }
    drawControl = null;
    drawnLayerGroup?.clearLayers();
};

export const initializeMap = (center = [20,0], zoom = 3) => {
    if (mapContainer) { mapContainer.off(); mapContainer.remove(); }
    mapContainer = L.map('mapContainer', { zoomAnimation:true }).setView(center, zoom);

    L.tileLayer(`${location.origin}/Public/tiles/{z}/{x}/{y}.png`, {
        maxZoom: 19, attribution: '© OpenStreetMap contributors'
    }).addTo(mapContainer);

    mapContainer.attributionControl.setPrefix(
        '&copy; <a href="https://leafletjs.com/">Leaflet</a>'
    );
    addZoomLevelControl(mapContainer);

    window.addEventListener('resize', () => mapContainer.invalidateSize());

    mapContainer.createPane('region-boundary');
    Object.assign(mapContainer.getPane('region-boundary').style, {
        zIndex:250, pointerEvents:'auto'
    });

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

// export const selectMarker = (marker) => {
//     clearSelectedMarker();
//     const el = marker.getElement?.();
//     if (el) {
//         el.classList.add('selected-marker');
//     } else {
//         requestAnimationFrame(() => selectMarker(marker)); // retry next frame
//         return;
//     }
//     selectedMarker = marker;
// };

export const selectMarker = (marker) => {
    clearSelectedMarker();

    const el = marker.getElement?.();
    if (!el || !(el instanceof HTMLImageElement)) {
        requestAnimationFrame(() => selectMarker(marker));
        return;
    }

    // ✅ Extract bg-* from the image src URL (e.g. ".../bg-purple/star.png")
    const src = el.getAttribute('src') || '';
    const match = src.match(/\/(bg-[a-z]+)\//i);
    const bgClass = match?.[1] || 'bg-blue';

    const colorMap = {
        'bg-black':  '#000000',
        'bg-purple': '#6f42c1',
        'bg-blue':   '#0d6efd',
        'bg-green':  '#198754',
        'bg-red':    '#dc3545',
    };

    const color = colorMap[bgClass] || '#0d6efd';
    el.style.setProperty('--selected-shadow-color', color);

    el.classList.add('selected-marker');
    selectedMarker = marker;
};

document.addEventListener('mapping-context-cleared', () => {
    // Remove region previews
    Object.values(regionPreviewById).forEach(m => mapContainer?.removeLayer(m));
    Object.keys(regionPreviewById).forEach(k => delete regionPreviewById[k]);

    // 🧼 Also remove selected marker from map
    try {
        clearSelectedMarker?.();
    } catch (err) {
        console.warn('⚠️ Failed to clear selected marker from mapManager', err);
    }
    // ✅ Remove preview marker
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
});



export const getMapInstance = () => mapContainer;
