import { mountRazorNotes } from './razor';
import { date, message, number } from '../../wwwroot/js/localization/messages.js';
import { encodeFeatureText as encode } from '../../wwwroot/js/util/feature-metadata.js';
import { installLocationMapControls } from '../../wwwroot/js/util/location-map-controls.js';
import { registerLanguageDraft } from '../../wwwroot/js/localization/languageNavigation.js';
/**
 * Visit Edit - Form + Map for editing a visit event
 * Allows editing timestamps, location (via map click), appearance, and notes.
 */

import { createTileLayer } from '../../wwwroot/js/retryTileLayer.js';

// Wayfarer marker icon dimensions used by the shared map icon assets.
const WF_WIDTH = 28;
const WF_HEIGHT = 45;
const WF_ANCHOR = [WF_WIDTH / 2, WF_HEIGHT];

/**
 * Build PNG icon URL for wayfarer markers
 */
const buildPngIconUrl = (icon, color) => {
    const safeIcon = (icon ?? '').trim() || 'marker';
    const safeColor = (color ?? '').trim() || 'bg-blue';
    return `/icons/wayfarer-map-icons/dist/png/marker/${safeColor}/${safeIcon}.png`;
};

let map = null;
let marker = null;

const mountVisitEdit = () => {
    initMap();
    initNotes();
    initDeleteConfirmation();
    initIconColorSync();
    initLocationPings();
};



/**
 * Initialize Leaflet map with visit marker
 */
const initMap = () => {
    const container = document.getElementById('mapContainer');
    if (!container) return;

    const lat = parseFloat(container.dataset.lat) || 0;
    const lon = parseFloat(container.dataset.lon) || 0;
    const icon = container.dataset.icon || 'marker';
    const color = container.dataset.color || 'bg-blue';

    map = L.map('mapContainer', { zoomControl: false }).setView([lat, lon], 15);

    createTileLayer().addTo(map);

    // Set standard attribution prefix (matches Timeline)
    installLocationMapControls(map);

    // Add initial marker
    const iconUrl = buildPngIconUrl(icon, color);
    const leafletIcon = L.icon({
        iconUrl,
        iconSize: [WF_WIDTH, WF_HEIGHT],
        iconAnchor: WF_ANCHOR,
        className: 'map-icon'
    });

    marker = L.marker([lat, lon], {
        icon: leafletIcon, draggable: true, alt: message('Visits.MarkerName', 'Visit marker')
    }).addTo(map);

    // Update coordinates on marker drag
    marker.on('dragend', () => {
        const pos = marker.getLatLng();
        updateCoordinates(pos.lat, pos.lng);
    });

    // Update coordinates on map click
    map.on('click', (e) => {
        const { lat, lng } = e.latlng;
        marker.setLatLng([lat, lng]);
        updateCoordinates(lat, lng);
    });

    // Update marker when coordinates change manually
    document.getElementById('Latitude').addEventListener('change', updateMarkerFromInputs);
    document.getElementById('Longitude').addEventListener('change', updateMarkerFromInputs);
};

/**
 * Update coordinate input fields
 */
const updateCoordinates = (lat, lon) => {
    document.getElementById('Latitude').value = lat.toFixed(6);
    document.getElementById('Longitude').value = lon.toFixed(6);
};

/**
 * Update marker position from input fields
 */
const updateMarkerFromInputs = () => {
    const lat = parseFloat(document.getElementById('Latitude').value);
    const lon = parseFloat(document.getElementById('Longitude').value);

    if (!isNaN(lat) && !isNaN(lon) && lat >= -90 && lat <= 90 && lon >= -180 && lon <= 180) {
        marker.setLatLng([lat, lon]);
        map.setView([lat, lon]);
    }
};

/**
 * Update marker icon when icon/color dropdowns change
 */
const initIconColorSync = () => {
    const iconSelect = document.getElementById('IconNameSnapshot');
    const colorSelect = document.getElementById('MarkerColorSnapshot');

    const updateMarkerIcon = () => {
        if (!marker) return;

        const icon = iconSelect?.value || 'marker';
        const color = colorSelect?.value || 'bg-blue';
        const iconUrl = buildPngIconUrl(icon, color);

        const newIcon = L.icon({
            iconUrl,
            iconSize: [WF_WIDTH, WF_HEIGHT],
            iconAnchor: WF_ANCHOR,
            className: 'map-icon'
        });

        marker.setIcon(newIcon);
    };

    if (iconSelect) iconSelect.addEventListener('change', updateMarkerIcon);
    if (colorSelect) colorSelect.addEventListener('change', updateMarkerIcon);
};

/** Bind only the independent Visit snapshot's NotesHtml field; MVC retains validation and save ownership. */
const initNotes = () => {
    const notes = mountRazorNotes(document.getElementById('notes'));
    // The hidden field receives Notes only on Visit submit. The selector reads the mounted draft directly.
    const savedNotes = notes.getValue();
    const unregisterLanguageDraft = registerLanguageDraft(() => notes.getValue() !== savedNotes);
    document.getElementById('visitForm').addEventListener('submit', () => {
        document.getElementById('hiddenNotes').value = notes.getValue();
    });
    window.addEventListener('pagehide', event => {
        if (event.persisted) return;
        unregisterLanguageDraft();
        notes.dispose();
    });
};

/**
 * Initialize delete confirmation dialog
 */
const initDeleteConfirmation = () => {
    const btnDelete = document.getElementById('btnDelete');
    const deleteForm = document.getElementById('deleteForm');

    if (btnDelete && deleteForm) {
        btnDelete.addEventListener('click', () => {
            wayfarer.showConfirmationModal({
                title: message('Shell.LocationConfirmDeletion', 'Confirm deletion'),
                message: message('Visits.DeletePrompt', 'Are you sure you want to delete this visit? This cannot be undone.'),
                confirmText: message('Shell.Delete', 'Delete'),
                onConfirm: () => {
                    deleteForm.submit();
                }
            });
        });
    }
};

// === Relevant Locations Section ===
let locationPingsPage = 0;
const locationPingsPageSize = 10;

/**
 * Initialize the relevant locations section.
 * Loads data immediately since card is expanded by default.
 */
const initLocationPings = () => {
    const card = document.getElementById('locationPingsCard');
    if (!card) return;

    const collapse = document.getElementById('locationPingsCollapse');

    // Load data immediately (card is expanded by default)
    loadLocationPings();

    // Toggle chevron icon on expand
    collapse.addEventListener('show.bs.collapse', () => {
        document.getElementById('collapseIcon').classList.replace('bi-chevron-down', 'bi-chevron-up');
    });

    // Toggle chevron icon on collapse
    collapse.addEventListener('hide.bs.collapse', () => {
        document.getElementById('collapseIcon').classList.replace('bi-chevron-up', 'bi-chevron-down');
    });

    // Load more button handler
    document.getElementById('loadMoreLocationsBtn')?.addEventListener('click', loadLocationPings);
};

/**
 * Load a page of relevant locations and append to the table.
 */
const loadLocationPings = async () => {
    const visitId = document.getElementById('locationPingsCard')?.dataset.visitId;
    if (!visitId) return;

    const nextPage = locationPingsPage + 1;
    const more = document.getElementById('loadMoreLocationsBtn');
    if (more.disabled) return;
    more.disabled = true;

    try {
        const res = await fetch(`/api/visit/${visitId}/locations?page=${nextPage}&pageSize=${locationPingsPageSize}`);
        if (!res.ok) throw new Error(`Visit locations returned HTTP ${res.status}`);
        const data = await res.json();
        if (!data.success) throw new Error('Visit locations response was incomplete');
        locationPingsPage = nextPage;
        document.getElementById('locationPingsCount').textContent = number(data.totalItems);
        document.getElementById('locationPingsEmpty').style.display = 'none';

        document.getElementById('locationPingsLoading').style.display = 'none';

        if (data.totalItems === 0) {
            document.getElementById('locationPingsEmpty').style.display = 'block';
        } else {
            document.getElementById('locationPingsTableWrapper').style.display = 'block';
            renderLocationPings(data.data);

            // Show/hide load more button based on remaining items
            const loadedCount = locationPingsPage * locationPingsPageSize;
            document.getElementById('locationPingsLoadMore').style.display =
                loadedCount < data.totalItems ? 'block' : 'none';
        }
    } catch (e) {
        console.error('Failed to load relevant locations', e);
        document.getElementById('locationPingsLoading').style.display = 'none';
        document.getElementById('locationPingsCount').textContent = '–';
        document.getElementById('locationPingsEmpty').style.display = 'block';
        document.getElementById('locationPingsEmpty').textContent = message('Visits.LocationsFailed', 'Failed to load relevant locations.');
    } finally {
        more.disabled = false;
    }
};

/**
 * Render relevant location rows into the table body.
 * @param {Array} locations - Array of location objects from the API
 */
const renderLocationPings = (locations) => {
    const tbody = document.getElementById('locationPingsBody');
    const returnUrl = encodeURIComponent(window.location.pathname + window.location.search);

    locations.forEach(loc => {
        const row = document.createElement('tr');
        row.innerHTML = `
            <td class="small">${formatLocationTimestamp(loc.localTimestamp)}</td>
            <td class="small font-monospace">${loc.latitude == null ? '–' : number(loc.latitude, { minimumFractionDigits: 5, maximumFractionDigits: 5 })}, ${loc.longitude == null ? '–' : number(loc.longitude, { minimumFractionDigits: 5, maximumFractionDigits: 5 })}</td>
            <td class="text-end small">${loc.accuracy ? number(loc.accuracy) + ' m' : '-'}</td>
            <td class="text-end small">${loc.speed ? number(loc.speed * 3.6, { minimumFractionDigits: 1, maximumFractionDigits: 1 }) + ' km/h' : '-'}</td>
            <td class="small">${encode(loc.activity || '-')}</td>
            <td class="small text-truncate" style="max-width:150px;" title="${encode(loc.address || '')}">${encode(loc.address || '-')}</td>
            <td>
                <a href="/User/Location/Edit/${loc.id}?returnUrl=${returnUrl}" class="btn btn-sm btn-outline-secondary py-0 px-1" title="${encode(message('Locations.EditAction', 'Edit location'))}" aria-label="${encode(message('Locations.EditAction', 'Edit location'))}"><i class="bi bi-pencil"></i></a>
            </td>
        `;
        tbody.appendChild(row);
    });
};

/**
 * Format a timestamp for display in the location pings table.
 * @param {string} ts - ISO timestamp string
 * @returns {string} Formatted date/time string
 */
const formatLocationTimestamp = (ts) => {
    if (!ts) return '-';
    return date(new Date(ts), {
        day: '2-digit', month: 'short', year: 'numeric',
        hour: '2-digit', minute: '2-digit'
    });
};

if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', mountVisitEdit, { once: true });
else mountVisitEdit();
