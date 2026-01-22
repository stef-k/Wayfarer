// Custom control to display zoom level and copy permalink with theme support
// import * as turf from '@turf/turf';
const ZoomLevelControl = L.Control.extend({
    onAdd: function (map) {
        // Create a container element for the control (styled via CSS in site.css)
        const container = L.DomUtil.create('div', 'leaflet-bar leaflet-control leaflet-control-custom');

        // Create zoom text
        const zoomText = L.DomUtil.create('span', '', container);
        zoomText.textContent = `Zoom: ${map.getZoom()}`;

        // Ruler (measure) button
        const measureBtn = L.DomUtil.create('button', '', container);
        measureBtn.title = 'Measure distance';
        measureBtn.innerHTML = `<img src="/lib/bootstrap-icons/bootstrap-icons-1.13.1/rulers.svg" alt="Measure" style="width:1.2em; height:1.2em;" />`;

        let measureActive = false;
        let currentTool = null;

        L.DomEvent.on(measureBtn, 'click', (e) => {
            L.DomEvent.stopPropagation(e);

            if (measureActive) {
                currentTool?.cancel?.();
                currentTool = null;
                measureActive = false;
                measureBtn.classList.remove('active');
                try { map._container.style.cursor = ''; } catch (_) {}
                return;
            }

            measureActive = true;
            measureBtn.classList.add('active');

            currentTool = initDistanceMeasureTool(map, {
                onFinish: ({ km }) => {
                    // console.log(`📏 Final distance: ${km} km`);
                },
                onCancel: () => {
                    measureActive = false;
                    measureBtn.classList.remove('active');
                    currentTool = null;
                    try { map._container.style.cursor = ''; } catch (_) {}
                }
            });
            try { map._container.style.cursor = 'pointer'; } catch (_) {}
        });

        // Create copy button using Bootstrap Icon
        const copyBtn = L.DomUtil.create('button', '', container);
        copyBtn.title = 'Copy map link';
        copyBtn.innerHTML = `<img src="/lib/bootstrap-icons/bootstrap-icons-1.13.1/link-45deg.svg" alt="Copy link" style="width:1.2em; height:1.2em;" />`;

        // Update zoom text on zoom changes
        map.on('zoomend', () => {
            zoomText.textContent = `Zoom: ${map.getZoom()}`;
        });

        // Copy current URL to clipboard on button click
        L.DomEvent.on(copyBtn, 'click', (e) => {
            L.DomEvent.stopPropagation(e);
            const url = window.location.href;
            const showSuccess = () => {
                copyBtn.innerHTML = `<img src="/lib/bootstrap-icons/bootstrap-icons-1.13.1/check.svg" alt="Copied" style="width:1.2em; height:1.2em;" />`;
                setTimeout(() => {
                    copyBtn.innerHTML = `<img src="/lib/bootstrap-icons/bootstrap-icons-1.13.1/link-45deg.svg" alt="Copy link" style="width:1.2em; height:1.2em;" />`;
                }, 1500);
            };

            if (navigator.clipboard && navigator.clipboard.writeText) {
                navigator.clipboard.writeText(url)
                    .then(showSuccess)
                    .catch(err => console.error('Copy failed', err));
            } else {
                // Fallback for older browsers
                const input = document.createElement('input');
                document.body.appendChild(input);
                input.value = url;
                input.select();
                document.execCommand('copy');
                document.body.removeChild(input);
                showSuccess();
            }
        });

        return container;
    },

    onRemove: function (map) {
        // No cleanup needed - CSS handles theming
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

export const initDistanceMeasureTool = (map, options   = {}) => {
    const { onFinish, onCancel } = options;
    const points = [];
    const pointMarkers = [];
    const labelMarkers = [];
    let polyline = null;
    let helpControl = null;

    const turfLineString = turf.lineString;
    const turfLength = turf.length;

    const reset = () => {
        points.length = 0;

        pointMarkers.forEach(m => map.removeLayer(m));
        pointMarkers.length = 0;

        labelMarkers.forEach(m => map.removeLayer(m));
        labelMarkers.length = 0;

        if (polyline) {
            map.removeLayer(polyline);
            polyline = null;
        }

        if (helpControl) {
            map.removeControl(helpControl);
            helpControl = null;
        }
    };

    const cancelTool = () => {
        reset();
        map.off('click', onMapClick);
        window.removeEventListener('keydown', onKey);
        window.__distanceToolContext = null;

        if (typeof onCancel === 'function') {
            onCancel();
        }
    };

    const finish = () => {
        if (points.length < 2) return;

        const coords = points.map(p => [p.lng, p.lat]);
        const line = turfLineString(coords);
        const km = turfLength(line, { units: 'kilometers' });

        if (typeof onFinish === 'function') {
            onFinish({ km: +km.toFixed(2), geojson: line });
        }

        const last = points.at(-1);
        const label = L.marker(last, {
            icon: L.divIcon({
                className: 'distance-label',
                html: `<span class="badge bg-dark">${km.toFixed(2)} km</span>`,
                iconSize: [60, 24],
                iconAnchor: [30, 12]
            })
        }).addTo(map);
        labelMarkers.push(label);
    };

    const onMapClick = (e) => {
        points.push(e.latlng);

        const marker = L.circleMarker(e.latlng, {
            radius: 4,
            color: '#0d6efd',
            fillColor: '#0d6efd',
            fillOpacity: 0.9
        }).addTo(map);
        pointMarkers.push(marker);

        if (!polyline) {
            polyline = L.polyline(points, {
                color: '#0d6efd',
                weight: 3,
                dashArray: '5, 5'
            }).addTo(map);
        } else {
            polyline.setLatLngs(points);
        }

        if (points.length > 1) finish();
    };

    let onKey = (e) => {
        if (e.key === 'Escape') {
            cancelTool();
        }
    };

    map.on('click', onMapClick);
    window.addEventListener('keydown', onKey);

    helpControl = L.control({ position: 'bottomleft' });
    helpControl.onAdd = () => {
        const div = L.DomUtil.create('div', 'leaflet-bar help-popup');
        div.innerHTML = '🖱 Click to add points<br>⎋ ESC to cancel';
        return div;
    };
    helpControl.addTo(map);

    // Ensure any previous tool is cancelled
    if (window.__distanceToolContext?.cancel) {
        window.__distanceToolContext.cancel();
    }
    window.__distanceToolContext = { cancel: cancelTool };

    return {
        cancel: cancelTool
    };
};

export const calculateLineDistanceKm = (coords) => {
    if (!coords || coords.length < 2) return 0;
    const line = turf.lineString(coords);
    return turf.length(line, { units: 'kilometers' });
};
