// mapManager.js – refactored to use store
import {addZoomLevelControl} from '../../../map-utils.js';
import {store} from './storeInstance.js';

/* ------------------------------------------------------------------ *
 *  Private state
 * ------------------------------------------------------------------ */
const tilesUrl = `${window.location.origin}/Public/tiles/{z}/{x}/{y}.png`;
let mapContainer = null;
let drawControl = null;
let drawnLayerGroup = null;
let selectedMarker = null;
let previewMarker = null;
let previewMarkerType = null;

const placeMarkersById = {};
const regionMarkersById = {};
const regionPreviewById = {};
const areaPolygonsById = {};

const WF_WIDTH = 28;
const WF_HEIGHT = 45;
const WF_ANCHOR = [14, 45];

export const getRegionMarkerById = (id) => regionMarkersById[id] || null;
export const getPlaceMarkerById = (id) => placeMarkersById[id] || null;

export const clearPreviewMarker = () => {
    try {
        const marker = previewMarker;

        if (mapContainer && marker && typeof marker === 'object') {
            // Only call hasLayer/removeLayer if marker is not null
            if (mapContainer.hasLayer(marker)) {
                mapContainer.removeLayer(marker);
            }
        }
    } catch (err) {
        console.warn('⚠️ Failed to remove previewMarker in clearPreviewMarker', err);
    } finally {
        previewMarker = null;
        previewMarkerType = null;
    }
};

const buildPngIconUrl = (iconName, bgClass) =>
    `/icons/wayfarer-map-icons/dist/png/marker/${bgClass}/${iconName}.png`;

export const applyCoordinates = ({lat, lon}) => {
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
        if (ctx.type === 'region' && !isNaN(latNum) && !isNaN(lonNum)) {
            // 🔥 Remove existing region marker from DB
            if (regionMarkersById[ctx.id]) {
                mapContainer.removeLayer(regionMarkersById[ctx.id]);
                delete regionMarkersById[ctx.id];
            }

            if (previewMarker) {
                previewMarker.setLatLng([latNum, lonNum]);
            } else {
                const iconUrl = buildPngIconUrl('map', 'bg-red');
                previewMarker = L.marker([latNum, lonNum], {
                    icon: L.icon({
                        iconUrl,
                        iconSize: [WF_WIDTH, WF_HEIGHT],
                        iconAnchor: WF_ANCHOR,
                        className: 'map-icon'
                    })
                }).addTo(mapContainer);
            }

            previewMarkerType = 'region';
            selectMarker(previewMarker);
        }
    };

    if (ctx.type === 'place' && ['set-location', 'edit', 'create'].includes(ctx.action)) {
        fill(`#place-form-${ctx.id}`, 'Latitude', 'Longitude');
    }

    if (ctx.type === 'region' && ['set-center', 'edit'].includes(ctx.action)) {
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

/**
 * Initialize the mini-map for drawing/editing an Area,
 * centering on its parent Region and showing existing Places.
 *
 * @param {string} areaId       The GUID of the Area form.
 * @param {object|null} geometry  GeoJSON polygon or null.
 * @param {string} fillColor     Hex color for the new area.
 */
export const initAreaMap = (areaId, geometry, fillColor) => {
    // 1️⃣ Find the container
    const container = document.getElementById(`map-area-${areaId}`);
    if (!container) return;

    // 2️⃣ Create the Leaflet map
    const map = L.map(container, {zoomAnimation: true}).setView([0, 0], 2);
    L.tileLayer(tilesUrl, {
        attribution: '&copy; OpenStreetMap contributors'
    }).addTo(map);

    map.attributionControl.setPrefix('&copy; <a href="https://wayfarer.stefk.me" title="Powered by Wayfarer, made by Stef" target="_blank">Wayfarer</a> | <a href="https://stefk.me" title="Check my blog" target="_blank">Stef K</a> | &copy; <a href="https://leafletjs.com/" target="_blank">Leaflet</a>');

    // 3️⃣ Draw existing Places for context
    const contextGroup = new L.FeatureGroup().addTo(map);
    const regionItem = container.closest('.accordion-item[id^="region-item-"]');
    let usedRegionCenter = false;

    if (regionItem) {
        // 3a) Center on the Region if available
        const lat = parseFloat(regionItem.dataset.centerLat);
        const lon = parseFloat(regionItem.dataset.centerLon);
        if (!isNaN(lat) && !isNaN(lon)) {
            map.setView([lat, lon], 8);
            usedRegionCenter = true;
        }

        // 3b) Render each Place marker in that region
        regionItem.querySelectorAll('.place-list-item').forEach(el => {
            const plat = parseFloat(el.dataset.placeLat);
            const plon = parseFloat(el.dataset.placeLon);
            if (isNaN(plat) || isNaN(plon)) return;

            const iconName = el.dataset.placeIcon || 'marker';
            const color = el.dataset.placeColor || 'bg-blue';
            const iconUrl = buildPngIconUrl(iconName, color);

            L.marker([plat, plon], {
                icon: L.icon({
                    iconUrl,
                    iconSize: [WF_WIDTH, WF_HEIGHT],
                    iconAnchor: WF_ANCHOR,
                    className: 'map-icon'
                })
            }).addTo(contextGroup);
        });
    }

    // 3c) If we didn’t center on the Region, but we *did* add Place markers,
    //     auto-zoom to fit them.
    if (!usedRegionCenter && contextGroup.getLayers().length > 0) {
        map.fitBounds(contextGroup.getBounds(), {padding: [20, 20]});
    }

    // 4️⃣ Draw any existing Area polygon (for edit mode)
    const drawnItems = new L.FeatureGroup().addTo(map);
    if (geometry) {
        // load GeoJSON, then take each polygon layer and add it directly
        const geoLayer = L.geoJSON(geometry, {
            style: () => ({ color: fillColor, fillColor, fillOpacity: 0.4 })
        });

        // move each real layer into our drawnItems group
        geoLayer.eachLayer(layer => drawnItems.addLayer(layer));

        // zoom to the bounds of the actual polygon(s)
        const bounds = drawnItems.getBounds();
        if (bounds.isValid()) {
            map.fitBounds(bounds);
        }
    }

    // 5️⃣ Add the polygon-only Draw toolbar
    drawControl = new L.Control.Draw({
        draw: {
            polygon: {allowIntersection: false, showArea: true},
            rectangle: false,
            circle: false,
            polyline: false,
            marker: false,
            circlemarker: false
        },
        edit: {featureGroup: drawnItems}
    }).addTo(map);

    // 6️⃣ Wire up Create/Edit events to write GeoJSON back to the form
    map.on(L.Draw.Event.CREATED, e => {
        drawnItems.clearLayers();
        drawnItems.addLayer(e.layer);
        document
            .getElementById(`Geometry-${areaId}`)
            .value = JSON.stringify(e.layer.toGeoJSON().geometry);

        const saveBtn = document.querySelector(
            `#area-form-${areaId} .btn-area-save`
        );
        if (saveBtn) saveBtn.click();
    });
    map.on(L.Draw.Event.EDITED, e => {
        e.layers.eachLayer(layer => {
            document
                .getElementById(`Geometry-${areaId}`)
                .value = JSON.stringify(layer.toGeoJSON().geometry);
        });
    });
};


// draw a polygon on the **main** map
export const renderAreaPolygon = ({Id, Geometry, FillHex}) => {
    if (!Geometry) return;
    // Remove stale
    if (areaPolygonsById[Id]) {
        mapContainer.removeLayer(areaPolygonsById[Id]);
        delete areaPolygonsById[Id];
    }
    const poly = L.geoJSON(Geometry, {
        style: () => ({color: FillHex, fillColor: FillHex, fillOpacity: 0.3, weight: 2})
    }).addTo(mapContainer);
    areaPolygonsById[Id] = poly;
};

// remove from main map
export const removeAreaPolygon = (id) => {
    if (!areaPolygonsById[id]) return;
    mapContainer.removeLayer(areaPolygonsById[id]);
    delete areaPolygonsById[id];
};


export const renderRegionMarker = async ({Id, CenterLat, CenterLon, Name}) => {
    if (!CenterLat || !CenterLon) return;
    const lat = +CenterLat, lon = +CenterLon;
    if (isNaN(lat) || isNaN(lon)) return;

    if (regionMarkersById[Id]) {
        console.debug('🔥 Removing stale region marker before redraw:', Id);
        mapContainer.removeLayer(regionMarkersById[Id]);
        delete regionMarkersById[Id];
    }

    const iconUrl = buildPngIconUrl('map', 'bg-red');
    console.debug('🧷 Adding region marker:', Id, lat, lon);
    const marker = L.marker([lat, lon], {
        icon: L.icon({iconUrl, iconSize: [WF_WIDTH, WF_HEIGHT], iconAnchor: WF_ANCHOR, className: 'map-icon'})
    }).addTo(mapContainer);

    marker.on('click', () => {
        clearSelectedMarker();
        selectMarker(marker);
        store.dispatch('set-context', {
            type: 'region', id: Id, action: 'set-center', meta: {name: Name || 'Unnamed Region'}
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

    removePlaceMarker(p.Id); // 🔥 remove any stale marker

    const iconUrl = buildPngIconUrl(p.IconName || 'marker', p.MarkerColor || 'bg-blue');
    const marker = L.marker([lat, lon], {
        icon: L.icon({iconUrl, iconSize: [WF_WIDTH, WF_HEIGHT], iconAnchor: WF_ANCHOR, className: 'map-icon'})
    }).addTo(mapContainer);

    marker.on('click', () => {
        clearSelectedMarker();
        selectMarker(marker);
        store.dispatch('set-context', {
            type: 'place', id: p.Id, action: 'set-location', meta: {name: p.Name, regionId: p.RegionId}
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
    try {
        mapContainer.removeControl(drawControl);
    } catch {
    }
    drawControl = null;
    drawnLayerGroup?.clearLayers();
};

export const initializeMap = (center = [20, 0], zoom = 3) => {
    if (mapContainer) {
        mapContainer.off();
        mapContainer.remove();
    }
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

store.subscribe(({type, payload}) => {
    if (type === 'set-context' && payload?.type === 'search-temp') {
        const {lat, lon, name} = payload.meta || {};
        console.debug('[mapManager] SET preview marker at', lat, lon);
        if (!lat || !lon) return;

        const iconUrl = '/icons/wayfarer-map-icons/dist/png/marker/bg-black/map.png';
        clearPreviewMarker();

        previewMarker = L.marker([lat, lon], {
            icon: L.icon({
                iconUrl,
                iconSize: [24, 41],
                iconAnchor: [12, 41],
                className: 'map-icon'
            }),
            title: name || 'Temporary place'
        }).addTo(mapContainer);
        previewMarkerType = 'search-temp';
        clearSelectedMarker();
        selectMarker(previewMarker);
        applyCoordinates({lat, lon});
    }

    if (type === 'context-cleared') {
        Object.values(regionPreviewById).forEach(m => mapContainer?.removeLayer(m));
        Object.keys(regionPreviewById).forEach(k => delete regionPreviewById[k]);

        try {
            clearSelectedMarker?.();
        } catch (err) {
            console.warn('⚠️ Failed to clear selected marker from mapManager', err);
        }

        try {
            clearPreviewMarker();
        } catch (err) {
            console.warn('⚠️ Failed to clear preview marker', err);
        }

        const coordsEl = document.getElementById('context-coords');
        if (coordsEl) {
            coordsEl.classList.add('d-none');
            coordsEl.innerHTML = '';
        }
    }

    if (type === 'region-dom-reloaded' && payload?.regionId) {
        if (previewMarker) {
            mapContainer?.removeLayer(previewMarker);
            previewMarker = null;
        }
        const regionEl = document.getElementById(`region-item-${payload.regionId}`);
        if (!regionEl) return;

        const placeEls = regionEl.querySelectorAll('.place-list-item');
        for (const markerId in placeMarkersById) {
            const el = document.querySelector(`.place-list-item[data-place-id="${markerId}"]`);
            if (el?.closest(`#region-item-${payload.regionId}`)) {
                removePlaceMarker(markerId);
            }
        }
        for (const el of placeEls) {
            const placeId = el.dataset.placeId;
            const lat = parseFloat(el.dataset.placeLat);
            const lon = parseFloat(el.dataset.placeLon);
            const icon = el.dataset.placeIcon;
            const color = el.dataset.placeColor;
            const name = el.dataset.placeName;

            if (!isNaN(lat) && !isNaN(lon)) {
                renderPlaceMarker({
                    Id: placeId,
                    Name: name,
                    Latitude: lat,
                    Longitude: lon,
                    IconName: icon,
                    MarkerColor: color,
                    RegionId: payload.regionId
                });
            }
        }
        const lat = parseFloat(regionEl.dataset.centerLat);
        const lon = parseFloat(regionEl.dataset.centerLon);
        const name = regionEl.dataset.regionName || 'Unnamed Region';
        if (!isNaN(lat) && !isNaN(lon)) {
            renderRegionMarker({
                Id: payload.regionId,
                CenterLat: lat,
                CenterLon: lon,
                Name: name
            });
        }

        regionEl.querySelectorAll('.area-list-item').forEach(el => {
            const geom = JSON.parse(el.dataset.areaGeom || 'null');
            const fill = el.dataset.areaFill;
            renderAreaPolygon({Id: el.dataset.areaId, Geometry: geom, FillHex: fill});
        });
    }
});

export const getMapInstance = () => mapContainer;
