/**
 * Visit Edit - Form + Map for editing a visit event
 * Allows editing timestamps, location (via map click), appearance, and notes.
 */

// Wayfarer marker icon dimensions (matches Trip mapManager)
const WF_WIDTH = 28;
const WF_HEIGHT = 45;
const WF_ANCHOR = [WF_WIDTH / 2, WF_HEIGHT];
// Map tiles config (proxy URL + attribution) injected by layout.
const tilesConfig = window.wayfarerTileConfig || {};
const tilesUrl = tilesConfig.tilesUrl || `${window.location.origin}/Public/tiles/{z}/{x}/{y}.png`;
const tilesAttribution = tilesConfig.attribution || '&copy; OpenStreetMap contributors';

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
let quill = null;

document.addEventListener('DOMContentLoaded', () => {
    initMap();
    initQuill();
    initDeleteConfirmation();
    initIconColorSync();
    initLocationPings();
});

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

    map = L.map('mapContainer').setView([lat, lon], 15);

    L.tileLayer(tilesUrl, {
        maxZoom: 19,
        attribution: tilesAttribution
    }).addTo(map);

    // Set standard attribution prefix (matches Timeline)
    map.attributionControl.setPrefix('&copy; <a href="https://wayfarer.stefk.me" title="Powered by Wayfarer, made by Stef" target="_blank">Wayfarer</a> | <a href="https://stefk.me" title="Check my blog" target="_blank">Stef K</a> | &copy; <a href="https://leafletjs.com/" target="_blank">Leaflet</a>');

    // Add initial marker
    const iconUrl = buildPngIconUrl(icon, color);
    const leafletIcon = L.icon({
        iconUrl,
        iconSize: [WF_WIDTH, WF_HEIGHT],
        iconAnchor: WF_ANCHOR,
        className: 'map-icon'
    });

    marker = L.marker([lat, lon], { icon: leafletIcon, draggable: true }).addTo(map);

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

/**
 * Proxy external image URLs for display
 * @param {HTMLElement} root - Container element with images to proxy
 */
const proxyImages = (root) => {
    root.querySelectorAll('img').forEach(img => {
        const src = img.getAttribute('src');
        if (src && !src.startsWith('data:') && !src.startsWith('/Public/ProxyImage') && !img.dataset.original) {
            img.dataset.original = src;
            img.setAttribute('src', `/Public/ProxyImage?url=${encodeURIComponent(src)}`);
        }
    });
};

/**
 * Initialize Quill rich text editor for notes
 */
const initQuill = () => {
    const notesContainer = document.getElementById('notes');
    if (!notesContainer) return;

    quill = new Quill('#notes', {
        theme: 'snow',
        modules: {
            toolbar: [
                [{ 'header': [1, 2, 3, false] }],
                ['bold', 'italic', 'underline', 'strike'],
                [{ 'list': 'ordered' }, { 'list': 'bullet' }],
                ['link', 'image'],
                ['clean']
            ]
        }
    });

    // Load existing content and proxy images
    const existingContent = notesContainer.dataset.notesContent;
    if (existingContent) {
        quill.root.innerHTML = existingContent;
        proxyImages(quill.root);
    }

    // Proxy images on text changes (for newly inserted images)
    quill.on('text-change', () => {
        proxyImages(quill.root);
    });

    // Sync to hidden input on form submit
    const form = document.getElementById('visitForm');
    if (form) {
        form.addEventListener('submit', () => {
            const hiddenNotes = document.getElementById('hiddenNotes');
            if (hiddenNotes) {
                hiddenNotes.value = quill.root.innerHTML;
            }
        });
    }
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
                title: 'Confirm Deletion',
                message: 'Are you sure you want to delete this visit? This action cannot be undone.',
                confirmText: 'Delete',
                onConfirm: () => {
                    deleteForm.submit();
                }
            });
        });
    }
};

// === Relevant Locations Section ===
let locationPingsPage = 0;
let locationPingsTotalItems = 0;
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
    fetchLocationPingsCount();
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
 * Fetch the total count of relevant locations for the badge display.
 */
const fetchLocationPingsCount = async () => {
    const visitId = document.getElementById('locationPingsCard')?.dataset.visitId;
    if (!visitId) return;

    try {
        const res = await fetch(`/api/visit/${visitId}/locations?page=1&pageSize=1`);
        const data = await res.json();
        if (data.success) {
            document.getElementById('locationPingsCount').textContent = data.totalItems;
        }
    } catch (e) {
        console.error('Failed to fetch relevant locations count', e);
    }
};

/**
 * Load a page of relevant locations and append to the table.
 */
const loadLocationPings = async () => {
    const visitId = document.getElementById('locationPingsCard')?.dataset.visitId;
    if (!visitId) return;

    locationPingsPage++;

    try {
        const res = await fetch(`/api/visit/${visitId}/locations?page=${locationPingsPage}&pageSize=${locationPingsPageSize}`);
        const data = await res.json();

        document.getElementById('locationPingsLoading').style.display = 'none';

        if (data.success) {
            locationPingsTotalItems = data.totalItems;

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
        }
    } catch (e) {
        console.error('Failed to load relevant locations', e);
        document.getElementById('locationPingsLoading').style.display = 'none';
        document.getElementById('locationPingsEmpty').style.display = 'block';
        document.getElementById('locationPingsEmpty').textContent = 'Failed to load relevant locations.';
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
            <td class="small font-monospace">${loc.latitude?.toFixed(5)}, ${loc.longitude?.toFixed(5)}</td>
            <td class="text-end small">${loc.accuracy ? loc.accuracy + 'm' : '-'}</td>
            <td class="text-end small">${loc.speed ? (loc.speed * 3.6).toFixed(1) + ' km/h' : '-'}</td>
            <td class="small">${loc.activity || '-'}</td>
            <td class="small text-truncate" style="max-width:150px;" title="${loc.address || ''}">${loc.address || '-'}</td>
            <td>
                <a href="/User/Location/Edit/${loc.id}?returnUrl=${returnUrl}" class="btn btn-sm btn-outline-secondary py-0 px-1" title="Edit location"><i class="bi bi-pencil"></i></a>
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
    const d = new Date(ts);
    return d.toLocaleString('en-GB', {
        day: '2-digit', month: 'short', year: 'numeric',
        hour: '2-digit', minute: '2-digit'
    });
};
