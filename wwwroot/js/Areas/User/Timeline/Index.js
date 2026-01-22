let locations = []; // Declare locations as a global variable
let mapContainer = null;
let zoomLevel = 3;
let mapBounds = null;
let markerLayer, clusterLayer, highlightLayer;
let stream;
let markerTransitionTimer = null; // Timer for live-to-latest marker transition
// Map tiles config (proxy URL + attribution) injected by layout.
const tilesConfig = window.wayfarerTileConfig || {};
const tilesUrl = tilesConfig.tilesUrl || `${window.location.origin}/Public/tiles/{z}/{x}/{y}.png`;
const tilesAttribution = tilesConfig.attribution || '&copy; OpenStreetMap contributors';
import {addZoomLevelControl, latestLocationMarker, liveMarker} from '../../../map-utils.js';
import {
    formatViewerAndSourceTimes,
    formatDate,
    formatDecimal,
    getViewerTimeZone,
} from '../../../util/datetime.js';
import {
    generateActivityEditorHtml,
    initActivitySelect,
    setupActivityEditorEvents,
} from '../../../util/activity-editor.js';
import {
    generateWikipediaLinkHtml,
    initWikipediaPopovers,
} from '../../../util/wikipedia-utils.js';

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
const returnUrlParam = encodeURIComponent(`${window.location.pathname}${window.location.search}`);
const buildEditUrl = id => `/User/Location/Edit/${id}?returnUrl=${returnUrlParam}`;

document.addEventListener('DOMContentLoaded', () => {
    // Initialize the mapContainer and load location data
    mapContainer = initializeMap();

    mapBounds = mapContainer.getBounds();
    getUserLocations();
    onZoomOrMoveChanges();

    let username = document.getElementById('username').dataset.username;
    try {
        stream = new EventSource(`/api/sse/stream/location-update/${username}`);
    } catch (e) {
        console.error(`Could not connect to stream ${e}`);
    }
    if (!username) {
        console.error('Username not found!');
        return;
    }

    // handle the SSE stream
    stream.onmessage = (event) => {
        handleStream(event);
        getUserStats();
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

    // delete events from pop ups
    document.addEventListener("click", function (event) {
        const deleteLink = event.target.closest(".delete-location-from-popup");
        if (!deleteLink) return; // Exit if the click is not on a delete button

        event.preventDefault(); // Prevent the default link behavior

        const locationIdRaw = deleteLink.getAttribute("data-location-id");
        const locationId = Number.parseInt(locationIdRaw, 10);
        if (Number.isNaN(locationId)) {
            // Defensive guard: keep API payload numeric so the backend binder accepts it.
            return;
        }

        // parent modal showing the location
        const modalElement = document.querySelector(".modal.show");
        const modalInstance = bootstrap.Modal.getInstance(modalElement);

        wayfarer.showConfirmationModal({
            title: "Confirm Deletion",
            message: "Are you sure you want to delete the selected location? This action cannot be undone.",
            confirmText: "Delete",
            onConfirm: () => {
                fetch("/api/Location/bulk-delete", {
                    method: "POST",
                    headers: {"Content-Type": "application/json"},
                    body: JSON.stringify({locationIds: [locationId]}) // Send as an array
                })
                    .then(response => response.json())
                    .then(data => {
                        if (data.success) {
                            if (modalInstance) {
                                modalInstance.hide();
                            }
                            mapContainer = initializeMap();
                            getUserLocations();
                        } else {
                            alert("Failed to delete location");
                        }
                    })
                    .catch(error => console.error("Error:", error));
            }
        });
    });

    // Wire up the Bootstrap "shown" event for Wikipedia hover cards and activity editor
    const modalEl = document.getElementById('locationModal');
    if (!modalEl) {
        console.error('Modal element not found!');
    } else {
        modalEl.addEventListener('shown.bs.modal', () => {
            initWikipediaPopovers(modalEl);
            // Initialize TomSelect on activity dropdown if present
            const activityEditor = modalEl.querySelector('.activity-editor');
            if (activityEditor) {
                const locationId = activityEditor.dataset.locationId;
                initActivitySelect(locationId);
            }
        });
    }

    // Set up activity editor event handlers
    setupActivityEditorEvents('#modalContent');

    getUserStats();
});

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
        zoomAnimation: true
    }).setView(initialCenter, zoomLevel);
    L.tileLayer(tilesUrl, {
        maxZoom: 19, attribution: tilesAttribution
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
const generateLocationModalContent = (location, {isLive, isLatest}) => {
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
            <div class="col-6">${generateActivityEditorHtml(location)}</div>
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
        <div class="row">
            <div class="col-5">
                <a href="${buildEditUrl(location.id)}" class="btn-link" title="Edit location">Edit</a>
            </div>
            <div class="col-5 offset-2">
                <a href="#" class="btn-link text-danger delete-location-from-popup" data-location-id="${location.id}" title="Delete location">Delete</a>
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
    const url = `/api/Location/get-user-locations`;

    let requestData = {
        minLongitude: mapBounds.getSouthWest().lng,
        minLatitude: mapBounds.getSouthWest().lat,
        maxLongitude: mapBounds.getNorthEast().lng,
        maxLatitude: mapBounds.getNorthEast().lat,
        zoomLevel: zoomLevel,
    };

    // Send the requestData to your backend API (adjust the URL as needed)
    fetch(url, {
        method: "POST", credentials: 'include', headers: {
            "Content-Type": "application/json", "Accept": "application/json",
        }, body: JSON.stringify(requestData)
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

const getUserStats = async () => {

    const url = `/api/users/stats`;

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
        summaryParts.push(`<strong>Total Locations:</strong> ${stats.totalLocations}`);
    if (stats.fromDate)
        summaryParts.push(`<strong>From Date:</strong> ${formatDate({ iso: stats.fromDate, displayTimeZone: viewerTimeZone })}`);
    if (stats.toDate)
        summaryParts.push(`<strong>To Date:</strong> ${formatDate({ iso: stats.toDate, displayTimeZone: viewerTimeZone })}`);
    if (stats.countriesVisited != null)
        summaryParts.push(`<strong><a href="#" class="text-decoration-none stat-link" data-stat-type="countries">Countries:</a></strong> ${stats.countriesVisited}`);
    if (stats.regionsVisited != null)
        summaryParts.push(`<strong><a href="#" class="text-decoration-none stat-link" data-stat-type="regions">Regions:</a></strong> ${stats.regionsVisited}`);
    if (stats.citiesVisited != null)
        summaryParts.push(`<strong><a href="#" class="text-decoration-none stat-link" data-stat-type="cities">Cities:</a></strong> ${stats.citiesVisited}`);

    const summary = summaryParts.join(" | ");
    document.getElementById("timeline-summary").innerHTML = summary;

    // Add click handlers for stat links
    document.querySelectorAll('.stat-link').forEach(link => {
        link.addEventListener('click', async (e) => {
            e.preventDefault();
            const statType = e.currentTarget.getAttribute('data-stat-type');
            await showDetailedStats(statType);
        });
    });
};

/**
 * Fetch and display detailed stats in a modal
 * @param {string} statType - Type of stat to highlight (countries, regions, cities)
 */
const showDetailedStats = async (statType) => {
    try {
        const response = await fetch('/api/users/stats/detailed', {
            method: 'GET',
            headers: {
                'Accept': 'application/json'
            }
        });

        if (!response.ok) {
            throw new Error(`Error ${response.status}: ${await response.text()}`);
        }

        const detailedStats = await response.json();

        // Generate modal content
        const modalContent = generateStatsModalContent(detailedStats, statType);
        document.getElementById('statsModalContent').innerHTML = modalContent;

        // Show the modal
        new bootstrap.Modal(document.getElementById('statsModal')).show();

        // Add click handlers for country/region/city coordinates
        document.querySelectorAll('.country-coords-link').forEach(link => {
            link.addEventListener('click', (e) => {
                e.preventDefault();
                const lat = parseFloat(link.getAttribute('data-lat'));
                const lng = parseFloat(link.getAttribute('data-lng'));

                // Extract zoom from href URL
                const href = link.getAttribute('href');
                const urlParams = new URLSearchParams(href.substring(1)); // Remove leading '?'
                const zoom = parseInt(urlParams.get('zoom')) || 8;

                // Close modal and navigate to location on map
                bootstrap.Modal.getInstance(document.getElementById('statsModal')).hide();
                mapContainer.setView([lat, lng], zoom);
            });
        });

    } catch (error) {
        console.error('Error fetching detailed stats:', error);
        alert('Failed to load detailed statistics');
    }
};

/**
 * Generate HTML content for the stats modal with hierarchical collapsible structure
 * @param {object} stats - Detailed stats object
 * @param {string} highlightType - Which section to highlight
 * @returns {string} HTML content
 */
const generateStatsModalContent = (stats, highlightType) => {
    let html = '<div class="container-fluid">';

    // Summary section
    html += '<div class="row mb-3">';
    html += '<div class="col-12">';
    html += `<h6>Overview</h6>`;
    html += `<p><strong>Total Locations:</strong> ${stats.totalLocations}</p>`;
    if (stats.fromDate && stats.toDate) {
        html += `<p><strong>Date Range:</strong> ${formatDate({ iso: stats.fromDate, displayTimeZone: viewerTimeZone })} to ${formatDate({ iso: stats.toDate, displayTimeZone: viewerTimeZone })}</p>`;
    }
    html += '</div>';
    html += '</div>';

    // Countries section with hierarchical collapsible structure
    const countriesHighlight = highlightType === 'countries' ? 'bg-light border' : '';
    html += `<div class="row mb-3 ${countriesHighlight} p-2">`;
    html += '<div class="col-12">';
    html += `<h6>Countries (${stats.countries.length})</h6>`;

    if (stats.countries.length > 0) {
        html += '<div class="accordion" id="countriesAccordion">';

        stats.countries.forEach((country, countryIdx) => {
            const homeLabel = country.isHomeCountry ? ' <span class="badge bg-info">Home</span>' : '';
            const firstVisit = formatDate({ iso: country.firstVisit, displayTimeZone: viewerTimeZone });
            const lastVisit = formatDate({ iso: country.lastVisit, displayTimeZone: viewerTimeZone });

            // Extract coordinates from PostGIS Point
            const lat = country.coordinates?.latitude || 0;
            const lng = country.coordinates?.longitude || 0;
            const countryMapUrl = `?lat=${lat.toFixed(6)}&lng=${lng.toFixed(6)}&zoom=8`;

            // Get regions for this country
            const countryRegions = stats.regions.filter(r => r.countryName === country.name);

            html += `<div class="accordion-item">`;
            html += `<h2 class="accordion-header" id="country-heading-${countryIdx}">`;
            html += `<div class="d-flex w-100 align-items-center">`;
            html += `<button class="accordion-button collapsed flex-grow-1" type="button" data-bs-toggle="collapse" data-bs-target="#country-${countryIdx}">`;
            html += `${country.name}${homeLabel} <small class="ms-2 text-muted">(${country.visitCount} records, ${firstVisit} - ${lastVisit})</small>`;
            html += `</button>`;
            html += `<a href="${countryMapUrl}" class="btn btn-sm btn-outline-primary country-coords-link me-2" data-lat="${lat}" data-lng="${lng}" onclick="event.stopPropagation();" title="View on map" style="min-width: 70px;"><i class="bi bi-geo-alt"></i> Map</a>`;
            html += `</div>`;
            html += `</h2>`;
            html += `<div id="country-${countryIdx}" class="accordion-collapse collapse" data-bs-parent="#countriesAccordion">`;
            html += `<div class="accordion-body">`;

            if (countryRegions.length > 0) {
                html += `<h6>Regions (${countryRegions.length})</h6>`;
                html += `<div class="accordion" id="regionsAccordion-${countryIdx}">`;

                countryRegions.forEach((region, regionIdx) => {
                    const regFirstVisit = formatDate({ iso: region.firstVisit, displayTimeZone: viewerTimeZone });
                    const regLastVisit = formatDate({ iso: region.lastVisit, displayTimeZone: viewerTimeZone });
                    const regLat = region.coordinates?.latitude || 0;
                    const regLng = region.coordinates?.longitude || 0;
                    const regionMapUrl = `?lat=${regLat.toFixed(6)}&lng=${regLng.toFixed(6)}&zoom=10`;

                    // Get cities for this region
                    const regionCities = stats.cities.filter(c => c.regionName === region.name && c.countryName === country.name);

                    html += `<div class="accordion-item">`;
                    html += `<h2 class="accordion-header" id="region-heading-${countryIdx}-${regionIdx}">`;
                    html += `<div class="d-flex w-100 align-items-center">`;
                    html += `<button class="accordion-button collapsed flex-grow-1" type="button" data-bs-toggle="collapse" data-bs-target="#region-${countryIdx}-${regionIdx}">`;
                    html += `${region.name} <small class="ms-2 text-muted">(${region.visitCount} records, ${regFirstVisit} - ${regLastVisit})</small>`;
                    html += `</button>`;
                    html += `<a href="${regionMapUrl}" class="btn btn-sm btn-outline-primary country-coords-link me-2" data-lat="${regLat}" data-lng="${regLng}" onclick="event.stopPropagation();" title="View on map" style="min-width: 70px;"><i class="bi bi-geo-alt"></i> Map</a>`;
                    html += `</div>`;
                    html += `</h2>`;
                    html += `<div id="region-${countryIdx}-${regionIdx}" class="accordion-collapse collapse" data-bs-parent="#regionsAccordion-${countryIdx}">`;
                    html += `<div class="accordion-body">`;

                    if (regionCities.length > 0) {
                        html += `<h6>Cities (${regionCities.length})</h6>`;
                        html += '<div class="list-group">';

                        regionCities.forEach(city => {
                            const cityFirstVisit = formatDate({ iso: city.firstVisit, displayTimeZone: viewerTimeZone });
                            const cityLastVisit = formatDate({ iso: city.lastVisit, displayTimeZone: viewerTimeZone });
                            const cityLat = city.coordinates?.latitude || 0;
                            const cityLng = city.coordinates?.longitude || 0;
                            const cityMapUrl = `?lat=${cityLat.toFixed(6)}&lng=${cityLng.toFixed(6)}&zoom=13`;

                            html += `<div class="list-group-item d-flex justify-content-between align-items-center">`;
                            html += `<div><strong>${city.name}</strong> <small class="text-muted">(${city.visitCount} records, ${cityFirstVisit} - ${cityLastVisit})</small></div>`;
                            html += `<a href="${cityMapUrl}" class="btn btn-sm btn-outline-primary country-coords-link" data-lat="${cityLat}" data-lng="${cityLng}" title="View on map" style="min-width: 70px;"><i class="bi bi-geo-alt"></i> Map</a>`;
                            html += `</div>`;
                        });

                        html += '</div>';
                    } else {
                        html += '<p class="text-muted">No cities in this region</p>';
                    }

                    html += `</div></div></div>`;
                });

                html += `</div>`;
            } else {
                html += '<p class="text-muted">No regions in this country</p>';
            }

            html += `</div></div></div>`;
        });

        html += '</div>';
    } else {
        html += '<p class="text-muted">No country data available</p>';
    }

    html += '</div>';
    html += '</div>';

    html += '</div>';

    return html;
};

const handleStream = (event) => {
    getUserLocations();
};

