let mapContainer = null;
let data = null;
// Map tiles config (proxy URL + attribution) injected by layout.
const tilesConfig = window.wayfarerTileConfig || {};
const tilesUrl = tilesConfig.tilesUrl || `${window.location.origin}/Public/tiles/{z}/{x}/{y}.png`;
const tilesAttribution = tilesConfig.attribution || '&copy; OpenStreetMap contributors';

document.addEventListener("DOMContentLoaded", function () {

    if (typeof L !== 'undefined') {

        data = document.getElementById('mapContainer');
        const latitude = parseFloat(data.dataset.lat);
        const longitude = parseFloat(data.dataset.lon);

        // Initialize the map with the latitude and longitude from the location
        const mapContainer = L.map('mapContainer').setView([latitude, longitude], 13); // Center map at the location

        // Add the tile layer from the cache proxy.
        L.tileLayer(tilesUrl, {
            attribution: tilesAttribution,
            zoomAnimation: true
        }).addTo(mapContainer);
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
        });

        // Set the existing marker at the current location
        marker = L.marker([latitude, longitude]).addTo(mapContainer);
    }

    // Initialize Quill editor
    var quill = new Quill('#notes', {
        theme: 'snow',
        placeholder: 'Add your notes...',
        modules: {
            clipboard: {
                matchVisual: false
            },
            toolbar: [
                [{ 'header': [1, 2, 3, 4, 5, 6, false] }],
                ['bold', 'italic', 'underline'],  // Bold, Italic, Underline
                [{ 'list': 'ordered' }, { 'list': 'bullet' }],  // Lists
                ['link', 'image'],  // Link
                [{ 'font': [] }],  // Add font family option if needed
                ['clean']
            ]
        }
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

    var toolbar = quill.getModule('toolbar');

    var clearText = document.createElement('span');
    clearText.classList.add('ql-clear');
    clearText.innerText = 'Clear';
    clearText.setAttribute('data-tooltip', 'Clear editor text');
    toolbar.container.appendChild(clearText);
    let savedRange = null;

    clearText.addEventListener('click', function () {
        quill.setText(''); // Clears the editor content
    });

    var toolbar = quill.getModule('toolbar');
    toolbar.addHandler('image', function () {
        // Show the modal when the image button is clicked
        var imageModal = new bootstrap.Modal(document.getElementById('imageModal'));
        imageModal.show();
    });

    quill.on('selection-change', function (range) {
        if (range) {
            savedRange = range;
        }
    });


    // Insert image URL when "Insert Image" button is clicked
    document.getElementById('insertImageBtn').addEventListener('click', function () {
        const imageUrl = document.getElementById('imageUrl').value;

        if (imageUrl) {
            if (savedRange) {
                // Restore the saved selection range
                quill.setSelection(savedRange.index, savedRange.length);
                // Insert the image at the saved range
                quill.insertEmbed(savedRange.index, 'image', imageUrl);
            } else {
                // If no range was saved, insert at the end
                quill.insertEmbed(quill.getLength(), 'image', imageUrl);
            }
            console.log(imageUrl);
            // Close the modal
            const imageModal = bootstrap.Modal.getInstance(document.getElementById('imageModal'));
            imageModal.hide();
        } else {
            alert('Please enter a valid image URL.');
        }
    });

    // Clear the input field when the modal is hidden
    document.getElementById('imageModal').addEventListener('hidden.bs.modal', function () {
        document.getElementById('imageUrl').value = '';
    });
    // Ensure the notes content is correctly handled (escaping it to avoid breaking the JS)
    const notesElement = document.getElementById('notes');
    const notesContent = notesElement.dataset.notesContent;

    // Set the notes content in the Quill editor
    quill.root.innerHTML = notesContent;

    // When the form is submitted, update the hidden input field with the Quill content
    var form = document.getElementById("locationForm");
    form.addEventListener("submit", function () {
        var hiddenNotesField = document.getElementById("hiddenNotes");
        hiddenNotesField.value = quill.root.innerHTML;  // Set the hidden input value to the Quill content
    });

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
});
