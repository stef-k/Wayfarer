import { mountProtectedLocationForm } from '../../wwwroot/js/util/location-edit-form.js';
import { mountRazorNotes } from './razor';
import { renderLocationAddress } from '../../wwwroot/js/util/location-address.js';
import { createTileLayer } from '../../wwwroot/js/retryTileLayer.js';

let mapContainer = null;
let data = null;

/** Mount once after the server-rendered form is available. */
const mountLocationEdit = () => {
    // Share the Location address hierarchy while leaving posted editable values untouched.
    const summary = document.getElementById('location-address-summary');
    if (summary) {
        const location = JSON.parse(summary.dataset.address);
        summary.innerHTML = renderLocationAddress(location, location.fullAddress || location.address
            || [location.place, location.region, location.country].filter(Boolean).join(', '));
    }

    if (typeof L !== 'undefined') {

        data = document.getElementById('mapContainer');
        const latitude = parseFloat(data.dataset.lat);
        const longitude = parseFloat(data.dataset.lon);

        // Initialize the map with the latitude and longitude from the location
        const mapContainer = L.map('mapContainer').setView([latitude, longitude], 13); // Center map at the location

        // Add the tile layer from the cache proxy.
        createTileLayer({ zoomAnimation: true }).addTo(mapContainer);
        mapContainer.attributionControl.setPrefix('&copy; <a href="https://wayfarer.stefk.me" title="Powered by Wayfarer, made by Stef" target="_blank">Wayfarer</a> | <a href="https://stefk.me" title="Check my blog" target="_blank">Stef K</a> | &copy; <a href="https://leafletjs.com/" target="_blank">Leaflet</a>');
        // Marker variable to store the placed marker
        var marker;

        // Change the cursor when hovering over the map
        mapContainer.getContainer().style.cursor = 'pointer';

        // Map click event to place the marker and update the Coordinates field
        mapContainer.on('click', function (e) {
            const { lat, lng } = e.latlng;

            // Remove the existing marker if there is one
            if (marker) {
                marker.remove();
            }

            // Place a new marker
            marker = L.marker([lat, lng]).addTo(mapContainer);

            // Update the Coordinates input field with the selected latitude and longitude
            document.getElementById('Latitude').value = lat.toFixed(8);
            document.getElementById('Longitude').value = lng.toFixed(8);
            // Retain the complete pair synchronously, including while an older save is pending.
            document.getElementById('Latitude').dispatchEvent(new Event('input', { bubbles: true }));
        });

        // Set the existing marker at the current location
        marker = L.marker([latitude, longitude]).addTo(mapContainer);
    }

    // The editor exposes authored values; the existing feature owner keeps all durable save/recovery state.
    const form = document.getElementById('locationForm');
    // Prevent native POST even if editor construction or retained proof is unavailable.
    form.addEventListener('submit', event => event.preventDefault());
    try {
        const notes = mountRazorNotes(document.getElementById('notes'));
        mountProtectedLocationForm(form, notes);
        window.addEventListener('pagehide', event => { if (!event.persisted) notes.dispose(); });
    } catch {
        document.getElementById('location-save-status').textContent = 'Editor, saved proof or local storage is unavailable. Keep your draft on this page; no save was sent.';
    }

    // Initialize TomSelect on Activity dropdown with API-based loading
    if (typeof TomSelect !== 'undefined') {
        const activitySelect = document.getElementById('activitySelect');
        if (activitySelect) {
            const apiUrl = activitySelect.dataset.apiUrl || '/api/activity';
            new TomSelect(activitySelect, {
                valueField: 'id',
                labelField: 'name',
                searchField: ['name', 'description'],
                create: false,
                sortField: { field: 'name', direction: 'asc' },
                placeholder: 'Search for an activity...',
                allowEmptyOption: true,
                preload: 'focus',
                load: function(query, callback) {
                    fetch(apiUrl)
                        .then(response => response.json())
                        .then(data => {
                            callback(data);
                        })
                        .catch(() => {
                            callback();
                        });
                }
            });
        }
    }
};

if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', mountLocationEdit, { once: true });
else mountLocationEdit();
