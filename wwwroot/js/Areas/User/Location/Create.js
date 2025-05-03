const tilesUrl = `${window.location.origin}/Public/tiles/{z}/{x}/{y}.png`;

document.addEventListener("DOMContentLoaded", function () {

    if (typeof L !== 'undefined') {
        // Initialize the map with a global zoom level (default zoom level for demonstration)
        var mapContainer = L.map('mapContainer', {
            zoomAnimation: true
        }).setView([51.505, -0.09], 2); // Default view (Global zoom)

        // Add the OpenStreetMap tile layer
        L.tileLayer(tilesUrl, {
            attribution: '&copy; <a href="https://www.openstreetmap.org/copyright" target="_blank">OpenStreetMap</a> contributors'
        }).addTo(mapContainer);
        mapContainer.attributionControl.setPrefix('&copy; <a href="https://leafletjs.com/" target="_blank">Leaflet</a>');
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
    document.getElementById('LocalTimestamp').value = localDateTime;

    var quill = new Quill('#notes', {
        theme: 'snow',
        modules: {
            clipboard: {
                matchVisual: false
            },
            toolbar: {
                container: [
                    [{ 'header': [1, 2, 3, 4, 5, 6, false] }],
                    ['bold', 'italic', 'underline'],  // Bold, Italic, Underline
                    [{ 'list': 'ordered' }, { 'list': 'bullet' }],  // Lists
                    ['link', 'image'],  // Link
                    [{ 'font': [] }],  // Add font family option if needed
                    ['clean']
                ],
                handlers: {

                }
            }
        }
    });
    var toolbar = quill.getModule('toolbar');

    var clearText = document.createElement('span');
    clearText.classList.add('ql-clear');
    clearText.innerText = 'Clear';
    clearText.setAttribute('data-tooltip', 'Clear editor text');
    toolbar.container.appendChild(clearText);

    clearText.addEventListener('click', function () {
        quill.setText(''); // Clears the editor content
    });
    // Prevent base64 images from being inserted when pasted
    quill.on("text-change", function () {
        const editor = document.querySelector(".ql-editor");
        const images = editor.querySelectorAll("img");

        images.forEach((img) => {
            if (img.src.startsWith("data:image")) {
                img.remove(); // Remove the pasted image
            }
        });
    });

    // handler form submision and quill field
    var form = document.querySelector('form');
    form.addEventListener('submit', function () {
        var notes = document.getElementById('Notes');
        notes.value = quill.root.innerHTML; // Save HTML content of the editor
    });

});


