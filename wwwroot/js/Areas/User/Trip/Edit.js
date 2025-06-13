const form = document.getElementById('trip-form');
const btnSave = document.getElementById('btn-save-trip');
const btnSaveEdit = document.getElementById('btn-save-edit-trip');
const btnAddRegion = document.getElementById('btn-add-region');
const regionsAccordion = document.getElementById('regions-accordion');
const placeSearch = document.getElementById('place-search');
const btnSearch = document.getElementById('btn-search');
const tripId = form.querySelector('input[name="Id"]').value;
let mapContainer; // This will hold the Leaflet map instance

let zoomLevel = 3;
const tilesUrl = `${window.location.origin}/Public/tiles/{z}/{x}/{y}.png`;
import {addZoomLevelControl} from '../../../map-utils.js';

// permalink setup
const urlParams = new URLSearchParams(window.location.search);
const initialLat = parseFloat(urlParams.get('lat'));
const initialLng = parseFloat(urlParams.get('lng'));
const z = parseInt(urlParams.get('zoom'), 10);
zoomLevel = (!isNaN(z) && z >= 0) ? z : 3;
let initialCenter = (
    !isNaN(initialLat) && !isNaN(initialLng)
        ? [initialLat, initialLng]
        : [20, 0]
);

const initializeMap = () => {
    // Check if map is already initialized and remove it
    if (mapContainer !== undefined && mapContainer !== null) {
        mapContainer.off();
        mapContainer.remove();
    }
    // Initialize the Leaflet map on the #mapContainer element
    mapContainer = L.map('mapContainer', {
        zoomAnimation: true
    }).setView(initialCenter, zoomLevel);
    L.tileLayer(tilesUrl, {
        maxZoom: 19, attribution: '© OpenStreetMap contributors'
    }).addTo(mapContainer); // Add to mapContainer instance

    mapContainer.attributionControl.setPrefix('&copy; <a href="https://leafletjs.com/" target="_blank">Leaflet</a>');

    addZoomLevelControl(mapContainer); // Add control to mapContainer instance

    return mapContainer;
};

const saveTrip = async (action) => {
    const formData = new FormData(form);
    formData.set('submitAction', action);

    try {
        const resp = await fetch(form.action, {
            method: 'POST',
            body: formData,
            headers: {'X-CSRF-TOKEN': formData.get('__RequestVerificationToken')}
        });

        if (resp.redirected) {
            window.location.href = resp.url;
        } else {
            const html = await resp.text();
            const parser = new DOMParser();
            const doc = parser.parseFromString(html, 'text/html');
            const newForm = doc.querySelector('#trip-form');
            if (newForm) {
                form.replaceWith(newForm);
                rebindMainButtons();
            }
        }
    } catch (err) {
        console.error(err);
        alert('Error saving trip.');
    }
};

const rebindMainButtons = () => {
    document.getElementById('btn-save-trip')?.addEventListener('click', () => saveTrip('save'));
    document.getElementById('btn-save-edit-trip')?.addEventListener('click', () => saveTrip('save-edit'));
};

const loadRegionCreateForm = async () => {
    const resp = await fetch(`/User/Regions/Create?tripId=${tripId}`);
    const html = await resp.text();
    regionsAccordion.insertAdjacentHTML('afterbegin', html);
    attachRegionFormHandlers();
};

const attachRegionFormHandlers = () => {
    document.querySelectorAll('.btn-region-save').forEach(btn => {
        btn.onclick = async () => {
            const regionId = btn.dataset.regionId;
            const formEl = document.getElementById(`region-form-${regionId}`);
            const fd = new FormData(formEl);
            const resp = await fetch(`/User/Regions/Create`, {
                method: 'POST',
                body: fd,
                headers: {'X-CSRF-TOKEN': fd.get('__RequestVerificationToken')}
            });

            const html = await resp.text();
            const wrapper = formEl.closest('.accordion-item, form');

            if (resp.ok) {
                {
                    const resp2 = await fetch(`/User/Trip/GetTripDays?tripId=${tripId}`);
                    const newTripDays = await resp2.text();
                    const input = document.querySelector('#trip-form input[name="Days"]');
                    if (input) input.value = newTripDays;
                }
                wrapper.outerHTML = html;
                attachRegionItemHandlers(regionId);
                // Update trip Days input field
                const tripDaysResp = await fetch(`/User/Trip/GetTripDays?tripId=${tripId}`);
                const newTripDays = await tripDaysResp.text();
                document.querySelector('#trip-form input[name="Days"]').value = newTripDays;
            } else {
                wrapper.outerHTML = html;
                attachRegionFormHandlers();

                // Extract errors manually if present
                const dom = new DOMParser().parseFromString(html, 'text/html');
                const errorsBlock = dom.querySelector('.region-form-errors ul');

                if (errorsBlock) {
                    const errors = Array.from(errorsBlock.querySelectorAll('li')).map(li => li.textContent.trim());
                    const message = errors.join('\n');
                    showAlert('danger', message);
                }

            }
        };
    });

    document.querySelectorAll('.btn-region-cancel').forEach(btn => {
        btn.onclick = async () => {
            const regionId = btn.dataset.regionId;
            const wrapper = document.getElementById(`region-form-${regionId}`);
            const resp = await fetch(`/User/Regions/GetItemPartial?regionId=${regionId}`);
            const html = await resp.text();
            wrapper.outerHTML = html;
            attachRegionItemHandlers(regionId);
        };
    });


};

const attachRegionItemHandlers = (regionId) => {
    document.querySelectorAll(`.btn-delete-region[data-region-id="${regionId}"]`)
        .forEach(btn => btn.onclick = () => {
            showConfirmationModal({
                title: 'Delete Region?',
                message: 'Are you sure you want to permanently delete this region and all its places?',
                confirmText: 'Delete',
                onConfirm: async () => {
                    const fd = new FormData();
                    fd.set('__RequestVerificationToken', document.querySelector('input[name="__RequestVerificationToken"]').value);

                    const resp = await fetch(`/User/Regions/Delete/${regionId}`, {
                        method: 'POST',
                        body: fd
                    });

                    if (resp.ok) {
                        const regionEl = document.getElementById(`region-item-${regionId}`);
                        if (regionEl) {
                            // If the collapse is open, collapse it first (optional visual polish)
                            const collapseEl = regionEl.querySelector('.accordion-collapse.show');
                            if (collapseEl) {
                                const bsCollapse = bootstrap.Collapse.getInstance(collapseEl);
                                bsCollapse?.hide();
                            }

                            // Remove the whole accordion item
                            regionEl.remove();
                        }

                        showAlert('success', 'Region deleted.');
                    } else {
                        showAlert('danger', 'Failed to delete region.');
                    }
                }
            });
        });


    document.querySelectorAll(`.btn-edit-region[data-region-id="${regionId}"]`)
        .forEach(btn => btn.onclick = async () => {
            const resp = await fetch(`/User/Regions/Create?tripId=${tripId}&regionId=${regionId}`);
            const html = await resp.text();
            const item = document.getElementById(`region-item-${regionId}`);
            item.outerHTML = html;
            attachRegionFormHandlers();
        });

    document.querySelectorAll(`.btn-add-place[data-region-id="${regionId}"]`)
        .forEach(btn => btn.onclick = async () => {
            const regionId = btn.dataset.regionId;
            const resp = await fetch(`/User/Places/CreateOrUpdate?regionId=${regionId}`);
            const html = await resp.text();
            const regionItem = document.getElementById(`region-item-${regionId}`);
            regionItem.insertAdjacentHTML('beforeend', html); // place at bottom
            attachPlaceFormHandlers();
        });

    document.querySelectorAll(`.btn-delete-place[data-region-id="${regionId}"]`)
        .forEach(btn => btn.onclick = () => {
            const placeId = btn.dataset.placeId;

            showConfirmationModal({
                title: 'Delete Place?',
                message: 'Are you sure you want to permanently delete this place?',
                confirmText: 'Delete',
                onConfirm: async () => {
                    const fd = new FormData();
                    fd.set('__RequestVerificationToken', document.querySelector('input[name="__RequestVerificationToken"]').value);

                    const resp = await fetch(`/User/Places/Delete/${placeId}`, {
                        method: 'POST',
                        body: fd
                    });

                    const html = await resp.text();

                    if (resp.ok) {
                        const regionEl = document.getElementById(`region-item-${regionId}`);
                        regionEl.outerHTML = html;
                        attachRegionItemHandlers(regionId);
                        showAlert('success', 'Place deleted.');
                    } else {
                        showAlert('danger', 'Failed to delete place.');
                    }
                }
            });
        });

    document.querySelectorAll(`.btn-edit-place[data-region-id="${regionId}"]`)
        .forEach(btn => btn.onclick = async () => {
            const placeId = btn.dataset.placeId;
            const resp = await fetch(`/User/Places/Edit/${placeId}`);
            const html = await resp.text();
            const placeLi = btn.closest('li');
            placeLi.outerHTML = html;
            attachPlaceFormHandlers();
        });

};

const initExistingRegions = () => {
    document.querySelectorAll('.accordion-item[id^="region-item-"]').forEach(el => {
        const regionId = el.id.replace('region-item-', '');
        attachRegionItemHandlers(regionId);
    });
};

const attachPlaceFormHandlers = () => {
    document.querySelectorAll('.btn-place-cancel').forEach(btn => {
        btn.onclick = async () => {
            const placeId = btn.dataset.placeId;
            const regionId = btn.dataset.regionId;

            // If this was a new form, just remove it
            const formEl = document.getElementById(`place-form-${placeId}`);
            if (!placeId || !formEl) return;

            // Otherwise reload region partial to show updated state
            const regionEl = document.getElementById(`region-item-${regionId}`);
            const resp = await fetch(`/User/Regions/GetItemPartial?regionId=${regionId}`);
            const html = await resp.text();
            regionEl.outerHTML = html;
            attachRegionItemHandlers(regionId);
        };
    });

    document.querySelectorAll('.btn-place-save').forEach(btn => {
        btn.onclick = async () => {
            const placeId = btn.dataset.placeId;
            const regionId = btn.dataset.regionId;
            const formEl = document.getElementById(`place-form-${placeId}`);
            const fd = new FormData(formEl);

            const resp = await fetch(`/User/Places/CreateOrUpdate`, {
                method: 'POST',
                body: fd,
                headers: {
                    'X-CSRF-TOKEN': fd.get('__RequestVerificationToken')
                }
            });

            const html = await resp.text();
            const wrapper = formEl.closest('form');

            if (resp.ok) {
                const regionEl = document.getElementById(`region-item-${regionId}`);
                regionEl.outerHTML = html;
                attachRegionItemHandlers(regionId);
            } else {
                wrapper.outerHTML = html;
                attachPlaceFormHandlers();

                const dom = new DOMParser().parseFromString(html, 'text/html');
                const errorsBlock = dom.querySelector('.place-form-errors ul');

                if (errorsBlock) {
                    const errors = Array.from(errorsBlock.querySelectorAll('li')).map(li => li.textContent.trim());
                    const message = errors.join('\n');
                    showAlert('danger', message);
                }
            }
        };
    });
};


const setupQuil = () => {
    // Initialize Quill editor
    var quill = new Quill('#notes', {
        theme: 'snow',
        placeholder: 'Add your notes...',
        modules: {
            clipboard: {
                matchVisual: false
            },
            toolbar: [
                [{'header': [1, 2, 3, 4, 5, 6, false]}],
                ['bold', 'italic', 'underline'],  // Bold, Italic, Underline
                [{'list': 'ordered'}, {'list': 'bullet'}],  // Lists
                ['link', 'image'],  // Link
                [{'font': []}],  // Add font family option if needed
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
    let form = document.getElementById("trip-form");
    form.addEventListener("submit", function () {
        var hiddenNotesField = document.getElementById("hiddenNotes");
        hiddenNotesField.value = quill.root.innerHTML;  // Set the hidden input value to the Quill content
    });
};

// Segment handlers
const initSegmentHandlers = () => {

    document.querySelectorAll('.btn-edit-segment').forEach(btn => {
        btn.addEventListener('click', async () => {
            const segmentId = btn.dataset.segmentId;
            if (!segmentId || segmentId === 'undefined') {
                console.warn('Invalid segmentId on edit');
                return;
            }

            const resp = await fetch(`/User/Segments/Edit/${segmentId}`);
            const html = await resp.text();
            const segmentItem = document.getElementById(`segment-item-${segmentId}`);
            segmentItem.outerHTML = html;
            attachSegmentFormHandlers();
        });
    });

    document.querySelectorAll('.btn-delete-segment').forEach(btn => {
        btn.addEventListener('click', () => {
            const segmentId = btn.dataset.segmentId;
            if (!segmentId || segmentId === 'undefined') {
                console.warn('Invalid segmentId on delete');
                return;
            }

            showConfirmationModal({
                title: 'Delete Segment?',
                message: 'Are you sure you want to permanently delete this segment?',
                confirmText: 'Delete',
                onConfirm: async () => {
                    const fd = new FormData();
                    fd.set('__RequestVerificationToken', document.querySelector('input[name="__RequestVerificationToken"]').value);

                    const resp = await fetch(`/User/Segments/Delete/${segmentId}`, {
                        method: 'POST',
                        body: fd
                    });

                    const html = await resp.text();

                    if (resp.ok) {
                        document.getElementById('segments-list').innerHTML = html;
                        initSegmentHandlers(); // 🔁 rebind for new DOM
                        showAlert('success', 'Segment deleted.');
                    } else {
                        showAlert('danger', 'Failed to delete segment.');
                    }
                }
            });
        });
    });
};

const attachSegmentFormHandlers = () => {
    document.querySelectorAll('.btn-segment-cancel').forEach(btn => {
        btn.onclick = async () => {
            const segmentId = btn.dataset.segmentId;
            const resp = await fetch(`/User/Segments/Edit/${segmentId}`);
            const html = await resp.text();
            const segmentItem = document.getElementById(`segment-item-${segmentId}`);
            segmentItem.outerHTML = html;
            initSegmentHandlers(); // Rebind handlers
        };
    });

    document.querySelectorAll('.btn-segment-save').forEach(btn => {
        btn.onclick = async () => {
            const segmentId = btn.dataset.segmentId;
            const formEl = document.getElementById(`segment-form-${segmentId}`);
            const fd = new FormData(formEl);

            const resp = await fetch(`/User/Segments/Create`, {
                method: 'POST',
                body: fd,
                headers: { 'X-CSRF-TOKEN': fd.get('__RequestVerificationToken') }
            });

            const html = await resp.text();
            const wrapper = formEl.closest('form');

            if (resp.ok) {
                // Replace full list with updated segment list
                document.getElementById('segments-list').innerHTML = html;
                initSegmentHandlers(); // Rebind handlers
            }  else {
                wrapper.outerHTML = html;
                attachSegmentFormHandlers();

                const dom = new DOMParser().parseFromString(html, 'text/html');
                const errorsBlock = dom.querySelector('.segment-form-errors ul');

                if (errorsBlock) {
                    const errors = Array.from(errorsBlock.querySelectorAll('li')).map(li => li.textContent.trim());
                    const message = errors.join('\n');
                    console.warn('Segment form validation errors:\n' + message);
                    showAlert('danger', message);
                }
            }
        };
    });
};

const loadSegmentCreateForm = async () => {
    const resp = await fetch(`/User/Segments/Create?tripId=${tripId}`);
    const html = await resp.text();

    const container = document.getElementById('segments-list');
    container.insertAdjacentHTML('beforeend', html); // or 'afterbegin' if you want newest first

    attachSegmentFormHandlers(); // attach save/cancel buttons
};


// Entry point
document.addEventListener('DOMContentLoaded', () => {
    rebindMainButtons();
    btnAddRegion?.addEventListener('click', loadRegionCreateForm);
    btnSearch?.addEventListener('click', () => {
        console.log('Search:', placeSearch.value);
        // TODO: Implement Mapbox search
    });

    document.getElementById('btn-add-segment')?.addEventListener('click', loadSegmentCreateForm);

    initializeMap(); // Initialize the map (now using mapContainer)
    setupQuil();

    window.addEventListener('resize', function () {
        mapContainer.invalidateSize(); // Use mapContainer instance
    });

    initExistingRegions();
    initSegmentHandlers();
});