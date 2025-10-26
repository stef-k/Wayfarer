// Chronological Timeline - allows navigation by day, month, or year
let locations = [];
let mapContainer = null;
let markerLayer, clusterLayer;
const tilesUrl = `${window.location.origin}/Public/tiles/{z}/{x}/{y}.png`;
import {addZoomLevelControl, latestLocationMarker, liveMarker} from '../../../map-utils.js';
import {
    formatViewerAndSourceTimes,
    formatDate,
    currentDateInputValue,
    currentMonthInputValue,
    currentYearInputValue,
    getViewerTimeZone,
} from '../../../util/datetime.js';

const viewerTimeZone = getViewerTimeZone();
const getLocationSourceTimeZone = location => location?.timezone || location?.timeZoneId || location?.timeZone || null;
const getLocationTimestampInfo = location => formatViewerAndSourceTimes({
    iso: location?.localTimestamp,
    sourceTimeZone: getLocationSourceTimeZone(location),
    viewerTimeZone,
});
// Current view state
let currentDate = new Date();
let currentViewType = 'day'; // 'day', 'month', or 'year'

document.addEventListener('DOMContentLoaded', () => {
    // Initialize the map
    mapContainer = initializeMap();

    // Set today's date as default
    setDateToToday();

    // Load initial data
    loadChronologicalData();

    // Wire up event listeners
    setupEventListeners();

    // Wire up the Bootstrap "shown" event for Wikipedia hover cards
    const modalEl = document.getElementById('locationModal');
    if (!modalEl) {
        console.error('Modal element not found!');
    } else {
        modalEl.addEventListener('shown.bs.modal', () => {
            initWikipediaPopovers(modalEl);
        });
    }

    // Delete events from pop ups
    document.addEventListener("click", function (event) {
        const deleteLink = event.target.closest(".delete-location-from-popup");
        if (!deleteLink) return;

        event.preventDefault();
        const locationId = deleteLink.getAttribute("data-location-id");
        if (!locationId) return;

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
                    body: JSON.stringify({locationIds: [locationId]})
                })
                    .then(response => response.json())
                    .then(data => {
                        if (data.success) {
                            if (modalInstance) {
                                modalInstance.hide();
                            }
                            loadChronologicalData();
                        } else {
                            alert("Failed to delete location");
                        }
                    })
                    .catch(error => console.error("Error:", error));
            }
        });
    });
});

/**
 * Sets up all event listeners for navigation and date selection
 */
const setupEventListeners = () => {
    // Navigation buttons
    document.getElementById('btnPrevYear').addEventListener('click', () => navigateDate(-1, 'year'));
    document.getElementById('btnPrevMonth').addEventListener('click', () => navigateDate(-1, 'month'));
    document.getElementById('btnPrevDay').addEventListener('click', () => navigateDate(-1, 'day'));
    document.getElementById('btnNextDay').addEventListener('click', () => navigateDate(1, 'day'));
    document.getElementById('btnNextMonth').addEventListener('click', () => navigateDate(1, 'month'));
    document.getElementById('btnNextYear').addEventListener('click', () => navigateDate(1, 'year'));

    // Quick navigation
    document.getElementById('btnToday').addEventListener('click', () => {
        setDateToToday();
        loadChronologicalData();
    });

    document.getElementById('btnYesterday').addEventListener('click', () => {
        currentDate.setDate(currentDate.getDate() - 1);
        currentViewType = 'day';
        updateViewTypeUI();
        updateDatePickerValues();
        loadChronologicalData();
    });

    // Date pickers
    document.getElementById('datePicker').addEventListener('change', (e) => {
        const selectedDate = new Date(e.target.value);
        if (!isNaN(selectedDate.getTime())) {
            currentDate = selectedDate;
            currentViewType = 'day';
            updateViewTypeUI();
            loadChronologicalData();
        }
    });

    document.getElementById('monthPicker').addEventListener('change', (e) => {
        const [year, month] = e.target.value.split('-').map(Number);
        currentDate = new Date(year, month - 1, 1);
        currentViewType = 'month';
        updateViewTypeUI();
        loadChronologicalData();
    });

    document.getElementById('yearPicker').addEventListener('change', (e) => {
        const year = parseInt(e.target.value);
        if (!isNaN(year)) {
            currentDate = new Date(year, 0, 1);
            currentViewType = 'year';
            updateViewTypeUI();
            loadChronologicalData();
        }
    });

    // View type selector
    document.querySelectorAll('input[name="viewType"]').forEach(radio => {
        radio.addEventListener('change', (e) => {
            currentViewType = e.target.value;
            updateViewTypeUI();
            loadChronologicalData();
        });
    });
};

/**
 * Navigate date forward or backward by specified unit.
 * Navigation is contextual - maintains current view context:
 * - In month view: year navigation maintains the month (e.g., Oct 2025 → Oct 2024)
 * - In day view: month/year navigation maintains the day if possible
 * Always allows navigation even if target date has no data to prevent users from getting trapped.
 */
const navigateDate = (direction, unit) => {
    const newDate = new Date(currentDate);

    switch (unit) {
        case 'day':
            newDate.setDate(newDate.getDate() + direction);
            // Day navigation always switches to day view
            currentViewType = 'day';
            break;
        case 'month':
            // In day view, try to maintain the day of month when navigating months
            if (currentViewType === 'day') {
                const currentDay = newDate.getDate();
                newDate.setMonth(newDate.getMonth() + direction);
                // Adjust if the day doesn't exist in the new month (e.g., Jan 31 → Feb 28)
                const daysInNewMonth = new Date(newDate.getFullYear(), newDate.getMonth() + 1, 0).getDate();
                if (currentDay > daysInNewMonth) {
                    newDate.setDate(daysInNewMonth);
                }
            } else {
                // In month/year view, just navigate months
                newDate.setMonth(newDate.getMonth() + direction);
                if (currentViewType === 'year') currentViewType = 'month';
            }
            break;
        case 'year':
            // Year navigation maintains month and day context
            newDate.setFullYear(newDate.getFullYear() + direction);
            // Keep the current view type unless we're in day view with an invalid date
            if (currentViewType === 'day') {
                // Check if the day exists in the new year (e.g., Feb 29 in non-leap year)
                const maxDay = new Date(newDate.getFullYear(), newDate.getMonth() + 1, 0).getDate();
                if (newDate.getDate() > maxDay) {
                    newDate.setDate(maxDay);
                }
            }
            break;
    }

    currentDate = newDate;
    updateViewTypeUI();
    updateDatePickerValues();
    loadChronologicalData();
};

/**
 * Set the current date to today
 */
const setDateToToday = () => {
    currentDate = new Date();
    currentViewType = 'day';
    updateViewTypeUI();
    updateDatePickerValues();
};

/**
 * Update the UI based on current view type
 */
const updateViewTypeUI = () => {
    // Update radio buttons
    document.getElementById(`view${currentViewType.charAt(0).toUpperCase() + currentViewType.slice(1)}`).checked = true;

    // Show/hide appropriate date pickers
    document.getElementById('datePicker').style.display = currentViewType === 'day' ? 'block' : 'none';
    document.getElementById('monthPicker').style.display = currentViewType === 'month' ? 'block' : 'none';
    document.getElementById('yearPicker').style.display = currentViewType === 'year' ? 'block' : 'none';

    // Show/hide navigation buttons based on view type
    const showDayNav = currentViewType === 'day';
    const showMonthNav = currentViewType === 'day' || currentViewType === 'month';
    const showYesterday = currentViewType === 'day';

    document.getElementById('btnPrevDay').style.display = showDayNav ? 'inline-block' : 'none';
    document.getElementById('btnNextDay').style.display = showDayNav ? 'inline-block' : 'none';
    document.getElementById('btnPrevMonth').style.display = showMonthNav ? 'inline-block' : 'none';
    document.getElementById('btnNextMonth').style.display = showMonthNav ? 'inline-block' : 'none';
    document.getElementById('btnYesterday').style.display = showYesterday ? 'inline-block' : 'none';

    // Update Yesterday button state - only enabled when viewing today
    updateYesterdayButton();
};

/**
 * Update Yesterday button state - only enabled when viewing today's date
 */
const updateYesterdayButton = () => {
    const today = new Date();
    const isToday = currentDate.getFullYear() === today.getFullYear() &&
                    currentDate.getMonth() === today.getMonth() &&
                    currentDate.getDate() === today.getDate();

    document.getElementById('btnYesterday').disabled = !isToday;
};

/**
 * Update date picker values based on current date
 */
const updateDatePickerValues = () => {
    document.getElementById('datePicker').value = currentDateInputValue(currentDate);
    document.getElementById('monthPicker').value = currentMonthInputValue(currentDate);
    document.getElementById('yearPicker').value = currentYearInputValue(currentDate);
};

/**
 * Load chronological data from the server
 */
const loadChronologicalData = async () => {
    const year = currentDate.getFullYear();
    const month = currentDate.getMonth() + 1;
    const day = currentDate.getDate();

    let url = `/User/Timeline/GetChronologicalData?dateType=${currentViewType}&year=${year}`;

    if (currentViewType === 'month' || currentViewType === 'day') {
        url += `&month=${month}`;
    }

    if (currentViewType === 'day') {
        url += `&day=${day}`;
    }

    try {
        const response = await fetch(url);
        const result = await response.json();

        if (result.success) {
            locations = result.data;
            displayLocationsOnMap(mapContainer, locations);

            // Fetch and display enhanced stats
            await updateEnhancedStats();

            // Check navigation availability
            await updateNavigationButtons();
        } else {
            console.error('Error loading data:', result.message);
        }
    } catch (error) {
        console.error('Error fetching chronological data:', error);
    }
};

/**
 * Update navigation buttons based on data availability and future date restrictions.
 * Handles contextual navigation where year buttons maintain month/day context.
 */
const updateNavigationButtons = async () => {
    const year = currentDate.getFullYear();
    const month = currentDate.getMonth() + 1;
    const day = currentDate.getDate();

    let url = `/User/Timeline/CheckNavigationAvailability?dateType=${currentViewType}&year=${year}`;

    if (currentViewType === 'month' || currentViewType === 'day') {
        url += `&month=${month}`;
    }

    if (currentViewType === 'day') {
        url += `&day=${day}`;
    }

    try {
        const response = await fetch(url);
        const result = await response.json();

        if (result.success) {
            // Update day navigation buttons (only in day view)
            document.getElementById('btnPrevDay').disabled = !result.canNavigatePrevDay;
            document.getElementById('btnNextDay').disabled = !result.canNavigateNextDay;

            // Update month navigation buttons (in day and month views)
            document.getElementById('btnPrevMonth').disabled = !result.canNavigatePrevMonth;
            document.getElementById('btnNextMonth').disabled = !result.canNavigateNextMonth;

            // Update year navigation buttons (always visible)
            document.getElementById('btnPrevYear').disabled = !result.canNavigatePrevYear;
            document.getElementById('btnNextYear').disabled = !result.canNavigateNextYear;
        }
    } catch (error) {
        console.error('Error checking navigation availability:', error);
    }
};

/**
 * Fetch and display enhanced stats (locations, countries, regions, cities)
 */
const updateEnhancedStats = async () => {
    const year = currentDate.getFullYear();
    const month = currentDate.getMonth() + 1;
    const day = currentDate.getDate();

    let url = `/User/Timeline/GetChronologicalStats?dateType=${currentViewType}&year=${year}`;

    if (currentViewType === 'month' || currentViewType === 'day') {
        url += `&month=${month}`;
    }

    if (currentViewType === 'day') {
        url += `&day=${day}`;
    }

    try {
        const response = await fetch(url);
        const result = await response.json();

        if (result.success && result.stats) {
            const stats = result.stats;
            const dateString = formatDateDisplay(currentDate, currentViewType);

            const summaryParts = [];
            summaryParts.push(`<strong>Period:</strong> ${dateString}`);

            if (stats.totalLocations != null)
                summaryParts.push(`<strong>Locations:</strong> ${stats.totalLocations}`);
            if (stats.countriesVisited != null)
                summaryParts.push(`<strong><a href="#" class="text-decoration-none stat-link" data-stat-type="countries">Countries:</a></strong> ${stats.countriesVisited}`);
            if (stats.regionsVisited != null)
                summaryParts.push(`<strong><a href="#" class="text-decoration-none stat-link" data-stat-type="regions">Regions:</a></strong> ${stats.regionsVisited}`);
            if (stats.citiesVisited != null)
                summaryParts.push(`<strong><a href="#" class="text-decoration-none stat-link" data-stat-type="cities">Cities:</a></strong> ${stats.citiesVisited}`);

            const summary = summaryParts.join(" | ");
            document.getElementById('timeline-summary').innerHTML = summary;

            // Add click handlers for stat links
            document.querySelectorAll('.stat-link').forEach(link => {
                link.addEventListener('click', async (e) => {
                    e.preventDefault();
                    const statType = e.currentTarget.getAttribute('data-stat-type');
                    await showDetailedStats(statType);
                });
            });
        }
    } catch (error) {
        console.error('Error fetching stats:', error);
    }
};

/**
 * Fetch and display detailed stats in a modal for chronological view
 * @param {string} statType - Type of stat to highlight (countries, regions, cities)
 */
const showDetailedStats = async (statType) => {
    const year = currentDate.getFullYear();
    const month = currentDate.getMonth() + 1;
    const day = currentDate.getDate();

    let url = `/User/Timeline/GetChronologicalStatsDetailed?dateType=${currentViewType}&year=${year}`;

    if (currentViewType === 'month' || currentViewType === 'day') {
        url += `&month=${month}`;
    }

    if (currentViewType === 'day') {
        url += `&day=${day}`;
    }

    try {
        const response = await fetch(url);
        const result = await response.json();

        if (!result.success) {
            throw new Error(result.message || 'Failed to fetch detailed stats');
        }

        const detailedStats = result.stats;

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
    html += `<p><strong>Period:</strong> ${formatDateDisplay(currentDate, currentViewType)}</p>`;
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

/**
 * Format date for display based on view type
 */
const formatDateDisplay = (date, viewType) => {
    const year = date.getFullYear();
    const monthNames = ['January', 'February', 'March', 'April', 'May', 'June',
        'July', 'August', 'September', 'October', 'November', 'December'];
    const month = monthNames[date.getMonth()];
    const day = date.getDate();

    switch (viewType) {
        case 'day':
            return `${month} ${day}, ${year}`;
        case 'month':
            return `${month} ${year}`;
        case 'year':
            return `${year}`;
        default:
            return '';
    }
};

/**
 * Initialize map with OpenStreetMap layer
 */
const initializeMap = () => {
    if (mapContainer !== undefined && mapContainer !== null) {
        mapContainer.off();
        mapContainer.remove();
    }

    mapContainer = L.map('mapContainer', {
        zoomAnimation: true
    }).setView([20, 0], 3);

    L.tileLayer(tilesUrl, {
        maxZoom: 19,
        attribution: '© OpenStreetMap contributors'
    }).addTo(mapContainer);

    mapContainer.attributionControl.setPrefix('&copy; <a href="https://wayfarer.stefk.me" title="Powered by Wayfarer, made by Stef" target="_blank">Wayfarer</a> | <a href="https://stefk.me" title="Check my blog" target="_blank">Stef K</a> | &copy; <a href="https://leafletjs.com/" target="_blank">Leaflet</a>');
    addZoomLevelControl(mapContainer);

    return mapContainer;
};

/**
 * Build marker layers (flat and clustered)
 */
const buildLayers = (locations) => {
    markerLayer = L.layerGroup();
    clusterLayer = L.markerClusterGroup({
        maxClusterRadius: 50,
        chunkedLoading: true
    });

    locations.forEach(location => {
        const coords = [location.coordinates.latitude, location.coordinates.longitude];

        // Decide icon
        const nowMin = Math.floor(Date.now() / 60000);
        const locMin = Math.floor(new Date(location.localTimestamp).getTime() / 60000);
        const isLiveIcon = (nowMin - locMin) <= location.locationTimeThresholdMinutes;
        const isLatestIcon = location.isLatestLocation;

        const markerOptions = {};
        if (isLiveIcon) markerOptions.icon = liveMarker;
        else if (isLatestIcon) markerOptions.icon = latestLocationMarker;

        const marker = L.marker(coords, markerOptions);

        // Only show "latest" tooltip if not live
        if (isLatestIcon && !isLiveIcon) {
            marker.bindTooltip("User's latest location.", {
                direction: "top",
                offset: [0, -25]
            });
        }

        // Click => fill & show modal
        marker.on('click', () => {
            const now2 = Math.floor(Date.now() / 60000);
            const loc2 = Math.floor(new Date(location.localTimestamp).getTime() / 60000);
            const isLiveM = (now2 - loc2) <= location.locationTimeThresholdMinutes;
            const isLatestM = location.isLatestLocation;

            document.getElementById('modalContent').innerHTML =
                generateLocationModalContent(location, {isLive: isLiveM, isLatest: isLatestM});

            new bootstrap.Modal(document.getElementById('locationModal')).show();
        });

        markerLayer.addLayer(marker);
        clusterLayer.addLayer(marker);
    });

    // Use clustering for better performance
    mapContainer.addLayer(clusterLayer);
};

/**
 * Display locations on the map with markers
 */
const displayLocationsOnMap = (map, locations) => {
    if (!map) {
        map = initializeMap();
    }

    // Remove old layers
    if (markerLayer && map.hasLayer(markerLayer)) {
        map.removeLayer(markerLayer);
    }
    if (clusterLayer && map.hasLayer(clusterLayer)) {
        map.removeLayer(clusterLayer);
    }

    // Build and add new layers
    if (locations && locations.length > 0) {
        buildLayers(locations);

        // Fit bounds to show all locations
        const bounds = locations.map(l => [l.coordinates.latitude, l.coordinates.longitude]);
        if (bounds.length > 0) {
            map.fitBounds(bounds, {padding: [50, 50]});
        }
    } else {
        // No locations, set to default view
        map.setView([20, 0], 3);
    }
};

/**
 * Generate the content for the modal when a marker is clicked
 */
const generateLocationModalContent = (location, {isLive, isLatest}) => {
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
             <div class="col-6"><strong>Altitude:</strong> <span>${location.altitude || '<i class="bi bi-patch-question" title="No available data for Altitude"></i>'}</span></div>
        </div>
        <div class="row mb-2">
            <div class="col-12"><strong>Address:</strong> <span>${location.fullAddress || '<i class="bi bi-patch-question" title="No available data for Address"></i> '}</span><br/>
            ${generateGoogleMapsLink(location)}
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
 */
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
 * Creates a pop over with Wikipedia content about the place IF an article exists based on coordinates
 */
const initWikipediaPopovers = modalEl => {
    modalEl.querySelectorAll('.wikipedia-link').forEach(el => {
        tippy(el, {
            appendTo: () => document.body,
            popperOptions: {
                modifiers: [{
                    name: 'zIndex', options: {value: 2000}
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

                const geoUrl = new URL('https://en.wikipedia.org/w/api.php');
                geoUrl.search = new URLSearchParams({
                    action: 'query', list: 'geosearch', gscoord: `${lat}|${lon}`, gsradius: 100,
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

