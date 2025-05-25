// Custom control to display zoom level
const ZoomLevelControl = L.Control.extend({
    onAdd: function (map) {
        // Create a container element for the control
        const container = L.DomUtil.create('div', 'leaflet-bar leaflet-control leaflet-control-custom');
        container.style.backgroundColor = 'white';
        container.style.padding = '2px 4px';
        container.style.border = '1px solid #ccc';
        container.style.fontSize = '14px';

        container.innerHTML = `Zoom: ${map.getZoom()}`;


        // Update the zoom level text when zoom changes
        map.on('zoomend', function () {
            container.innerHTML = `Zoom: ${map.getZoom()}`;
        });

        return container;
    },

    onRemove: function (map) {
        // Nothing to clean up
    }
});

export const addZoomLevelControl = (map) => {
    const zoomLevelControl = new ZoomLevelControl({ position: 'topright' });
    map.addControl(zoomLevelControl);
};

export const latestLocationMarker = L.icon({
    iconUrl: '/icons/location-latest-green.svg',
    iconSize: [36, 36],
    iconAnchor: [18, 18],
    popupAnchor: [12, 12],
});
export const liveMarker = L.divIcon({
    className: 'pulsing-marker',
    iconSize: [36, 36],
    iconAnchor: [18, 18],
});

export const eatMarker = L.divIcon({
    iconUrl: '/lib/bootstrap-icons/bootstrap-icons-1.13.1/fork-knife.svg',
    iconAnchor: [18, 18],
    popupAnchor: [12, 12],
});

export const drinkMarker = L.divIcon({
    iconUrl: '/lib/bootstrap-icons/bootstrap-icons-1.13.1/cup-straw.svg',
    iconAnchor: [18, 18],
    popupAnchor: [12, 12],
});

export const cameraMarker = L.divIcon({
    iconUrl: '/lib/bootstrap-icons/bootstrap-icons-1.13.1/camera.svg',
    iconAnchor: [18, 18],
    popupAnchor: [12, 12],
});