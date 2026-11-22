import { message } from '../../wwwroot/js/localization/messages.js';
import { installLocationMapControls } from '../../wwwroot/js/util/location-map-controls.js';
import { mountLocationActivitySelect } from '../../wwwroot/js/util/location-activity-select.js';
import { mountRazorNotes } from './razor';
import { attachLocationCreate } from '../../wwwroot/js/util/location-create.js';
import { createTileLayer } from '../../wwwroot/js/retryTileLayer.js';

/** Mount the draft anew when an acknowledged response redisplays the existing form. */
const mountLocationCreate = () => {
    let mapContainer;
    if (typeof L !== 'undefined') {
        // Initialize the map with a global zoom level (default zoom level for demonstration)
        mapContainer = L.map('mapContainer', {
            zoomControl: false,
            zoomAnimation: true
        }).setView([51.505, -0.09], 2); // Default view (Global zoom)

        // Add the tile layer from the cache proxy.
        createTileLayer().addTo(mapContainer);
        installLocationMapControls(mapContainer);
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
            marker = L.marker([lat, lng], { alt: message('Locations.SelectedMarker', 'Selected location') }).addTo(mapContainer);

            // Update the Coordinates input field with the selected latitude and longitude
            document.getElementById('Latitude').value = lat.toFixed(8);  // Limit to 8 decimal places
            document.getElementById('Longitude').value = lng.toFixed(8);  // Limit to 8 decimal places
        });


        // Clear button functionality
        document.getElementById('clearButton').addEventListener('click', function () {
            // Clear the marker from the map
            if (marker) {
                marker.remove();
            }

            // Clear the coordinates input fields
            document.getElementById('Latitude').value = '';
            document.getElementById('Longitude').value = '';

            // Reset the map view (optional, can be customized)
            mapContainer.setView([51.505, -0.09], 3); // Resetting to default view
        });
    } else {
        console.error('Leaflet library is not loaded!');
    }

    var localDate = new Date();

    // Format the local date to match the 'datetime-local' input format
    var year = localDate.getFullYear();
    var month = (localDate.getMonth() + 1).toString().padStart(2, '0');
    var day = localDate.getDate().toString().padStart(2, '0');
    var hours = localDate.getHours().toString().padStart(2, '0');
    var minutes = localDate.getMinutes().toString().padStart(2, '0');

    // Construct the formatted datetime-local string
    var localDateTime = `${year}-${month}-${day}T${hours}:${minutes}`;

    // Use browser-local now on a fresh form; validation redisplay retains the attempted timestamp.
    const timestamp = document.getElementById('LocalTimestamp');
    if (timestamp.dataset.initializeLocalTime === 'true') timestamp.value = localDateTime;

    // Ordinary MVC submission owns validation; Notes changes only after authoring or explicit Clear.
    const notes = mountRazorNotes(document.getElementById('notes'));
    attachLocationCreate(document.getElementById('notes').closest('form'), () => notes.isClear() ? '' : notes.getValue(), content => {
        notes.dispose();
        mapContainer?.remove();
        document.getElementById('activitySelect')?.tomselect?.destroy();
        document.querySelector('.site-content').replaceWith(content);
        mountLocationCreate();
    });
    window.addEventListener('pagehide', event => { if (!event.persisted) notes.dispose(); });

    mountLocationActivitySelect(document.getElementById('activitySelect'));
};

if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', mountLocationCreate, { once: true });
else mountLocationCreate();
