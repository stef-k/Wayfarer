import { confirmLocationHistoryDeletion, bindLocationBulkDeletion } from '../../../util/location-history-actions.js';
import { locationText, locationHtml, locationMapsLink, locationTimes, sourceTimeZone, displayCoordinate,
    measurement, renderLocationStats, readLocationHistory } from '../../../util/location-history-text.js';
import { message, number } from '../../../localization/messages.js';
import { encodeFeatureText as encode } from '../../../util/feature-metadata.js';
import { notesHtmlForDisplay } from '../../../util/notes-images.js';
let locations = []; // Declare locations as a global variable
let mapContainer = null;
let zoomLevel = 2;
let mapBounds = null;
let markerClusterGroup = null;
let isSearchPanelOpen = false;
let markerLayer, clusterLayer, highlightLayer;
let markerTransitionTimer = null; // Timer for live-to-latest marker transition
import {addZoomLevelControl, latestLocationMarker, liveMarker} from '../../../map-utils.js';
import { installLocationMapControls } from '../../../util/location-map-controls.js';
import { createTileLayer } from '../../../retryTileLayer.js';
import { renderLocationAddress, renderAddressComponent } from '../../../util/location-address.js';
import {
    isLiveLocation,
    scheduleLocationExpiry,
    currentDateInputValue,
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
const z = parseInt(urlParams.get('zoom'), 10);
zoomLevel = (!isNaN(z) && z >= 0) ? z : 3;
let initialCenter = (
    !isNaN(initialLat) && !isNaN(initialLng)
        ? [initialLat, initialLng]
        : [20, 0]
);

const returnUrlParam = encodeURIComponent(`${window.location.pathname}${window.location.search}`);
const buildEditUrl = id => `/User/Location/Edit/${id}?returnUrl=${returnUrlParam}`;
const syncLocationActivity = (locationId, activityType) => {
    // Keep in-memory location data aligned with table/modal edits.
    const target = locations.find(loc => loc.id === locationId);
    if (!target) return;

    target.activityType = activityType || null;
    if (Object.prototype.hasOwnProperty.call(target, 'activity')) {
        target.activity = activityType || null;
    }
};

document.addEventListener('DOMContentLoaded', () => {
    // Initialize the mapContainer and load location data
    mapContainer = initializeMap();

    mapBounds = mapContainer.getBounds();
    getUserLocations();
    onZoomOrMoveChanges();

    // Cleanup timers on page unload
    window.addEventListener('beforeunload', () => {
        if (markerTransitionTimer) {
            clearTimeout(markerTransitionTimer);
        }
    });

    const selectAllCheckbox = document.getElementById("selectAll");
    const tableBody = document.querySelector("#locationsTable tbody");

    // set max date in search criteria for toTimestamp
    const today = currentDateInputValue();
    // Set the max attribute to today's date
    document.getElementById('toTimestamp').setAttribute('max', today);

    // Event delegation for dynamically added row checkboxes
    tableBody.addEventListener("change", function (event) {
        if (event.target.name === "locationCheckbox") {
            const allCheckboxes = tableBody.querySelectorAll('input[name="locationCheckbox"]');
            const allChecked = Array.from(allCheckboxes).every(cb => cb.checked);
            const anyChecked = Array.from(allCheckboxes).some(cb => cb.checked);

            // Update the "Select All" checkbox state
            selectAllCheckbox.checked = allChecked;
            selectAllCheckbox.indeterminate = !allChecked && anyChecked;
        }
    });

    // "Select All" toggle for all row checkboxes
    selectAllCheckbox.addEventListener("change", function () {
        const isChecked = selectAllCheckbox.checked;
        const allCheckboxes = tableBody.querySelectorAll('input[name="locationCheckbox"]');

        allCheckboxes.forEach(checkbox => {
            checkbox.checked = isChecked;
        });
    });

    // Toggle advanced search panel visibility
    document.getElementById('searchToggleBtn').addEventListener('click', () => {
        const advancedSearchPanel = document.getElementById('advancedSearchPanel');
        isSearchPanelOpen = (advancedSearchPanel.style.display === 'none' ? 'block' : 'none') === 'block';
        advancedSearchPanel.style.display = advancedSearchPanel.style.display === 'none' ? 'block' : 'none';
    });

    // Clear search form and reload all locations
    document.getElementById('clearSearchForm').addEventListener('click', () => {
        document.getElementById('advancedSearchForm').reset();
        getUserLocations();
    });

    // Handle search form submission
    document.getElementById('advancedSearchForm').addEventListener('submit', (e) => {
        e.preventDefault();
        const searchParams = {
            fromTimestamp: document.getElementById('fromTimestamp').value,
            toTimestamp: document.getElementById('toTimestamp').value,
            latitude: document.getElementById('latitude').value,
            longitude: document.getElementById('longitude').value,
            activity: document.getElementById('activity').value,
            address: document.getElementById('address').value,
            country: document.getElementById('country').value,
            place: document.getElementById('place').value,
            region: document.getElementById('region').value,
            notes: document.getElementById('notes').value,
        };

        // Fetch filtered locations
        fetchFilteredLocations(mapContainer, 1, 20, searchParams);
    });

    // delegate the view button click event
    document.querySelector('#locationsTable').addEventListener('click', (event) => {
        if (event.target.matches('.view-location')) {
            event.preventDefault();
            const locationId = parseInt(event.target.getAttribute('data-id'), 10);
            viewLocationDetails(locationId);
        }
    });


    // delete events from pop ups
    document.addEventListener("click", async function (event) {
        const deleteLink = event.target.closest(".delete-location-from-popup");
        if (!deleteLink) return; // Exit if the click is not on a delete button

        event.preventDefault(); // Prevent the default link behavior

        const locationIdRaw = deleteLink.getAttribute("data-location-id");
        // Ensure API receives numeric identifier (BulkDeleteRequest expects List<int>).
        const locationId = Number.parseInt(locationIdRaw, 10);
        if (Number.isNaN(locationId)) return; // Exit if there's no valid location ID

        // parent modal showing the location
        const modalElement = document.querySelector(".modal.show");
        const modalInstance = bootstrap.Modal.getInstance(modalElement);

        confirmLocationHistoryDeletion(locationId, () => {
            modalInstance?.hide();
            mapContainer = initializeMap();
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
            // Initialize TomSelect for activity editor if present
            const activityEditor = modalEl.querySelector('.activity-editor');
            if (activityEditor) {
                const locationId = activityEditor.dataset.locationId;
                initActivitySelect(locationId);
            }
        });
    }

    // Set up event delegation for activity editor save/clear buttons.
    setupActivityEditorEvents('#modalContent', syncLocationActivity, { protectLanguage: true });
    setupActivityEditorEvents('#locationsTable', syncLocationActivity, { protectLanguage: true });

    renderLocationStats();
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
        zoomControl: false,
        zoomAnimation: true
    }).setView(initialCenter, zoomLevel);
    createTileLayer().addTo(mapContainer);

    installLocationMapControls(mapContainer);

    addZoomLevelControl(mapContainer, { protectLanguage: true });

    if (!highlightLayer) {
        highlightLayer = L.featureGroup();
    } else {
        highlightLayer.clearLayers();
    }
    highlightLayer.addTo(mapContainer);
    return mapContainer;
};

// Fetch filtered location data from the API (with search parameters)
const fetchFilteredLocations = (mapContainer, page = 1, pageSize = 20, searchParams = {}) => {
    locations = [];
    let url = `/api/Location/search?page=${page}&pageSize=${pageSize}`;

    // Add search parameters to the URL
    if (Object.keys(searchParams).length > 0) {
        const queryString = new URLSearchParams(searchParams).toString();
        url = `${url}&${queryString}`;
    }

    fetch(url, {
        method: 'GET',
        credentials: 'include'
    })
        .then(readLocationHistory)
        .then(data => {
            document.getElementById('location-read-status').textContent = '';
            locations = data.data ?? []; // Store the fetched locations in the global variable
            if (locations && locations.length > 0) {
                displayLocationsOnMap(mapContainer, locations);
            } else {
                mapContainer.setView([20, 0], 2); // Set the mapContainer to a default view if no locations exist
            }
            // Update the table and pagination regardless of whether locations exist
            displayLocationsInTable(locations);
            updatePagination(data.TotalItems, page, pageSize);
        })
        .catch(error => {
            console.error(error);
            document.getElementById('location-read-status').textContent = locationText('LoadFailed', 'Could not load locations.');
        });
}

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
                    generateLocationModalContent(location, { isLive: isLiveM, isLatest: isLatestM });

                new bootstrap.Modal(document.getElementById('locationModal')).show();
            });
        };

        if (isHighlight && highlightLayer) {
            const icon = highlightCandidate?.type === 'live' ? liveMarker : latestLocationMarker;
            const highlightMarker = L.marker(coords, { icon, alt: locationText('LatestMarker', "User's latest location.") });
            if (highlightCandidate?.type === 'latest') {
                highlightMarker.bindTooltip(locationHtml('LatestMarker', "User's latest location."), {
                    direction: "top",
                    offset: [0, -25]
                });
            }
            bindInteractions(highlightMarker);
            highlightMarker.addTo(highlightLayer);
            highlightMarker.setZIndexOffset(1000);
        } else {
            const marker = L.marker(coords, { alt: locationText('Details', 'Location Details') });
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

// Generate the content for the modal when a marker is clicked
const generateLocationModalContent = location => {
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
    const timestamps = locationTimes(location);
    const isLive = isLiveLocation(location);
    const isLatest = !!location.isLatestLocation;
    const badge = isLive
        ? `<span class="badge bg-danger float-end ms-2">${locationHtml('Live', 'LIVE LOCATION')}</span>`
        : (isLatest ? `<span class="badge bg-success float-end ms-2">${locationHtml('Latest', 'LATEST LOCATION')}</span>` : '');
    const sourceZone = sourceTimeZone(location);
    const recordedTime = timestamps.source
        ? `<div>${encode(timestamps.source)}</div>`
        : `<div class="fst-italic text-muted">${locationHtml('SourceZoneUnavailable', 'Source timezone unavailable')}</div>`;
    return `<div class="container-fluid">
        ${badge ? `<div class="row mb-2"><div class="col-12">${badge}</div></div>` : ''}
        <div class="row mb-2">
            <div class="col-6">
                <strong>${locationHtml('ViewerTime', 'Datetime (your timezone)')}:</strong>
                <div>${timestamps.viewer}</div>
            </div>
            <div class="col-6">
                <strong>${locationHtml('RecordedTime', 'Recorded local time')}:</strong>
                ${recordedTime}
                ${sourceZone && !timestamps.source ? `<div class="small text-muted">${encode(sourceZone)}</div>` : ''}
            </div>
        </div>
        <div class="row mb-2">
            <div class="col-12"><strong>${locationHtml('Coordinates', 'Coordinates')}:</strong></div>
            <div class="col-6">
                <p class="mb-0">${locationHtml('Latitude', 'Latitude')}: <span class="fw-bold text-primary">${displayCoordinate(location.coordinates.latitude)}</span></p>
            </div>
            <div class="col-6">
                <p class="mb-0">${locationHtml('Longitude', 'Longitude')}: <span class="fw-bold text-primary">${displayCoordinate(location.coordinates.longitude)}</span></p>
            </div>
        </div>
        <div class="row mb-2">
            <div class="col-6">${generateActivityEditorHtml(location)}</div>
            <div class="col-6"><strong>${locationHtml('Altitude', 'Altitude')}:</strong> <span>${measurement(location.altitude, 'Altitude', 'Altitude', ' m')}</span></div>
        </div>
        <div class="row mb-2">
            <div class="col-6"><strong>${locationHtml('Accuracy', 'Accuracy')}:</strong> <span>${measurement(location.accuracy, 'Accuracy', 'Accuracy', ' m')}</span></div>
            <div class="col-6"><strong>${locationHtml('Speed', 'Speed')}:</strong> <span>${measurement(location.speed, 'Speed', 'Speed', ' km/h')}</span></div>
        </div>
        <div class="row mb-2">
            ${renderLocationAddress(location)}<div class="col-12">
            <br/>
            ${locationMapsLink(location)}
            ${generateWikipediaLinkHtml(location, { query: location.place || location.fullAddress })}
            </div>
        </div>
        <div class="row mb-2">
            <div class="col-12" style="${style}"><strong>${encode(message('Shell.Notes', 'Notes'))}:</strong>
                <div class="border p-1">
                    ${notesHtmlForDisplay(location.notes)}
                </div>
            </div>
        </div>
        <div class="row">
            <div class="col-5">
                <a href="${buildEditUrl(location.id)}" class="btn-link" title="${locationHtml('EditAction', 'Edit location')}">${encode(message('Shell.Edit', 'Edit'))}</a>
            </div>
            <div class="col-5 offset-2">
                <a href="#" class="btn-link text-danger delete-location-from-popup" data-location-id="${location.id}" title="${locationHtml('DeleteAction', 'Delete location')}">${encode(message('Shell.Delete', 'Delete'))}</a>
            </div>
        </div>
    </div>
`
};



// Display locations in the table
const displayLocationsInTable = (locations) => {
    const tableBody = document.querySelector('#locationsTable tbody');
    tableBody.innerHTML = ''; // Clear existing rows

    if (locations.length === 0) {
        const row = document.createElement('tr');
        row.innerHTML = `<td colspan="9" class="text-center">${locationHtml('NoMatches', 'No locations match the criteria.')}</td>`;
        tableBody.appendChild(row);
    } else {
        locations.forEach(location => {
            const row = document.createElement('tr');
            const timestamps = locationTimes(location);
            const sourceZone = sourceTimeZone(location);
            const recordedTime = timestamps.source
                ? `<div class="small text-muted">${locationHtml('Recorded', 'Recorded: {0}', timestamps.source)}</div>`
                : `<div class="small text-muted fst-italic">${locationHtml('RecordedZoneUnavailable', 'Recorded timezone unavailable')}</div>`;
            const recordedZone = sourceZone ? `<div class="small text-muted">${encode(sourceZone)}</div>` : '';
            // Embed the inline activity editor for table rows.
            const activityEditorHtml = generateActivityEditorHtml(location, { showLabel: false, compact: true });
            row.innerHTML = `
                <td>
                    <input type="checkbox" name="locationCheckbox" aria-label="${encode(message('Shell.SelectRow', 'Select row'))}" value="${location.id}" />
                </td>
                <td>
                    <div><i class="bi bi-clock me-1"></i>${timestamps.viewer}</div>
                    ${recordedTime}
                    ${recordedZone}
                </td>
                <td>${displayCoordinate(location.coordinates.latitude)}</td>
                <td>${displayCoordinate(location.coordinates.longitude)}</td>
                <td class="text-center">${measurement(location.accuracy, 'Accuracy', 'Accuracy')}</td>
                <td class="text-center">${measurement(location.speed, 'Speed', 'Speed')}</td>
                <td class="text-center">${measurement(location.altitude, 'Altitude', 'Altitude')}</td>
                <td>${activityEditorHtml}</td>
                <td>${renderLocationAddress(location, location.address)}</td>
                <td>${renderAddressComponent(location.place, locationText("Place", "Place"))}</td>
                <td>${renderAddressComponent(location.country, locationText("Country", "Country"))}</td>
                <td>
                    <a href="#" class="btn btn-primary btn-sm view-location"  data-id="${location.id}">${encode(message('Shell.View', 'View'))}</a>
                    <a href="${buildEditUrl(location.id)}" class="btn btn-secondary btn-sm">${encode(message('Shell.Edit', 'Edit'))}</a>
                </td>
            `;
            tableBody.appendChild(row);
        });
    }
};

// View location details in a modal
const viewLocationDetails = (locationId) => {
    const location = locations.find(loc => loc.id === locationId); // Use the global locations array
    const modalContent = generateLocationModalContent(location);

    document.getElementById('modalContent').innerHTML = modalContent;
    const myModal = new bootstrap.Modal(document.getElementById('locationModal'));
    myModal.show();
}

// Update pagination controls
const updatePagination = (totalItems, currentPage, pageSize) => {
    const totalPages = Math.ceil(totalItems / pageSize);
    let paginationHtml = '';

    for (let i = 1; i <= totalPages; i++) {
        paginationHtml += `<a href="#" onclick="getUserLocations(mapContainer, ${i}, ${pageSize})" class="btn btn-sm ${i === currentPage ? 'btn-primary' : 'btn-light'}">${number(i)}</a>`;
    }

    // Safely update pagination
    const paginationElement = document.getElementById('pagination');
    if (paginationElement) {
        paginationElement.innerHTML = paginationHtml;
    } else {
        console.error("Pagination element not found");
    }
}


bindLocationBulkDeletion(() => {
    mapContainer = initializeMap();
    getUserLocations();
});

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
        method: "POST",
        credentials: 'include',
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
            document.getElementById('location-read-status').textContent = '';
            locations = data.data ?? []; // Store the fetched locations in the global variable
            if (locations && locations.length > 0) {
                displayLocationsOnMap(mapContainer, locations);
            }
            displayLocationsInTable(locations);
        })
        .catch(error => {
            console.error(error);
            document.getElementById('location-read-status').textContent = locationText('LoadFailed', 'Could not load locations.');
        });
}

const debouncedGetUserLocations = debounce(getUserLocations, 350);
