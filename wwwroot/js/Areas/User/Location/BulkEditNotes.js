// js/Areas/User/Location/BulkEditNotes.js
// JavaScript for the Bulk Edit Notes view

document.addEventListener('DOMContentLoaded', function () {
    // Cache filter elements and submit button
    const countrySelect = document.getElementById('Country');
    const regionSelect = document.getElementById('Region');
    const placeSelect = document.getElementById('Place');
    const fromDateInput = document.getElementById('FromDate');
    const toDateInput = document.getElementById('ToDate');
    const applyBtn = document.getElementById('applyBtn');

    // Utility to populate a <select> with string values
    function populate(select, items, includeAll = true) {
        select.innerHTML = '';
        if (includeAll) select.appendChild(new Option('All', ''));
        items.forEach(text => select.appendChild(new Option(text, text)));
    }

    // Call preview-count endpoint and update button
    async function updateCount() {
        const params = new URLSearchParams();
        if (countrySelect.value) params.append('country', countrySelect.value);
        if (regionSelect.value) params.append('region', regionSelect.value);
        if (placeSelect.value) params.append('place', placeSelect.value);

        const fromDate = fromDateInput.value;
        const toDate = toDateInput.value;

        // Only include if valid ISO date string
        if (fromDate && !isNaN(Date.parse(fromDate))) params.append('fromDate', fromDate);
        if (toDate && !isNaN(Date.parse(toDate))) params.append('toDate', toDate);

        try {
            const resp = await fetch(`/User/Location/PreviewCount?${params}`);
            const count = await resp.json();
            applyBtn.textContent = `Apply to ${count} locations`;
        } catch (e) {
            console.error('Failed to fetch preview count', e);
        }
    }

    // When country changes: fetch regions, clear place, update count
    countrySelect.addEventListener('change', async () => {
        try {
            const resp = await fetch(`/User/Location/GetRegions?country=${encodeURIComponent(countrySelect.value)}`);
            const regions = await resp.json();
            populate(regionSelect, regions);
            populate(placeSelect, []);
            await updateCount();
        } catch (e) {
            console.error('Failed to fetch regions', e);
        }
    });

    // When region changes: fetch places, update count
    regionSelect.addEventListener('change', async () => {
        try {
            const params = new URLSearchParams({
                country: countrySelect.value,
                region: regionSelect.value
            });
            const resp = await fetch(`/User/Location/GetPlaces?${params}`);
            const places = await resp.json();
            populate(placeSelect, places);
            await updateCount();
        } catch (e) {
            console.error('Failed to fetch places', e);
        }
    });

    // When place or dates change: update count
    [placeSelect, fromDateInput, toDateInput].forEach(el => el.addEventListener('change', updateCount));

    // Initial count
    updateCount();

    // ------------------ Quill Initialization ------------------
    var quill = new Quill('#notes', {
        theme: 'snow',
        placeholder: 'Add your notes...',
        modules: {
            clipboard: {matchVisual: false},
            toolbar: [
                [{'header': [1, 2, 3, 4, 5, 6, false]}],
                ['bold', 'italic', 'underline'],
                [{'list': 'ordered'}, {'list': 'bullet'}],
                ['link', 'image'],
                [{'font': []}],
                ['clean']
            ]
        }
    });

    // Prevent base64 images
    quill.on('text-change', function () {
        document.querySelectorAll('.ql-editor img').forEach(img => {
            if (img.src.startsWith('data:image')) img.remove();
        });
    });

    // Add Clear button
    var toolbar = quill.getModule('toolbar');
    var clearBtn = document.createElement('span');
    clearBtn.classList.add('ql-clear');
    clearBtn.innerText = 'Clear';
    toolbar.container.appendChild(clearBtn);
    clearBtn.addEventListener('click', () => quill.setText(''));

    // Image handler
    toolbar.addHandler('image', function () {
        new bootstrap.Modal(document.getElementById('imageModal')).show();
    });

    let savedRange = null;
    quill.on('selection-change', range => {
        if (range) savedRange = range;
    });

    document.getElementById('insertImageBtn').addEventListener('click', () => {
        const url = document.getElementById('imageUrl').value;
        if (!url) return alert('Please enter a valid image URL.');
        if (savedRange) {
            quill.setSelection(savedRange.index, savedRange.length);
            quill.insertEmbed(savedRange.index, 'image', url);
        } else quill.insertEmbed(quill.getLength(), 'image', url);
        bootstrap.Modal.getInstance(document.getElementById('imageModal')).hide();
    });

    document.getElementById('imageModal').addEventListener('hidden.bs.modal', () => {
        document.getElementById('imageUrl').value = '';
    });

    // Load existing notes
    quill.root.innerHTML = document.getElementById('notes').dataset.notesContent || '';

    // On submit copy HTML
    document.getElementById('locationForm')
        .addEventListener('submit', () => {
            document.getElementById('hiddenNotes').value = quill.root.innerHTML;
        });
});
