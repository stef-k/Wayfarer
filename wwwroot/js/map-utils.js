import { registerLanguageDraft } from './localization/languageNavigation.js';
import { message, number } from './localization/messages.js';
import { formatMapDistance, measureInstructions } from './localization/mapMeasurementText.js';

/** Copies the invariant permalink, retaining the older clipboard fallback when needed. */
const copyMapLink = async url => {
    if (navigator.clipboard?.writeText) {
        await navigator.clipboard.writeText(url);
        return;
    }

    const input = document.createElement('input');
    input.value = url;
    document.body.appendChild(input);
    try {
        input.select();
        if (!document.execCommand('copy')) throw new Error('Clipboard copy was rejected');
    } finally {
        input.remove();
    }
};

// Custom control to display zoom level and copy permalink with theme support
// import * as turf from '@turf/turf';
const ZoomLevelControl = L.Control.extend({
    onAdd: function (map) {
        // Create a container element for the control (styled via CSS in site.css)
        const container = L.DomUtil.create('div', 'leaflet-bar leaflet-control leaflet-control-custom');

        // Create zoom text
        const zoomText = L.DomUtil.create('span', '', container);
        zoomText.textContent = message('Shell.MapZoomLevel', 'Zoom: {0}', number(map.getZoom()));

        // Ruler (measure) button
        const measureBtn = L.DomUtil.create('button', '', container);
        measureBtn.title = message('Shell.MeasureDistance', 'Measure distance');
        measureBtn.setAttribute('aria-label', measureBtn.title);
        measureBtn.innerHTML = `<img src="/lib/bootstrap-icons/bootstrap-icons-1.13.2/rulers.svg" alt="" style="width:1.2em; height:1.2em;" />`;

        let measureActive = false;
        let currentTool = null;
        // Language navigation queries the same owner that controls the ruler and its drawn layers.
        if (this.options.protectLanguage) this.releaseDraft = registerLanguageDraft(() => measureActive);

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
        copyBtn.title = message('Shell.CopyMapLink', 'Copy map link');
        copyBtn.setAttribute('aria-label', copyBtn.title);
        copyBtn.innerHTML = `<img src="/lib/bootstrap-icons/bootstrap-icons-1.13.2/link-45deg.svg" alt="" style="width:1.2em; height:1.2em;" />`;

        // Update zoom text on zoom changes
        map.on('zoomend', () => {
            zoomText.textContent = message('Shell.MapZoomLevel', 'Zoom: {0}', number(map.getZoom()));
        });

        const copyStatus = L.DomUtil.create('span', 'visually-hidden', container);
        copyStatus.setAttribute('role', 'status');
        let resetCopy;
        // Announce clipboard feedback in both layouts, including embeds without a toast region.
        L.DomEvent.on(copyBtn, 'click', async (event) => {
            L.DomEvent.stopPropagation(event);
            const showFeedback = (text, icon) => {
                clearTimeout(resetCopy);
                copyStatus.textContent = text;
                copyBtn.title = text;
                copyBtn.setAttribute('aria-label', text);
                copyBtn.querySelector('img').src = `/lib/bootstrap-icons/bootstrap-icons-1.13.2/${icon}.svg`;
                resetCopy = setTimeout(() => {
                    copyStatus.textContent = '';
                    copyBtn.title = message('Shell.CopyMapLink', 'Copy map link');
                    copyBtn.setAttribute('aria-label', copyBtn.title);
                    copyBtn.querySelector('img').src = '/lib/bootstrap-icons/bootstrap-icons-1.13.2/link-45deg.svg';
                }, 1500);
            };

            try {
                await copyMapLink(window.location.href);
                showFeedback(message('Shell.MapLinkCopied', 'Map link copied'), 'check');
            } catch (error) {
                console.error('Copy failed', error);
                showFeedback(message('Shell.MapLinkCopyFailed', 'Could not copy the map link.'), 'exclamation-circle');
            }
        });

        return container;
    },

    onRemove: function () {
        this.releaseDraft?.();
    }
});

/** Mounts the shared map control with draft awareness on integrated history routes. */
export const addZoomLevelControl = (map, { protectLanguage = false } = {}) => {
    const zoomLevelControl = new ZoomLevelControl({ position: 'topright', protectLanguage });
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

/** Owns the temporary ruler layers and renders localized guidance and distance labels. */
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
        const badge = document.createElement('span');
        badge.className = 'badge bg-dark';
        badge.textContent = formatMapDistance(km);
        const label = L.marker(last, {
            icon: L.divIcon({
                className: 'distance-label',
                html: badge,
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
        const [addPoints, cancel] = measureInstructions();
        div.append('🖱 ', addPoints, document.createElement('br'), '⎋ ', cancel);
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
