import { addZoomLevelControl, latestLocationMarker, liveMarker } from '../../../map-utils.js';
import { embeddedMapOptions, installEmbeddedMap } from '../../../embeddedMap.js';
import { createTileLayer } from '../../../retryTileLayer.js';
import { message } from '../../../localization/messages.js';
import { encodeFeatureText } from '../../../util/feature-metadata.js';
import { installMapAttribution } from '../../../util/map-attribution.js';
import { isLiveLocation, scheduleLocationExpiry, getViewerTimeZone } from '../../../util/datetime.js';
import { initWikipediaPopovers } from '../../../util/wikipedia-utils.js';
import { renderTimelineLocation } from './timelineDetail.js';
import { formatTimelineSummary, readTimelineLocations } from './timelineFeedback.js';

let mapContainer = null;
let zoomLevel = 3;
let mapBounds = null;
let username = null;
let timelineLive;
let markerLayer, clusterLayer, highlightLayer;
let stream;
let embedded;
let locationsRequest;
let statsRequest;
let markerTransitionTimer = null; // Timer for live-to-latest marker transition
// permalink setup
const urlParams = new URLSearchParams(window.location.search);
const initialLat = parseFloat(urlParams.get('lat'));
const initialLng = parseFloat(urlParams.get('lng'));
const z = parseInt(urlParams.get('zoom'), 10);
zoomLevel = (!isNaN(z) && z >= 0) ? z : 3;
let initialCenter = (
    !isNaN(initialLat) && !isNaN(initialLng)
        ? [initialLat, initialLng]
        : [20, 0]
);

const viewerTimeZone = getViewerTimeZone();

document.addEventListener('DOMContentLoaded', () => {
    username = document.getElementById('username').dataset.username;
    embedded = Boolean(document.querySelector('[data-timeline-embed]'));
    const timelineLiveStr = document.getElementById('timelineLive').dataset.timelineLive;
    timelineLive = timelineLiveStr && timelineLiveStr.toLowerCase() === "true";

    try {
        stream = new EventSource(`/api/sse/stream/location-update/${username}`);
    } catch (e) {
        console.error(`Could not connect to stream ${e}`);
    }
    if (!username) {
        console.error('Username not found!');
        return;
    }

    // Initialize the mapContainer and load location data
    mapContainer = initializeMap();
    mapBounds = mapContainer.getBounds();
    getUserLocations();
    onZoomOrMoveChanges();

    // handle the SSE stream
    if (stream) stream.onmessage = refreshLiveTimeline;

    // Wire up the Bootstrap "shown" event for Wikipedia hover cards
    const modalEl = document.getElementById('locationModal');
    if (!modalEl) {
        console.error('Modal element not found!');
    } else {
        modalEl.addEventListener('shown.bs.modal', () => {
            initWikipediaPopovers(modalEl);
        });
    }

    // Cleanup timers and streams on page unload
    window.addEventListener('beforeunload', () => {
        if (markerTransitionTimer) {
            clearTimeout(markerTransitionTimer);
        }
        if (stream) {
            stream.close();
        }
    });

    getUserStats();

});

/** Refreshes an admitted live Timeline on the existing observation stream. */
const refreshLiveTimeline = () => {
    if (timelineLive) {
        getUserLocations();
        getUserStats();
    }
};

/** Initializes the cache-backed map and the controls for this layout. */
const initializeMap = () => {
    if (mapContainer !== undefined && mapContainer !== null) {
        mapContainer.off();
        mapContainer.remove();
    }
    mapContainer = L.map('mapContainer', {
        ...embeddedMapOptions(embedded),
        zoomControl: false,
        zoomAnimation: true
    }).setView(initialCenter, zoomLevel);
    if (embedded) installEmbeddedMap(mapContainer, `/Public/Users/Timeline/${encodeURIComponent(username)}`);
    L.control.zoom({
        zoomInTitle: message('Shell.ZoomIn', 'Zoom in'),
        zoomOutTitle: message('Shell.ZoomOut', 'Zoom out')
    }).addTo(mapContainer);
    createTileLayer().addTo(mapContainer);

    installMapAttribution(mapContainer);

    addZoomLevelControl(mapContainer);

    if (!highlightLayer) {
        highlightLayer = L.featureGroup();
    } else {
        highlightLayer.clearLayers();
    }
    highlightLayer.addTo(mapContainer);
    return mapContainer;
};

/** Keeps admitted live/latest markers above ordinary and clustered records. */
const buildLayers = (locations) => {
    if (highlightLayer) {
        highlightLayer.clearLayers();
    }

    // --- FLAT marker layer (no clustering) ---
    markerLayer = L.layerGroup();

    // --- CLUSTERED marker layer ---
    clusterLayer = L.markerClusterGroup({
        maxClusterRadius: 25,
        chunkedLoading:  true
    });


    // Find the backend-designated latest location and determine if it's currently live
    const latestLocation = locations.find(loc => loc.isLatestLocation);
    let highlightCandidate = null;
    if (latestLocation) {
        const isLive = isLiveLocation(latestLocation);
        highlightCandidate = { location: latestLocation, type: isLive ? 'live' : 'latest' };
    }
    const highlightId = highlightCandidate?.location?.id ?? null;

    const markerText = message('PublicTimeline.LocationMarker', 'Location record');
    const markerOptions = { title: markerText, alt: markerText };
    locations.forEach(location => {
        const coords = [location.coordinates.latitude, location.coordinates.longitude];
        const isHighlight = highlightId !== null && location.id === highlightId;

        const bindInteractions = (markerInstance) => {
            markerInstance.on('click', () => {
                const isLiveM = isLiveLocation(location);
                const isLatestM = location.isLatestLocation;

                document.getElementById('modalContent').innerHTML =
                    renderTimelineLocation(location, { isLive: isLiveM, isLatest: isLatestM }, viewerTimeZone);

                new bootstrap.Modal(document.getElementById('locationModal')).show();
            });
        };

        if (isHighlight && highlightLayer) {
            const icon = highlightCandidate?.type === 'live' ? liveMarker : latestLocationMarker;
            const highlightMarker = L.marker(coords, { ...markerOptions, icon });
            if (highlightCandidate?.type === 'latest') {
                highlightMarker.bindTooltip(encodeFeatureText(message("PublicTimeline.LatestTooltip", "User's latest location.")), {
                    direction: "top",
                    offset: [0, -25]
                });
            }
            bindInteractions(highlightMarker);
            highlightMarker.addTo(highlightLayer);
            highlightMarker.setZIndexOffset(1000);
        } else {
            const marker = L.marker(coords, markerOptions);
            bindInteractions(marker);

            if (markerLayer) markerLayer.addLayer(marker);
            if (clusterLayer) clusterLayer.addLayer(marker);
        }
    });

    // add only the appropriate layer
    if (mapContainer.getZoom() <= 5) {
        if (markerLayer) mapContainer.addLayer(markerLayer);
    } else {
        if (clusterLayer) mapContainer.addLayer(clusterLayer);
    }

    if (highlightLayer) {
        highlightLayer.addTo(mapContainer);
        highlightLayer.bringToFront(); // Ensure live/latest marker is always on top
    }

    // Schedule marker transition when live marker expires
    scheduleMarkerTransition(locations, highlightCandidate?.type === 'live' ? latestLocation : null);
};

/**
 * Schedules a timer to re-render markers when the current live marker should transition to latest.
 * Uses the observation actually rendered, including expiry during the render itself.
 * @param {Array} locations - Retained map data for the transition render.
 * @param {object|null} renderedLiveLocation - Observation actually drawn with the LIVE icon.
 */
const scheduleMarkerTransition = (locations, renderedLiveLocation) => {
    clearTimeout(markerTransitionTimer);
    // A point drawn live must transition even if it expires before this scheduler runs.
    markerTransitionTimer = renderedLiveLocation ? scheduleLocationExpiry(renderedLiveLocation, () => {
        displayLocationsOnMap(mapContainer, locations);
    }, true) : null;
};

// Display locations on the mapContainer with markers
const displayLocationsOnMap = (mapContainer, locations) => {
    if (!mapContainer) {
        mapContainer = initializeMap();
    }

    // remove any old layers
    if (markerLayer  && mapContainer.hasLayer(markerLayer))  {
        mapContainer.removeLayer(markerLayer);
    }
    if (clusterLayer && mapContainer.hasLayer(clusterLayer)) {
        mapContainer.removeLayer(clusterLayer);
    }

    // rebuild & add the right one
    buildLayers(locations);
};

/**
 * Track on map zoom and move changes
 */
const onZoomOrMoveChanges = () => {
    mapContainer.on("moveend zoomend", () => {
        let z = mapContainer.getZoom();
        if (z !== zoomLevel) {
            zoomLevel = z;
        }

        if (z <= 5) {
            if (clusterLayer && mapContainer.hasLayer(clusterLayer)) {
                mapContainer.removeLayer(clusterLayer);
            }
            if (markerLayer) {
                mapContainer.addLayer(markerLayer);
            }
        } else {
            if (markerLayer && mapContainer.hasLayer(markerLayer)) {
                mapContainer.removeLayer(markerLayer);
            }
            if (clusterLayer) {
                mapContainer.addLayer(clusterLayer);
            }
        }

        // Keep highlight layer on top after layer changes
        if (highlightLayer) {
            highlightLayer.bringToFront();
        }

        mapBounds = mapContainer.getBounds();
        zoomLevel = mapContainer.getZoom();
        debouncedGetUserLocations();
        updateUrlWithMapState();
    });
};

/**
 * Update URL based on user interaction with the map
 */
const updateUrlWithMapState = () => {
    const c = mapContainer.getCenter();
    const z = mapContainer.getZoom();
    const params = new URLSearchParams();
    params.set('lat', c.lat.toFixed(6));
    params.set('lng', c.lng.toFixed(6));
    params.set('zoom', z);

    const newUrl = `${window.location.pathname}?${params.toString()}`;
    if (zoomLevel !== 3 || c.lat.toFixed(6) !== '20.000000' || c.lng.toFixed(6) !== '0.000000') {
        history.replaceState(null, '', newUrl);
    }
};

// delay execution of a function (used on fetch to avoid excessive api calls)
const debounce = (func, delay) => {
    let debounceTimer;
    return function (...args) {
        clearTimeout(debounceTimer);
        debounceTimer = setTimeout(() => func.apply(this, args), delay);
    };
};

/** Loads the current viewport, discarding results superseded by a newer request. */
const getUserLocations = async () => {
    locationsRequest?.abort();
    locationsRequest = new AbortController();
    const { signal } = locationsRequest;
    const status = document.getElementById('timeline-map-status');
    const request = {
        minLongitude: mapBounds.getSouthWest().lng,
        minLatitude: mapBounds.getSouthWest().lat,
        maxLongitude: mapBounds.getNorthEast().lng,
        maxLatitude: mapBounds.getNorthEast().lat,
        username,
        zoomLevel
    };

    const updateStatus = text => {
        status.textContent = text;
        status.hidden = !text;
        if (embedded) mapContainer.invalidateSize({ pan: false });
    };

    try {
        const response = await fetch('/Public/Users/GetPublicTimeline', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
            body: JSON.stringify(request),
            signal
        });
        const feedback = await readTimelineLocations(response, locations => {
            if (signal.aborted) return;
            displayLocationsOnMap(mapContainer, locations);
            if (response.ok && !locations.length && mapContainer.getZoom() <= 2) {
                mapContainer.setView([20, 0], 2);
            }
        });
        if (!signal.aborted) updateStatus(feedback);
    } catch (error) {
        if (signal.aborted) return;
        console.error('Error fetching location data:', error);
        updateStatus(message('PublicTimeline.LocationsFailed', 'Could not load locations. Move the map to try again.'));
    }
};

const debouncedGetUserLocations = debounce(getUserLocations, 350);

/** Loads public aggregate history independently of viewport and surfaces only display messages. */
const getUserStats = async () => {
    statsRequest?.abort();
    statsRequest = new AbortController();
    const { signal } = statsRequest;
    const updateSummary = text => {
        document.getElementById('timeline-summary').textContent = text;
        if (embedded) mapContainer.invalidateSize({ pan: false });
    };

    try {
        const response = await fetch(`/Public/Users/GetPublicStats/${encodeURIComponent(username)}`, {
            headers: { Accept: 'application/json' },
            signal
        });
        // The viewport request renders the shared unavailable state for a public Timeline that disappears.
        if (response.status === 404) {
            if (!signal.aborted) updateSummary('');
            return;
        }
        if (!response.ok) throw new Error(`Timeline statistics response: ${response.status}`);
        const summary = formatTimelineSummary(await response.json(), viewerTimeZone);
        if (!signal.aborted) updateSummary(summary);
    } catch (error) {
        if (signal.aborted) return;
        console.error('Error fetching timeline statistics:', error);
        updateSummary(message('PublicTimeline.StatsFailed', 'Could not load timeline statistics.'));
    }
};
