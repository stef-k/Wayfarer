let locations = []; // Declare locations as a global variable
let mapContainer = null;
let zoomLevel = 2;
let mapBounds = null;
let markerClusterGroup = null;
let isSearchPanelOpen = false;
let markerLayer, clusterLayer, highlightLayer;
const tilesUrl = `${window.location.origin}/Public/tiles/{z}/{x}/{y}.png`;
import {addZoomLevelControl, latestLocationMarker, liveMarker} from '../../../map-utils.js';
import {
    formatViewerAndSourceTimes,
    formatDate,
    currentDateInputValue,
    getViewerTimeZone,
} from '../../../util/datetime.js';

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
    document.addEventListener("click", function (event) {
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

        showConfirmationModal({
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

    // Wire up the Bootstrap “shown” event for Wikipedia hover cards
    const modalEl = document.getElementById('locationModal');
    if (!modalEl) {
        console.error('Modal element not found!');
    } else {
        modalEl.addEventListener('shown.bs.modal', () => {
            initWikipediaPopovers(modalEl);
        });
    }

    getUserStats();
});

/**
 * Sets up Map, Table, and Pagination and may be
 * used for initial or after data updates.
 */

// Initialize mapContainer with OpenStreetMap layer
const initializeMap = () => {
    if (mapContainer !== undefined && mapContainer !== null) {
        mapContainer.off();
        mapContainer.remove();
    }
    mapContainer = L.map('mapContainer', {
        zoomAnimation: true
    }).setView(initialCenter, zoomLevel);
    L.tileLayer(tilesUrl, {
        maxZoom: 19,
        attribution: '© OpenStreetMap contributors'
    }).addTo(mapContainer);

    mapContainer.attributionControl.setPrefix('&copy; <a href="https://wayfarer.stefk.me" title="Powered by Wayfarer, made by Stef" target="_blank">Wayfarer</a> | <a href="https://stefk.me" title="Check my blog" target="_blank">Stef K</a> | &copy; <a href="https://leafletjs.com/" target="_blank">Leaflet</a>');

    addZoomLevelControl(mapContainer);

    if (!highlightLayer) {
        highlightLayer = L.layerGroup();
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
        .then(response => response.json())
        .then(data => {
            locations = data.data; // Store the fetched locations in the global variable
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
            console.error("Error fetching filtered location data:", error);
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

    const nowMinGlobal = Math.floor(Date.now() / 60000);
    const thresholdFor = (location) => location.locationTimeThresholdMinutes ?? 10;

    let liveCandidate = null;
    let latestCandidate = null;

    locations.forEach(location => {
        const locMin = Math.floor(new Date(location.localTimestamp).getTime() / 60000);
        const isLive = (nowMinGlobal - locMin) <= thresholdFor(location);

        if (isLive) {
            if (!liveCandidate || locMin > liveCandidate.locMin) {
                liveCandidate = { location, locMin };
            }
        } else if (location.isLatestLocation) {
            if (!latestCandidate || locMin > latestCandidate.locMin) {
                latestCandidate = { location, locMin };
            }
        }
    });

    const highlightCandidate = liveCandidate
        ? { location: liveCandidate.location, type: 'live' }
        : latestCandidate
            ? { location: latestCandidate.location, type: 'latest' }
            : null;
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
    }
};// Display locations on the mapContainer with markers
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
    const timestamps = getLocationTimestampInfo(location);
    const nowMinutes = Math.floor(Date.now() / 60000);
    const locationMinutes = Math.floor(new Date(location.localTimestamp).getTime() / 60000);
    const isLive = (nowMinutes - locationMinutes) <= (location.locationTimeThresholdMinutes || 10);
    const isLatest = !!location.isLatestLocation;
    const badge = isLive
        ? '<span class="badge bg-danger float-end ms-2">LIVE LOCATION</span>'
        : (isLatest ? '<span class="badge bg-success float-end ms-2">LATEST LOCATION</span>' : '');
    const sourceZone = getLocationSourceTimeZone(location);
    const recordedTime = timestamps.source
        ? `<div>${timestamps.source}</div>`
        : `<div class="fst-italic text-muted">Source timezone unavailable</div>`;
    return `<div class="container-fluid">
        ${badge ? `<div class="row mb-2"><div class="col-12">${badge}</div></div>` : ''}
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
            <div class="col-6"><strong>Activity:</strong>   <span>${
        (location.activityType && location.activityType !== 'Unknown')
            ? location.activityType
            : '<i class="bi bi-patch-question" title="No available data for Activity"></i>'
    }</span></div>
            <div class="col-6"><strong>Altitude:</strong> <span>${location.altitude || '<i class="bi bi-patch-question" title="No available data for Altitude"></i>'}</span></div>
        </div>
        <div class="row mb-2">
            <div class="col-12"><strong>Address:</strong> <span>${location.fullAddress || '<i class="bi bi-patch-question" title="No available data for Address"></i> '}</span>
            <br/>
            ${generateGoogleMapsLink(location)}
            ${generateWikipediaLink(location)}
            </div>
        </div>
        <div class="row mb-2">
            <div class="col-12" style="${style}"><strong>Notes:</strong>
                <div class="border p-1">
                    ${location.notes}
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
`
};

/**
 * Generates a Google Maps link combining address and coordinates for precision.
 * @param {{ fullAddress?: string, coordinates: { latitude: number, longitude: number } }} location
 */
export const generateGoogleMapsLink = location => {
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

/**
 * Generates a link for Wikipedia
 * @param {object} location
 * @param {{ latitude: number, longitude: number }} location.coordinates
 */
const generateWikipediaLink = location => {
    const {latitude, longitude} = location.coordinates;
    return `
    <a
      href="#"
      class="ms-2 wikipedia-link btn btn-outline-primary btn-sm"
      data-lat="${latitude}"
      data-lon="${longitude}"
    ><i class="bi bi-wikipedia"></i> Wiki</a>
  `;
};


/**
 * Cretes a pop over with Wikipedia content about the place IF and article exists based on the coordinates of the location
 * @param {HTMLElement} modalEl  — the actual <div id="locationModal"> element
 */
const initWikipediaPopovers = modalEl => {
    modalEl.querySelectorAll('.wikipedia-link').forEach(el => {
        tippy(el, {
            appendTo: () => document.body,
            popperOptions: {
                modifiers: [
                    {
                        name: 'zIndex',
                        options: {value: 2000}  // must exceed Bootstrap modal (1050)
                    }
                ]
            },
            interactiveBorder: 20,
            content: 'Loading…',
            allowHTML: true,
            interactive: true,
            hideOnClick: false,
            placement: 'right',
            onShow: async instance => {
                if (instance._loaded) return;
                instance._loaded = true;

                const lat = el.getAttribute('data-lat');
                const lon = el.getAttribute('data-lon');

                // 1) GeoSearch for nearby pages
                const geoUrl = new URL('https://en.wikipedia.org/w/api.php');
                geoUrl.search = new URLSearchParams({
                    action: 'query',
                    list: 'geosearch',
                    gscoord: `${lat}|${lon}`,
                    gsradius: 100,      // meters
                    gslimit: 5,
                    format: 'json',
                    origin: '*'
                }).toString();

                try {
                    const geoRes = await fetch(geoUrl);
                    if (!geoRes.ok) throw new Error(`GeoSearch HTTP ${geoRes.status}`);
                    const geoJson = await geoRes.json();
                    const results = geoJson.query?.geosearch || [];

                    if (!results.length) {
                        instance.setContent(`
              <div style="max-width:250px">
                <em>No nearby Wikipedia article found.</em>
              </div>
            `);
                        return;
                    }

                    // 2) Fetch summary of the top hit
                    const title = encodeURIComponent(results[0].title);
                    const summaryUrl = `https://en.wikipedia.org/api/rest_v1/page/summary/${title}`;
                    const sumRes = await fetch(summaryUrl);
                    if (!sumRes.ok) throw new Error(`Summary HTTP ${sumRes.status}`);
                    const data = await sumRes.json();

                    instance.setContent(`
            <div style="max-width:250px">
              <strong>${data.title}</strong>
              <p>${data.extract}</p>
              <a href="${data.content_urls.desktop.page}" target="_blank">
                Read more »
              </a>
            </div>
          `);
                } catch (err) {
                    instance.setContent(`
            <div style="max-width:250px">
              <em>Could not load article.</em>
            </div>
          `);
                    console.debug('Wiki popover error:', err);
                }
            }
        });
    })
    ;
};


// Display locations in the table
const displayLocationsInTable = (locations) => {
    const tableBody = document.querySelector('#locationsTable tbody');
    tableBody.innerHTML = ''; // Clear existing rows

    if (locations.length === 0) {
        const row = document.createElement('tr');
        row.innerHTML = `<td colspan="9" class="text-center">No locations match the search criteria.</td>`;
        tableBody.appendChild(row);
    } else {
        locations.forEach(location => {
            const row = document.createElement('tr');
            const timestamps = getLocationTimestampInfo(location);
            const sourceZone = getLocationSourceTimeZone(location);
            const recordedTime = timestamps.source
                ? `<div class="small text-muted">Recorded: ${timestamps.source}</div>`
                : `<div class="small text-muted fst-italic">Recorded timezone unavailable</div>`;
            const recordedZone = sourceZone ? `<div class="small text-muted">${sourceZone}</div>` : '';
            row.innerHTML = `
                <td>
                    <input type="checkbox" name="locationCheckbox" value="${location.id}" />
                </td>
                <td>
                    <div><i class="bi bi-clock me-1"></i>${timestamps.viewer}</div>
                    ${recordedTime}
                    ${recordedZone}
                </td>
                <td>${location.coordinates.latitude}</td>
                <td>${location.coordinates.longitude}</td>
                <td class="text-center">${location.accuracy || '<i class="bi bi-patch-question" title="No available data for Accuracy"></i>'}</td>
                <td class="text-center">${location.altitude || '<i class="bi bi-patch-question" title="No available data for Altitude"></i>'}</td>
                <td>${location.activityType || '<i class="bi bi-patch-question" title="No available data for Activity"></i>'}</td>
                <td>${location.address || '<i class="bi bi-patch-question" title="No available data for Address"></i>'}</td>
                <td>${location.place || '<i class="bi bi-patch-question" title="No available data for Place"></i>'}</td>
                <td>${location.country || '<i class="bi bi-patch-question" title="No available data for Country"></i>'}</td>
                <td>
                    <a href="#" class="btn btn-primary btn-sm view-location"  data-id="${location.id}">View</a>
                    <a href="${buildEditUrl(location.id)}" class="btn btn-secondary btn-sm">Edit</a>
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
        paginationHtml += `<a href="#" onclick="getUserLocations(mapContainer, ${i}, ${pageSize})" class="btn btn-sm ${i === currentPage ? 'btn-primary' : 'btn-light'}">${i}</a>`;
    }

    // Safely update pagination
    const paginationElement = document.getElementById('pagination');
    if (paginationElement) {
        paginationElement.innerHTML = paginationHtml;
    } else {
        console.error("Pagination element not found");
    }
}


document.getElementById('deleteSelected').addEventListener('click', () => {
    const selectedIds = [];
    document.querySelectorAll('input[name="locationCheckbox"]:checked').forEach(checkbox => {
        const parsedId = Number.parseInt(checkbox.value, 10);
        if (!Number.isNaN(parsedId)) {
            // Keep payload numeric for the API's List<int> binder.
            selectedIds.push(parsedId);
        }
    });

    if (selectedIds.length > 0) {
        // Use the showConfirmationModal helper to confirm deletion
        showConfirmationModal({
            title: 'Confirm Deletion',
            message: 'Are you sure you want to delete the selected locations? This action cannot be undone.',
            confirmText: 'Delete',
            onConfirm: () => {
                // Perform the deletion action after confirmation
                fetch('/api/Location/bulk-delete', {
                    method: 'POST',
                    headers: {'Content-Type': 'application/json'},
                    body: JSON.stringify({locationIds: selectedIds})
                })
                    .then(response => response.json())
                    .then(data => {
                        if (data.success) {
                            mapContainer = initializeMap();
                            getUserLocations();
                            fetchLocations(mapContainer);
                        } else {
                            alert('Failed to delete locations');
                        }
                    })
                    .catch(error => console.error('Error:', error));
            }
        });
    } else {
        alert('No locations selected');
    }
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
            locations = data.data; // Store the fetched locations in the global variable
            if (locations && locations.length > 0) {
                displayLocationsOnMap(mapContainer, locations);
                displayLocationsInTable(locations);
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
        summaryParts.push(`<strong>Countries:</strong> ${stats.countriesVisited}`);
    if (stats.regionsVisited != null)
        summaryParts.push(`<strong>Regions:</strong> ${stats.regionsVisited}`);
    if (stats.citiesVisited != null)
        summaryParts.push(`<strong>Cities:</strong> ${stats.citiesVisited}`);

    const summary = summaryParts.join(" | ");
    document.getElementById("timeline-summary").innerHTML  = summary;
};

