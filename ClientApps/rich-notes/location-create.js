import { mountRazorNotes } from './razor';
import { createTileLayer } from '../../wwwroot/js/retryTileLayer.js';

const mountLocationCreate = () => {

    if (typeof L !== 'undefined') {
        // Initialize the map with a global zoom level (default zoom level for demonstration)
        var mapContainer = L.map('mapContainer', {
            zoomAnimation: true
        }).setView([51.505, -0.09], 2); // Default view (Global zoom)

        // Add the tile layer from the cache proxy.
        createTileLayer().addTo(mapContainer);
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

    // Set the value of the datetime-local input field
    if (!document.getElementById('LocalTimestamp').value) document.getElementById('LocalTimestamp').value = localDateTime;

    // Ordinary MVC submission owns validation; Notes changes only after authoring or explicit Clear.
    const notes = mountRazorNotes(document.getElementById('notes'));
    document.getElementById('notes').closest('form').addEventListener('submit', () => {
        document.getElementById('Notes').value = notes.isClear() ? '' : notes.getValue();
    });
    window.addEventListener('pagehide', event => { if (!event.persisted) notes.dispose(); });

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

if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', mountLocationCreate, { once: true });
else mountLocationCreate();
