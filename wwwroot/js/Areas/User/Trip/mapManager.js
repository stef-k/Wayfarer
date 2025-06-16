// mapManager.js
import { addZoomLevelControl } from '../../../map-utils.js';
import { clearMappingContext, setMappingContext  } from './mappingContext.js';

let mapContainer = null;
let drawControl = null;
let drawnLayerGroup = null;
const placeMarkersById = {};

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


/**
 * Renders a place marker on the map using icon name and color.
 * @param {object} place - The place object.
 *  Requires: Id, Latitude, Longitude, IconName, MarkerColor, Name, RegionId
 */
export const renderPlaceMarker = (place) => {
    if (!place?.Latitude || !place?.Longitude) return;

    const lat = parseFloat(place.Latitude);
    const lon = parseFloat(place.Longitude);

    // Remove existing if exists
    if (placeMarkersById[place.Id]) {
        map.removeLayer(placeMarkersById[place.Id]);
    }

    const iconUrl = `/icons/wayfarer-map-icons/dist/marker/${place.IconName || 'flag'}.svg`;

    const marker = L.marker([lat, lon], {
        icon: L.icon({
            iconUrl,
            iconSize: [24, 24],
            iconAnchor: [12, 24],
            className: `map-icon ${place.MarkerColor || 'bg-soft-blue'} color-white`
        })
    });
    
    marker.on('click', () => {
        setMappingContext({
            type: 'place',
            id: place.Id,
            action: 'set-location',
            meta: { name: place.Name, regionId: place.RegionId }
        });
    });

    marker.addTo(map);
    placeMarkersById[place.Id] = marker;
};

export const removePlaceMarker = (placeId) => {
    if (placeMarkersById[placeId]) {
        map.removeLayer(placeMarkersById[placeId]);
        delete placeMarkersById[placeId];
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

    mapContainer.createPane('region-boundary');
    mapContainer.getPane('region-boundary').style.zIndex = 250; // below markers
    mapContainer.getPane('region-boundary').style.pointerEvents = 'auto'; // allow clicks to pass through
    
    return mapContainer;
};

export const getMapInstance = () => mapContainer;
