let locations = []; // Declare locations as a global variable
let mapContainer = null;
let zoomLevel = 3;
let mapBounds = null;
let markerClusterGroup = null;
let username = null;
let markerLayer, clusterLayer, highlightLayer;
// Map tiles config (proxy URL + attribution) injected by layout.
const tilesConfig = window.wayfarerTileConfig || {};
const tilesUrl = tilesConfig.tilesUrl || `${window.location.origin}/Public/tiles/{z}/{x}/{y}.png`;
const tilesAttribution = tilesConfig.attribution || '&copy; OpenStreetMap contributors';
let timelineLive;
let stream;
let markerTransitionTimer = null; // Timer for live-to-latest marker transition

// permalink setup
const urlParams = new URLSearchParams(window.location.search);
const initialLat = parseFloat(urlParams.get('lat'));
const initialLng = parseFloat(urlParams.get('lng'));
const initialZoom = parseInt(urlParams.get('zoom'), 10);
const z = parseInt(urlParams.get('zoom'), 10);
zoomLevel = (!isNaN(z) && z >= 0) ? z : 3;
let initialCenter = (
    !isNaN(initialLat) && !isNaN(initialLng)
        ? [initialLat, initialLng]
        : [20, 0]
);

const viewerTimeZone = getViewerTimeZone();
const getLocationSourceTimeZone = location => location?.timezone || location?.timeZoneId || location?.timeZone || null;
const getLocationTimestampInfo = location => formatViewerAndSourceTimes({
    iso: location?.localTimestamp,
    sourceTimeZone: getLocationSourceTimeZone(location),
    viewerTimeZone,
});

import {addZoomLevelControl, latestLocationMarker, liveMarker} from '../../../map-utils.js';
import {
    formatViewerAndSourceTimes,
    formatDate,
    formatDecimal,
    getViewerTimeZone,
} from '../../../util/datetime.js';
import {
    generateWikipediaLinkHtml,
    initWikipediaPopovers,
} from '../../../util/wikipedia-utils.js';

document.addEventListener('DOMContentLoaded', () => {
    
    fixParentPadding();
    
    username = document.getElementById('username').dataset.username;
    const timelineLiveStr = document.getElementById('timelineLive').dataset.timelineLive;
    timelineLive = timelineLiveStr && timelineLiveStr.toLowerCase() === "true";
    
    try {
        stream = new EventSource(`/api/sse/stream/location-update/${username}`);
    }  catch (e) {
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
    stream.onmessage = (event) => {
        handleStream(event);
    }
    
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

    getUserStats(username);

});

const handleStream = (event) => {
    if(timelineLive) {
        getUserLocations();
        getUserStats();
    }
};

/**
 * Sets up Map, Table, and Pagination and may be
 * used for initial or after data updates.
 */

// Initialize mapContainer with the cache proxy tile layer.
const initializeMap = () => {
    if (mapContainer !== undefined && mapContainer !== null) {
        mapContainer.off();
        mapContainer.remove();
    }
    mapContainer = L.map('mapContainer', {
        scrollWheelZoom: false,
        zoomAnimation: true
    }).setView(initialCenter, zoomLevel);
    L.tileLayer(tilesUrl, {
        maxZoom: 19,
        attribution: tilesAttribution
    }).addTo(mapContainer);

    mapContainer.attributionControl.setPrefix('&copy; <a href="https://wayfarer.stefk.me" title="Powered by Wayfarer, made by Stef" target="_blank">Wayfarer</a> | <a href="https://stefk.me" title="Check my blog" target="_blank">Stef K</a> | &copy; <a href="https://leafletjs.com/" target="_blank">Leaflet</a>');

    addZoomLevelControl(mapContainer);

    if (!highlightLayer) {
        highlightLayer = L.featureGroup();
    } else {
        highlightLayer.clearLayers();
    }
    highlightLayer.addTo(mapContainer);
    return mapContainer;
};

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

    const nowMinGlobal = Math.floor(Date.now() / 60000);
    const thresholdFor = (location) => location.locationTimeThresholdMinutes ?? 10;

    // Find the backend-designated latest location and determine if it's currently live
    const latestLocation = locations.find(loc => loc.isLatestLocation);
    let highlightCandidate = null;
    if (latestLocation) {
        const locMin = Math.floor(new Date(latestLocation.localTimestamp).getTime() / 60000);
        const isLive = (nowMinGlobal - locMin) <= thresholdFor(latestLocation);
        highlightCandidate = { location: latestLocation, type: isLive ? 'live' : 'latest' };
    }
    const highlightId = highlightCandidate?.location?.id ?? null;

    locations.forEach(location => {
        const coords = [location.coordinates.latitude, location.coordinates.longitude];
        const isHighlight = highlightId !== null && location.id === highlightId;

        const bindInteractions = (markerInstance) => {
            markerInstance.on('click', () => {
                const now2  = Math.floor(Date.now() / 60000);
                const loc2  = Math.floor(new Date(location.localTimestamp).getTime() / 60000);
                const isLiveM   = (now2 - loc2) <= thresholdFor(location);
                const isLatestM = location.isLatestLocation;

                document.getElementById('modalContent').innerHTML =
                    generateLocationModalContent(location, { isLive: isLiveM, isLatest: isLatestM });

                new bootstrap.Modal(document.getElementById('locationModal')).show();
            });
        };

        if (isHighlight && highlightLayer) {
            const icon = highlightCandidate?.type === 'live' ? liveMarker : latestLocationMarker;
            const highlightMarker = L.marker(coords, { icon });
            if (highlightCandidate?.type === 'latest') {
                highlightMarker.bindTooltip("User's latest location.", {
                    direction: "top",
                    offset: [0, -25]
                });
            }
            bindInteractions(highlightMarker);
            highlightMarker.addTo(highlightLayer);
            highlightMarker.setZIndexOffset(1000);
        } else {
            const marker = L.marker(coords, {});
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
    scheduleMarkerTransition(locations);
};

/**
 * Schedules a timer to re-render markers when the current live marker should transition to latest.
 * Calculates when the earliest live location will age past its threshold and sets a timeout.
 * @param {Array} locations - Array of location objects with localTimestamp and locationTimeThresholdMinutes
 */
const scheduleMarkerTransition = (locations) => {
    // Clear any existing timer
    if (markerTransitionTimer) {
        clearTimeout(markerTransitionTimer);
        markerTransitionTimer = null;
    }

    if (!locations || locations.length === 0) return;

    const nowMs = Date.now();
    let earliestTransitionMs = null;

    // Find the earliest time when a live marker should transition
    locations.forEach(location => {
        const locMs = new Date(location.localTimestamp).getTime();
        const thresholdMs = (location.locationTimeThresholdMinutes ?? 10) * 60 * 1000;
        const ageMs = nowMs - locMs;

        // If location is currently live (within threshold)
        if (ageMs <= thresholdMs) {
            // Calculate when it will expire
            const expiresAtMs = locMs + thresholdMs;
            const msUntilExpiry = expiresAtMs - nowMs;

            if (msUntilExpiry > 0 && (earliestTransitionMs === null || msUntilExpiry < earliestTransitionMs)) {
                earliestTransitionMs = msUntilExpiry;
            }
        }
    });

    // If there's a live marker that will expire, schedule re-render
    if (earliestTransitionMs !== null) {
        // Add small buffer (1 second) to ensure threshold has definitely passed
        markerTransitionTimer = setTimeout(() => {
            displayLocationsOnMap(mapContainer, locations);
        }, earliestTransitionMs + 1000);
    }
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

// Generate the content for the modal when a marker is clicked
const generateLocationModalContent = (location, { isLive, isLatest }) => {
    // build your badge HTML
    let badge = '';
    if (isLive) {
        badge = '<span class="badge bg-danger float-end ms-2">LIVE LOCATION</span>';
    } else if (isLatest) {
        badge = '<span class="badge bg-success float-end ms-2">LATEST LOCATION</span>';
    }

    let dynamicMinHeight;
    let style;
    let charCount = location?.notes ? location.notes.length : 0;
    if (charCount === 0) {
        dynamicMinHeight = 0;
        style = 'display: none;';
    } else {
        const lineHeightEm = 1.5;
        const minLines = Math.ceil(charCount / 50);
        dynamicMinHeight = 16 * (lineHeightEm * minLines);
        style = `min-height: ${dynamicMinHeight}px; display: block;`;
    }
    const timestamps = getLocationTimestampInfo(location);
    const sourceZone = getLocationSourceTimeZone(location);
    const recordedTime = timestamps.source
        ? `<div>${timestamps.source}</div>`
        : `<div class="fst-italic text-muted">Source timezone unavailable</div>`;

    return `<div class="container-fluid">
        <div class="row mb-2">
            <div class="col-12">
                ${badge}
            </div>
        </div>
        <div class="row mb-2">
            <div class="col-6">
                <strong>Datetime (your timezone):</strong>
                <div>${timestamps.viewer}</div>
            </div>
            <div class="col-6">
                <strong>Recorded local time:</strong>
                ${recordedTime}
                ${sourceZone && !timestamps.source ? `<div class="small text-muted">${sourceZone}</div>` : ''}
            </div>
        </div>
        <div class="row mb-2">
            <div class="col-12"><strong>Coordinates:</strong></div>
            <div class="col-6">
                <p class="mb-0">Latitude: <span class="fw-bold text-primary">${location.coordinates.latitude}</span></p>
            </div>
            <div class="col-6">
                <p class="mb-0">Longitude: <span class="fw-bold text-primary">${location.coordinates.longitude}</span></p>
            </div>
        </div>
        <div class="row mb-2">
            <div class="col-6"><strong>Activity:</strong>
            <span>${(location.activityType && location.activityType !== 'Unknown') ? location.activityType :
        '<i class="bi bi-patch-question" title="No available data for Activity"></i>'}</span></div>
            <div class="col-6"><strong>Altitude:</strong> <span>${formatDecimal(location.altitude) != null ? formatDecimal(location.altitude) + ' m' : '<i class="bi bi-patch-question" title="No available data for Altitude"></i>'}</span></div>
        </div>
        <div class="row mb-2">
            <div class="col-6"><strong>Accuracy:</strong> <span>${formatDecimal(location.accuracy) != null ? formatDecimal(location.accuracy) + ' m' : '<i class="bi bi-patch-question" title="No available data for Accuracy"></i>'}</span></div>
            <div class="col-6"><strong>Speed:</strong> <span>${formatDecimal(location.speed) != null ? formatDecimal(location.speed) + ' km/h' : '<i class="bi bi-patch-question" title="No available data for Speed"></i>'}</span></div>
        </div>
        <div class="row mb-2">
            <div class="col-12"><strong>Address:</strong> <span>${location.fullAddress || '<i class="bi bi-patch-question" title="No available data for Address"></i> '}</span><br/>
            ${generateGoogleMapsLink(location)}
            ${generateWikipediaLinkHtml(location, { query: location.place || location.fullAddress })}
            </div>
        </div>
        <div class="row mb-2">
            <div class="col-12" style="${style}"><strong>Notes:</strong>
                <div class="border p-1" >
                    ${location.notes || 'No notes available'}
                </div>
            </div>
        </div>
    </div>
`;
};


/**
 * Generates a link query for Google Maps and opens in new tab
 * @param {string} address
 * @returns {string} an <a> tag to open Google Maps
 */
/**
 * Generates a Google Maps link combining address and coordinates for precision.
 * @param {{ fullAddress?: string, coordinates: { latitude: number, longitude: number } }} location
 */
const generateGoogleMapsLink = location => {
    const addr = location?.fullAddress || '';
    const lat  = location?.coordinates?.latitude;
    const lon  = location?.coordinates?.longitude;
    const hasCoords = Number.isFinite(+lat) && Number.isFinite(+lon);
    const query = addr && hasCoords
        ? `${addr} (${(+lat).toFixed(6)},${(+lon).toFixed(6)})`
        : hasCoords
            ? `${(+lat).toFixed(6)},${(+lon).toFixed(6)}`
            : addr;
    const q = encodeURIComponent(query || '');
    return `
    <a
      href="https://www.google.com/maps/search/?api=1&query=${q}"
      target="_blank"
      class="ms-2 btn btn-outline-primary btn-sm"
      title="View in Google Maps"
    ><i class="bi bi-globe-europe-africa"></i> Maps</a>
  `;
};

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
    params.set('lat',  c.lat.toFixed(6));
    params.set('lng',  c.lng.toFixed(6));
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

const getUserLocations = () => {
    const url = '/Public/Users/GetPublicTimeline';

    let requestData = {
        minLongitude: mapBounds.getSouthWest().lng,
        minLatitude: mapBounds.getSouthWest().lat,
        maxLongitude: mapBounds.getNorthEast().lng,
        maxLatitude: mapBounds.getNorthEast().lat,
        username: username,
        zoomLevel: zoomLevel,
    };

    // Send the requestData to your backend API (adjust the URL as needed)
    fetch(url, {
        method: "POST",
        headers: {
            "Content-Type": "application/json",
            "Accept": "application/json",
        },
        body: JSON.stringify(requestData)
    })
        .then(response => {
            if (!response.ok) {
                throw new Error(`HTTP error! Status: ${response.status}`);
            }
            return response.json();
        })
        .then(data => {
            // TODO: Update map with new data
            locations = data.data; // Store the fetched locations in the global variable
            if (locations && locations.length > 0) {
                displayLocationsOnMap(mapContainer, locations);
            } else {
                // Set the mapContainer to a default view if no locations exist AND user is on zoom level 2
                if (mapContainer.getZoom() <= 2) {
                    mapContainer.setView([20, 0], 2);
                }
            }
        })
        .catch(error => console.error("Error fetching location data:", error));
}

const debouncedGetUserLocations = debounce(getUserLocations, 350);

const getUserStats = async (username) => {
    if (!username) {
        throw new Error("Username is required");
    }

    const url = `/Public/Users/GetPublicStats/${encodeURIComponent(username)}`;

    const response = await fetch(url, {
        method: "GET",
        headers: {
            "Accept": "application/json"
        }
    });

    if (!response.ok) {
        // You can handle 404/403/other errors differently if you like
        const errorText = await response.text();
        throw new Error(`Error ${response.status}: ${errorText}`);
    }

    // assuming the response is JSON matching UserLocationStatsDto
    const stats = await response.json();

    const summaryParts = [];

    if (stats.totalLocations != null)
        summaryParts.push(`<strong>Total Locations:</strong>  ${stats.totalLocations}`);
    if (stats.fromDate)
        summaryParts.push(`<strong>From Date:</strong> ${formatDate({ iso: stats.fromDate, displayTimeZone: viewerTimeZone })}`);
    if (stats.toDate)
        summaryParts.push(`<strong>To Date:</strong> ${formatDate({ iso: stats.toDate, displayTimeZone: viewerTimeZone })}`);
    if (stats.countriesVisited != null)
        summaryParts.push(`<strong>Countries:</strong> ${stats.countriesVisited}`);
    if (stats.regionsVisited != null)
        summaryParts.push(`<strong>Regions:</strong> ${stats.regionsVisited}`);
    if (stats.citiesVisited != null)
        summaryParts.push(`<strong>Cities:</strong> ${stats.citiesVisited}`);

    const summary = summaryParts.join(" | ");
    document.getElementById("timeline-summary").innerHTML  = summary;
};

const fixParentPadding = () => {
    document.querySelectorAll('.wayfarer-embed').forEach(embed => {
        const parent = embed.closest('.container-fluid');
        if (parent) {
            parent.style.backgroundColor = "transparent !important";
            parent.style.paddingLeft = '0';
            parent.style.paddingRight = '0';
        }
    });
};

