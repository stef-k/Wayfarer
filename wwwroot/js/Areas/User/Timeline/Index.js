import { confirmTimelineDeletion } from './timeline-delete.js';
import { renderStatistics } from './statistics.js';
let locations = []; // Declare locations as a global variable
let mapContainer = null;
let zoomLevel = 3;
let mapBounds = null;
let markerLayer, clusterLayer, highlightLayer;
let stream;
let sessionEnded = false;
let markerTransitionTimer = null; // Timer for live-to-latest marker transition
import {addZoomLevelControl, latestLocationMarker, liveMarker} from '../../../map-utils.js';
import { createTileLayer } from '../../../retryTileLayer.js';
import { encodeFeatureText as encode } from '../../../util/feature-metadata.js';
import { count, html, installTimelineZoom, text } from './timeline-text.js';
import { renderLiveLocation } from './timeline-location.js';
import { installMapAttribution } from '../../../util/map-attribution.js';
import {
    formatHistoryDate,
    isLiveLocation,
    scheduleLocationExpiry,
    getViewerTimeZone,
} from '../../../util/datetime.js';
import {
    initActivitySelect,
    setupActivityEditorEvents,
} from '../../../util/activity-editor.js';
import {
    initWikipediaPopovers,
} from '../../../util/wikipedia-utils.js';

// permalink setup
const urlParams = new URLSearchParams(window.location.search);
const initialLat = parseFloat(urlParams.get('lat'));
const initialLng = parseFloat(urlParams.get('lng'));
const z = parseInt(urlParams.get('zoom'), 10);
zoomLevel = (!isNaN(z) && z >= 0) ? z : 3;
const initialCenter = (
    !isNaN(initialLat) && !isNaN(initialLng)
        ? [initialLat, initialLng]
        : [20, 0]
);

const viewerTimeZone = getViewerTimeZone();
/** HTML sinks and live observations share the initiating document's locale. */
const sessionMessage = () => text('SessionEnded', 'Session ended or account changed. Sign in again before reading Timeline.');
const sessionResponse = response => response.redirected || response.status === 401 || response.status === 403;
const mapNotice = () => document.getElementById('liveTimelineNotice');
const statisticsNotice = () => document.getElementById('liveTimelineStatsNotice');
const streamNotice = () => document.getElementById('liveTimelineStreamNotice');

document.addEventListener('DOMContentLoaded', () => {
    // Initialize the mapContainer and load location data
    mapContainer = initializeMap();

    mapBounds = mapContainer.getBounds();
    getUserLocations();
    onZoomOrMoveChanges();

    const username = document.getElementById('username').dataset.username;
    if (username) connectLiveUpdates(username);

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
    document.addEventListener("click", async function (event) {
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

        await confirmTimelineDeletion(locationId, () => {
            modalInstance?.hide();
            mapContainer = initializeMap();
            mapBounds = mapContainer.getBounds();
            onZoomOrMoveChanges();
            getUserLocations();
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
    setupActivityEditorEvents('#modalContent', undefined, { protectLanguage: true });

    getUserStats();
});

/** Stop private live publication once the authenticated document loses its session. */
const endLiveSession = () => {
    if (sessionEnded) return;
    sessionEnded = true;
    stream?.close();
    clearTimeout(markerTransitionTimer);
    locations = [];
    displayLocationsOnMap(mapContainer, locations);
    document.getElementById('timeline-summary').textContent = '';
    document.getElementById('modalContent').textContent = sessionMessage();
    document.getElementById('statsModalContent').textContent = sessionMessage();
    mapNotice().textContent = sessionMessage();
    statisticsNotice().textContent = streamNotice().textContent = '';
};

/** Connect live refresh independently of the viewport and statistics notices. */
const connectLiveUpdates = username => {
    try {
        stream = new EventSource(`/api/sse/stream/location-update/${username}`);
        stream.onmessage = () => { getUserLocations(); getUserStats(); };
        stream.onopen = () => { streamNotice().textContent = ''; };
        stream.onerror = () => {
            if (sessionEnded) return;
            streamNotice().textContent = stream.readyState === EventSource.CLOSED
                ? text('LiveStreamClosed', 'Live updates stopped. Reload to reconnect.')
                : text('LiveStreamFailed', 'Live updates are temporarily unavailable. The map will retry automatically.');
        };
    } catch {
        streamNotice().textContent = text('LiveStreamClosed', 'Live updates stopped. Reload to reconnect.');
    }
};

/** Initialize the map with the existing cached tile layer and localized control names. */
const initializeMap = () => {
    if (mapContainer !== undefined && mapContainer !== null) {
        mapContainer.off();
        mapContainer.remove();
    }
    mapContainer = L.map('mapContainer', {
        zoomAnimation: true, zoomControl: false
    }).setView(initialCenter, zoomLevel);
    installTimelineZoom(mapContainer);
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

    locations.forEach(location => {
        const coords = [location.coordinates.latitude, location.coordinates.longitude];
        const isHighlight = highlightId !== null && location.id === highlightId;

        const bindInteractions = (markerInstance) => {
            markerInstance.on('click', () => {
                const isLiveM = isLiveLocation(location);
                const isLatestM = location.isLatestLocation;

                document.getElementById('modalContent').innerHTML =
                    renderLiveLocation(location, { isLive: isLiveM, isLatest: isLatestM }, viewerTimeZone);

                new bootstrap.Modal(document.getElementById('locationModal')).show();
            });
        };

        if (isHighlight && highlightLayer) {
            const icon = highlightCandidate?.type === 'live' ? liveMarker : latestLocationMarker;
            const alt = highlightCandidate.type === 'live' ? text('LiveBadge', 'LIVE LOCATION') : text('LatestBadge', 'LATEST LOCATION');
            const highlightMarker = L.marker(coords, { icon, alt, title: alt });
            if (highlightCandidate?.type === 'latest') {
                highlightMarker.bindTooltip(html("LatestTooltip", "Your latest location."), {
                    direction: "top",
                    offset: [0, -25]
                });
            }
            bindInteractions(highlightMarker);
            highlightMarker.addTo(highlightLayer);
            highlightMarker.setZIndexOffset(1000);
        } else {
            const label = text('RecordId', 'Record {0}', location.id);
            const marker = L.marker(coords, { title: label, alt: label });
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

/** Retain camera changes for the live map permalink and edit return. */
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
    const params = new URLSearchParams(window.location.search);
    params.set('lat',  c.lat.toFixed(6));
    params.set('lng',  c.lng.toFixed(6));
    params.set('zoom', z);

    const newUrl = `${window.location.pathname}?${params.toString()}${window.location.hash}`;
    if (zoomLevel !== 3 || c.lat.toFixed(6) !== '20.000000' || c.lng.toFixed(6) !== '0.000000') {
        history.replaceState(history.state, '', newUrl);
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

let viewportRead = 0;
/** Read the existing viewport API and check its success envelope before displaying results. */
const getUserLocations = async () => {
    if (sessionEnded) return;
    const generation = ++viewportRead;
    mapNotice().textContent = text('LiveLoading', 'Loading locations…');

    let requestData = {
        minLongitude: mapBounds.getSouthWest().lng,
        minLatitude: mapBounds.getSouthWest().lat,
        maxLongitude: mapBounds.getNorthEast().lng,
        maxLatitude: mapBounds.getNorthEast().lat,
        zoomLevel: zoomLevel,
    };

    try {
        const response = await fetch('/api/Location/get-user-locations', {
            method: 'POST', credentials: 'include', headers: {
                'Content-Type': 'application/json', Accept: 'application/json'
            }, body: JSON.stringify(requestData)
        });
        if (sessionEnded || generation !== viewportRead) return;
        if (sessionResponse(response)) { endLiveSession(); return; }
        if (!response.ok) throw new Error();
        const data = await response.json();
        if (sessionEnded || generation !== viewportRead) return;
        if (!data.success || !Array.isArray(data.data)) throw new Error();
        locations = data.data;
        displayLocationsOnMap(mapContainer, locations);
        if (!locations.length && mapContainer.getZoom() <= 2) mapContainer.setView([20, 0], 2);
        mapNotice().textContent = locations.length ? '' : text('LiveEmpty', 'No locations in this map view.');
    } catch {
        if (!sessionEnded && generation === viewportRead) mapNotice().textContent =
            text('LiveReadFailed', 'Could not load locations. Move the map or reload to try again.');
    }
};

const debouncedGetUserLocations = debounce(getUserLocations, 350);

/** Load the independent all-history summary without clearing the current viewport. */
const getUserStats = async () => {
    if (sessionEnded) return;
    try {
        const response = await fetch('/api/users/stats', { headers: { Accept: 'application/json' } });
        if (sessionResponse(response)) { endLiveSession(); return; }
        if (!response.ok) throw new Error();
        const stats = await response.json();
        if (sessionEnded) return;
        renderSummary(stats);
        statisticsNotice().textContent = '';
    } catch {
        if (!sessionEnded) statisticsNotice().textContent = text('LiveStatsFailed', 'Could not load location statistics. Reload to try again.');
    }
};

/** Preserve the existing geographic drill-down links and viewer timezone. */
const renderSummary = stats => {
    const summaryParts = [];

    if (stats.totalLocations != null)
        summaryParts.push(`<strong>${html('TotalLocations', 'Total Locations:')}</strong> ${count(stats.totalLocations)}`);
    if (stats.fromDate)
        summaryParts.push(`<strong>${html('FromDate', 'From Date:')}</strong> ${encode(formatHistoryDate(stats.fromDate, viewerTimeZone))}`);
    if (stats.toDate)
        summaryParts.push(`<strong>${html('ToDate', 'To Date:')}</strong> ${encode(formatHistoryDate(stats.toDate, viewerTimeZone))}`);
    if (stats.countriesVisited != null)
        summaryParts.push(`<strong><a href="#" class="text-decoration-none stat-link" data-stat-type="countries">${html('CountriesLabel', 'Countries:')}</a></strong> ${count(stats.countriesVisited)}`);
    if (stats.regionsVisited != null)
        summaryParts.push(`<strong><a href="#" class="text-decoration-none stat-link" data-stat-type="regions">${html('RegionsLabel', 'Regions:')}</a></strong> ${count(stats.regionsVisited)}`);
    if (stats.citiesVisited != null)
        summaryParts.push(`<strong><a href="#" class="text-decoration-none stat-link" data-stat-type="cities">${html('CitiesLabel', 'Cities:')}</a></strong> ${count(stats.citiesVisited)}`);

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

        if (sessionResponse(response)) {
            endLiveSession();
            return;
        }
        if (!response.ok) throw new Error();

        const detailedStats = await response.json();
        if (sessionEnded) return;

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

    } catch {
        wayfarer.showAlert('danger', text('LiveDetailedStatsFailed', 'Failed to load detailed statistics. Please try again.'));
    }
};

/**
 * Generate HTML content for the stats modal with hierarchical collapsible structure
 * @param {object} stats - Detailed stats object
 * @param {string} highlightType - Which section to highlight
 * @returns {string} HTML content
 */
const generateStatsModalContent = (stats, highlightType) => {
    return renderStatistics(stats, highlightType,
        iso => encode(formatHistoryDate(iso, viewerTimeZone)));
};
