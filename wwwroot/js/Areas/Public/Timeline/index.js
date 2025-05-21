let locations = []; // Declare locations as a global variable
let mapContainer = null;
let zoomLevel = 3;
let mapBounds = null;
let markerClusterGroup = null;
let username = null;
// const tilesUrl = `https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png`;
const tilesUrl = `${window.location.origin}/Public/tiles/{z}/{x}/{y}.png`;
let timelineLive;
let stream;

import {addZoomLevelControl, latestLocationMarker, liveMarker} from '../../../map-utils.js';

document.addEventListener('DOMContentLoaded', () => {
    username = document.getElementById('username').dataset.username;
    timelineLive = document.getElementById('timelineLive').dataset.timelineLive;
    
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
    zoomLevel = mapContainer.getZoom();
    getUserLocations();
    onZoomOrMoveChanges();

    // handle the SSE stream
    stream.onmessage = (event) => {
        handleStream(event);
    }
    
    // Wire up the Bootstrap “shown” event for Wikipedia hover cards
    const modalEl = document.getElementById('locationModal');
    if (!modalEl) {
        console.error('Modal element not found!');
    } else {
        modalEl.addEventListener('shown.bs.modal', () => {
            initWikipediaPopovers(modalEl);
        });
    }
    
    getUserStats(username);
    
});

const handleStream = (event) => {
    if(timelineLive) {
        getUserLocations();
    }
};

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
    }).setView([20, 0], zoomLevel);
    L.tileLayer(tilesUrl, {
        maxZoom: 19,
        attribution: '© OpenStreetMap contributors'
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

    // Clear all markers from the map before adding new ones
    mapContainer.eachLayer(layer => {
        if (layer instanceof L.Marker || layer instanceof L.MarkerClusterGroup) {
            mapContainer.removeLayer(layer);
        }
    });

    // Create a fresh marker cluster group
    markerClusterGroup = L.markerClusterGroup({
        maxClusterRadius: dynamicClustering(),
    });

    // We'll still compute bounds, but we no longer auto-fit them.
    const bounds = L.latLngBounds();

    locations.forEach(location => {
        const modalContent = generateLocationModalContent(location);
        const coords = [location.coordinates.latitude, location.coordinates.longitude];

        // Compute “live” vs “latest”:
        const nowMin = Math.floor(Date.now() / 60000);
        const locMin = Math.floor(new Date(location.localTimestamp).getTime() / 60000);
        const isLive = (nowMin - locMin) <= location.locationTimeThresholdMinutes;

        let marker;
        if (isLive) {
            marker = L.marker(coords, {icon: liveMarker})
                .bindTooltip("User's real time location!", {direction: "top", offset: [0, -25]});
        } else if (location.isLatestLocation) {
            marker = L.marker(coords, {icon: latestLocationMarker})
                .bindTooltip("User's latest location.", {direction: "top", offset: [0, -25]});
        } else {
            marker = L.marker(coords);
        }

        marker.on('click', () => {
            document.getElementById('modalContent').innerHTML = modalContent;
            new bootstrap.Modal(document.getElementById('locationModal')).show();
        });

        markerClusterGroup.addLayer(marker);
        bounds.extend(coords);
    });

    // Add the cluster group—BUT do NOT fit or recenter the map.
    mapContainer.addLayer(markerClusterGroup);
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
    return `<div class="container-fluid">
        <div class="row mb-2">
            <div class="col-6"><strong>Local Datetime:</strong> <span>${new Date(location.localTimestamp).toISOString().replace('T', ' ').split('.')[0]}</span></div>
            <div class="col-6"><strong>Timezone:</strong> <span>${location.timezone}</span></div>
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
            <div class="col-6"><strong>Activity:</strong> <span>${location.activityType} </span></div>
            <div class="col-6"><strong>Altitude:</strong> <span>${location.altitude || 'Not provided'}</span></div>
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
    </div>
`
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
      class="ms-2"
      title="View in Google Maps"
    >📍 Maps</a>
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
      class="ms-2 wikipedia-link"
      data-lat="${latitude}"
      data-lon="${longitude}"
    >📖 Wiki</a>
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

const dynamicClustering = (level) => {
    if (zoomLevel <= 5) {
        return 15;
    } else {
        return 5;
    }
}

const onZoomOrMoveChanges = () => {
    mapContainer.on("moveend zoomend", () => {
        let z = mapContainer.getZoom();
        if (z !== zoomLevel) {
            zoomLevel = z;
            if (markerClusterGroup) {
                markerClusterGroup.options.maxClusterRadius = dynamicClustering(zoomLevel);
            }
        }
        mapBounds = mapContainer.getBounds();
        zoomLevel = mapContainer.getZoom();
        debouncedGetUserLocations();
    });
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
    document.getElementById("timeline-summary").innerHTML  = summary;
};