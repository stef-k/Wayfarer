let locations = []; // Declare locations as a global variable
let mapContainer = null;
let zoomLevel = 3;
let mapBounds = null;
let maxClusterRadius = 50;
let markerClusterGroup = null;
let stream;
const tilesUrl = `${window.location.origin}/Public/tiles/{z}/{x}/{y}.png`;
import {addZoomLevelControl, latestLocationMarker, liveMarker} from '../../../map-utils.js';

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

    // delete events from pop ups
    document.addEventListener("click", function (event) {
        const deleteLink = event.target.closest(".delete-location-from-popup");
        if (!deleteLink) return; // Exit if the click is not on a delete button

        event.preventDefault(); // Prevent the default link behavior

        const locationId = deleteLink.getAttribute("data-location-id");
        if (!locationId) return; // Exit if there's no valid location ID

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
        maxZoom: 19, attribution: '© OpenStreetMap contributors'
    }).addTo(mapContainer);

    mapContainer.attributionControl.setPrefix('&copy; <a href="https://leafletjs.com/" target="_blank">Leaflet</a>');

    addZoomLevelControl(mapContainer);

    return mapContainer;
};

// Display locations on the mapContainer with markers
const displayLocationsOnMap = (mapContainer, locations) => {
    if (!mapContainer) {
        mapContainer = initializeMap();
    }

    // Clear all existing markers
    mapContainer.eachLayer(layer => {
        if (layer instanceof L.Marker || layer instanceof L.MarkerClusterGroup) {
            mapContainer.removeLayer(layer);
        }
    });

    // Build a fresh cluster group, with built-in zoom threshold
    markerClusterGroup = L.markerClusterGroup({
        maxClusterRadius: 25,        // clusters above zoom 5 use a 25px radius
        chunkedLoading: true         // break work into small batches
    });


    // Add each location into the cluster
    locations.forEach(location => {
        const coords = [location.coordinates.latitude, location.coordinates.longitude];

        // Decide which icon to use _now_ for the marker itself:
        const nowMin = Math.floor(Date.now() / 60000);
        const locMin = Math.floor(new Date(location.localTimestamp).getTime() / 60000);
        const isLiveIcon = (nowMin - locMin) <= location.locationTimeThresholdMinutes;
        const isLatestIcon = location.isLatestLocation;

        let markerOptions = {};
        if (isLiveIcon) {
            markerOptions.icon = liveMarker;
        } else if (isLatestIcon) {
            markerOptions.icon = latestLocationMarker;
        }
        const marker = L.marker(coords, markerOptions);

        // Tooltip for latest only (live already has its icon + tooltip in your previous code)
        if (isLatestIcon && !isLiveIcon) {
            marker.bindTooltip("User's latest location.", {
                direction: "top",
                offset: [0, -25]
            });
        }

        marker.on('click', () => {
            // 1) recompute “live” for the modal badge
            const now2 = Math.floor(Date.now() / 60000);
            const loc2 = Math.floor(new Date(location.localTimestamp).getTime() / 60000);
            const isLiveM = (now2 - loc2) <= location.locationTimeThresholdMinutes;

            // 2) grab the latest-flag from your DTO
            const isLatestM = location.isLatestLocation;

            // 3) generate & show
            document.getElementById('modalContent').innerHTML =
                generateLocationModalContent(location, {isLive: isLiveM, isLatest: isLatestM});
            new bootstrap.Modal(document.getElementById('locationModal')).show();
        });

        markerClusterGroup.addLayer(marker);
    });

    // **NO** fitBounds or recentering here any more:
    mapContainer.addLayer(markerClusterGroup);
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

    return `<div class="container-fluid">
        <div class="row mb-2">
            <div class="col-12">
                ${badge}
            </div>
        </div>
        <div class="row mb-2">
            <div class="col-6"><strong>Local Datetime:</strong> <span>${new Date(location.localTimestamp).toISOString().replace('T', ' ').split('.')[0]}</span></div>
            <div class="col-6"><strong>Timezone:</strong> <span>${location.timezone || location.timeZoneId}</span></div>
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
             <div class="col-6"><strong>Altitude:</strong> <span>${location.altitude || '<i class="bi bi-patch-question" title="No available data for Altitude"></i>'}</span></div>
        </div>
        <div class="row mb-2">
            <div class="col-12"><strong>Address:</strong> <span>${location.fullAddress || '<i class="bi bi-patch-question" title="No available data for Address"></i> '}</span><br/>
            ${generateGoogleMapsLink(location.fullAddress)}
            ${generateWikipediaLink(location)}
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
                <a href="/User/Location/Edit/${location.id}" class="btn-link" title="Edit location">Edit</a>
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
export const generateGoogleMapsLink = address => {
    const q = encodeURIComponent(address || '');
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
                modifiers: [{
                    name: 'zIndex', options: {value: 2000}  // must exceed Bootstrap modal (1050)
                }]
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
                    action: 'query', list: 'geosearch', gscoord: `${lat}|${lon}`, gsradius: 100,      // meters
                    gslimit: 5, format: 'json', origin: '*'
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
    });
};

const onZoomOrMoveChanges = () => {
    mapContainer.on("moveend zoomend", () => {
        let z = mapContainer.getZoom();
        if (z !== zoomLevel) {
            zoomLevel = z;
        }
        if (z <= 5) {
            markerClusterGroup.disableClustering();
        } else {
            markerClusterGroup.enableClustering();
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
        summaryParts.push(`<strong>From Date:</strong> ${new Date(stats.fromDate).toISOString().split('T')[0]}`);
    if (stats.toDate)
        summaryParts.push(`<strong>To Date:</strong> ${new Date(stats.toDate).toISOString().split('T')[0]}`);
    if (stats.countriesVisited != null)
        summaryParts.push(`<strong>Countries:</strong> ${stats.countriesVisited}`);
    if (stats.regionsVisited != null)
        summaryParts.push(`<strong>Regions:</strong> ${stats.regionsVisited}`);
    if (stats.citiesVisited != null)
        summaryParts.push(`<strong>Cities:</strong> ${stats.citiesVisited}`);

    const summary = summaryParts.join(" | ");
    document.getElementById("timeline-summary").innerHTML = summary;
};

const handleStream = (event) => {
    getUserLocations();
};