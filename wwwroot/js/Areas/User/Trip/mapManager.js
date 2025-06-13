// mapManager.js
import { addZoomLevelControl } from '../../../map-utils.js';
import { clearMappingContext } from './mappingContext.js';

let mapContainer = null;
let drawControl = null;
let drawnLayerGroup = null;
let regionBoundaryLayer = null;
let regionBoundaryLayers = {}; // { regionId: layer }

export const setupDrawingTools = (regionId) => {
    const map = getMapInstance();
    if (!map) return;

    // Remove existing controls/layers
    if (drawControl) {
        map.removeControl(drawControl);
        drawnLayerGroup.clearLayers();
    }

    if (!drawnLayerGroup) {
        drawnLayerGroup = new L.FeatureGroup();
        map.addLayer(drawnLayerGroup);
    }

    // Load existing polygon for editing
    const existing = document.getElementById(`region-boundary-${regionId}`);
    if (existing?.value) {
        try {
            const geojson = JSON.parse(existing.value);
            const layer = L.geoJSON(geojson).getLayers()[0];
            if (layer) drawnLayerGroup.addLayer(layer);
        } catch (e) {
            console.warn('Could not parse existing polygon for editing');
        }
    }

    // Add draw/edit controls
    drawControl = new L.Control.Draw({
        draw: {
            polyline: false,
            rectangle: false,
            circle: false,
            marker: false,
            circlemarker: false,
            polygon: {
                allowIntersection: false,
                showArea: true,
                shapeOptions: {
                    color: '#3388ff',
                    weight: 3
                }
            }
        },
        edit: {
            featureGroup: drawnLayerGroup,
            edit: true,
            remove: true
        }
    });

    map.addControl(drawControl);

    // Handle save after user clicks "Finish"
    map.once(L.Draw.Event.CREATED, async (e) => {
        drawnLayerGroup.clearLayers(); // Replace with new
        drawnLayerGroup.addLayer(e.layer);
        await saveRegionBoundary(e.layer, regionId);
    });

    // Handle save after user finishes edit
    map.on(L.Draw.Event.EDITED, async (e) => {
        const layer = e.layers.getLayers()[0];
        if (!layer) return;

        await saveRegionBoundary(layer, regionId);
    });

    // Handle delete
    map.on(L.Draw.Event.DELETED, async () => {
        const resp = await fetch(`/User/Regions/SaveBoundary?regionId=${regionId}`, {
            method: 'POST',
            body: '', // empty body = delete
            headers: {
                'Content-Type': 'application/json',
                'X-CSRF-TOKEN': document.querySelector('input[name="__RequestVerificationToken"]').value
            }
        });

        if (resp.ok) {
            showAlert('success', 'Region boundary deleted.');
            clearMappingContext();
            document.dispatchEvent(new CustomEvent('boundary-saved', { detail: { regionId } }));
        } else {
            showAlert('danger', 'Failed to delete region boundary.');
        }

        disableDrawingTools();
    });
};

const saveRegionBoundary = async (layer, regionId) => {
    const geojson = layer.toGeoJSON();
    const resp = await fetch(`/User/Regions/SaveBoundary?regionId=${regionId}`, {
        method: 'POST',
        body: JSON.stringify(geojson),
        headers: {
            'Content-Type': 'application/json',
            'X-CSRF-TOKEN': document.querySelector('input[name="__RequestVerificationToken"]').value
        }
    });

    if (resp.ok) {
        showAlert('success', 'Region boundary saved!');
        clearMappingContext();
        document.dispatchEvent(new CustomEvent('boundary-saved', { detail: { regionId } }));
    } else {
        showAlert('danger', 'Failed to save region boundary.');
    }

    disableDrawingTools();
};

export const renderRegionBoundary = (polygonGeoJson, regionId = null) => {
    const map = getMapInstance();
    if (!map || !polygonGeoJson) return;

    // Optional: parse regionId from geoJson properties if not explicitly passed
    if (!regionId && polygonGeoJson?.properties?.regionId) {
        regionId = polygonGeoJson.properties.regionId;
    }

    if (!regionId) {
        console.warn('Region ID missing when rendering boundary.');
        return;
    }

    // Remove existing layer for region if exists
    if (regionBoundaryLayers[regionId]) {
        map.removeLayer(regionBoundaryLayers[regionId]);
    }

    const layer = L.geoJSON(polygonGeoJson, {
        style: {
            color: '#007bff',
            weight: 2,
            fillColor: '#cce5ff',
            fillOpacity: 0.3
        }
    });

    layer.addTo(map);
    regionBoundaryLayers[regionId] = layer;
};

export const disableDrawingTools = () => {
    const map = getMapInstance();
    if (!map || !drawControl) return;

    try {
        map.removeControl(drawControl);
    } catch (e) {
        console.warn('Draw control already removed or failed:', e);
    }

    drawControl = null;

    if (drawnLayerGroup) {
        drawnLayerGroup.clearLayers();
    }
};

export const initializeMap = (initialCenter = [20, 0], zoomLevel = 3) => {
    if (mapContainer) {
        mapContainer.off();
        mapContainer.remove();
    }

    const tilesUrl = `${window.location.origin}/Public/tiles/{z}/{x}/{y}.png`;

    mapContainer = L.map('mapContainer', {
        zoomAnimation: true
    }).setView(initialCenter, zoomLevel);

    L.tileLayer(tilesUrl, {
        maxZoom: 19,
        attribution: '© OpenStreetMap contributors'
    }).addTo(mapContainer);

    mapContainer.attributionControl.setPrefix('&copy; <a href="https://leafletjs.com/" target="_blank">Leaflet</a>');
    addZoomLevelControl(mapContainer);

    window.addEventListener('resize', () => {
        mapContainer.invalidateSize();
    });

    return mapContainer;
};

export const removeRegionBoundaryFromMap = (regionId = null) => {
    const map = getMapInstance();
    if (!map) return;

    if (regionId && regionBoundaryLayers[regionId]) {
        map.removeLayer(regionBoundaryLayers[regionId]);
        delete regionBoundaryLayers[regionId];
    } else if (!regionId) {
        // fallback: clear all
        Object.values(regionBoundaryLayers).forEach(layer => map.removeLayer(layer));
        regionBoundaryLayers = {};
    }
};

export const getMapInstance = () => mapContainer;
