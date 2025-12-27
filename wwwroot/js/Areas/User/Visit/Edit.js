/**
 * Visit Edit - Form + Map for editing a visit event
 * Allows editing timestamps, location (via map click), appearance, and notes.
 */

// Wayfarer marker icon dimensions
const WF_WIDTH = 48;
const WF_HEIGHT = 64;
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
let quill = null;

document.addEventListener('DOMContentLoaded', () => {
    initMap();
    initQuill();
    initDeleteConfirmation();
    initIconColorSync();
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

    L.tileLayer('/tiles/{z}/{x}/{y}', {
        maxZoom: 19,
        attribution: '&copy; OpenStreetMap contributors'
    }).addTo(map);

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

    // Load existing content
    const existingContent = notesContainer.dataset.notesContent;
    if (existingContent) {
        quill.root.innerHTML = existingContent;
    }

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
