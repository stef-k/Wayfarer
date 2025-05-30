// AllLocations.js
let locations = [];
let currentPage = 1;
const pageSize = 20;
let currentFilters = {};

document.addEventListener('DOMContentLoaded', () => {
    const selectAllCheckbox = document.getElementById("selectAll");
    const tableBody = document.querySelector("#locationsTable tbody");
    const today = new Date().toISOString().split('T')[0];
    document.getElementById('toTimestamp').max = today;

    // INITIAL LOAD
    loadLocations();

    // ROW CHECKBOX UI
    tableBody.addEventListener("change", e => {
        if (e.target.name === "locationCheckbox") {
            const all = [...tableBody.querySelectorAll('input[name="locationCheckbox"]')];
            const checked = all.filter(cb => cb.checked).length;
            selectAllCheckbox.checked = checked === all.length;
            selectAllCheckbox.indeterminate = checked > 0 && checked < all.length;
        }
    });
    selectAllCheckbox.addEventListener("change", () => {
        const all = tableBody.querySelectorAll('input[name="locationCheckbox"]');
        all.forEach(cb => cb.checked = selectAllCheckbox.checked);
    });

    // TOGGLE SEARCH PANEL
    document.getElementById('searchToggleBtn').onclick = () => {
        const panel = document.getElementById('advancedSearchPanel');
        panel.style.display = panel.style.display === 'none' ? 'block' : 'none';
    };

    // CLEAR SEARCH
    document.getElementById('clearSearchForm').onclick = () => {
        document.getElementById('advancedSearchForm').reset();
        currentFilters = {};
        loadLocations(1, {});
    };

    // SEARCH SUBMIT
    document.getElementById('advancedSearchForm').onsubmit = e => {
        e.preventDefault();
        currentFilters = {
            fromTimestamp: document.getElementById('fromTimestamp').value,
            toTimestamp: document.getElementById('toTimestamp').value,
            latitude: document.getElementById('latitude').value,
            longitude: document.getElementById('longitude').value,
            activity: document.getElementById('activity').value,
            address: document.getElementById('address').value,
            country: document.getElementById('country').value,
            place: document.getElementById('place').value,
            region: document.getElementById('region').value,
            notes: document.getElementById('notes').value,
        };
        loadLocations(1, currentFilters);
    };

    // ROW ACTIONS (view, edit, delete-in-modal)
    document.querySelector('#locationsTable').addEventListener('click', e => {
        if (e.target.matches('.view-location')) {
            e.preventDefault();
            const id = +e.target.dataset.id;
            const loc = locations.find(l => l.id === id);
            viewLocationDetails(loc);
        }
    });
    document.addEventListener('click', e => {
        const del = e.target.closest('.delete-location-from-popup');
        if (del) {
            e.preventDefault();
            const id = +del.dataset.locationId;
            // Get the current open modal instance
            const modalEl = document.getElementById('locationModal');
            const modalInstance = bootstrap.Modal.getInstance(modalEl);
            showConfirmationModal({
                title: "Confirm Deletion",
                message: "Are you sure? This cannot be undone.",
                confirmText: "Delete",
                onConfirm: () => {
                    fetch('/api/Location/bulk-delete', {
                        method: 'POST',
                        headers: {'Content-Type': 'application/json'},
                        body: JSON.stringify({locationIds: [id]})
                    })
                        .then(r => r.json())
                        .then(d => {
                            if (d.success) {
                                loadLocations(currentPage);
                                // Hide the modal now that the record is gone
                                modalInstance.hide();
                            }
                            else alert('Delete failed');
                        });
                }
            });
        }
    });

    // PAGINATION (dynamic links)
    window.goToPage = (page) => loadLocations(page);

    // INIT MODAL WIKI POP-OVERS
    const modalEl = document.getElementById('locationModal');
    if (modalEl) {
        modalEl.addEventListener('shown.bs.modal', () => {
            initWikipediaPopovers(modalEl);
        });
    }

    // BULK DELETE
    document.getElementById('deleteSelected').addEventListener('click', () => {
        // 1) Gather all checked row-IDs
        const selectedIds = Array.from(
            document.querySelectorAll('input[name="locationCheckbox"]:checked')
        ).map(cb => +cb.value);

        if (selectedIds.length === 0) {
            alert('No locations selected');
            return;
        }

        // 2) Confirm, then POST to /bulk-delete
        showConfirmationModal({
            title: 'Confirm Bulk Deletion',
            message: `Are you sure you want to delete ${selectedIds.length} location(s)? This cannot be undone.`,
            confirmText: 'Delete',
            onConfirm: () => {
                fetch('/api/Location/bulk-delete', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ locationIds: selectedIds })
                })
                    .then(r => r.json())
                    .then(d => {
                        if (d.success) {
                            loadLocations(currentPage);   // refresh current page
                        } else {
                            alert('Bulk delete failed: ' + (d.message || 'Unknown error'));
                        }
                    })
                    .catch(err => {
                        console.error(err);
                        showAlert('danger', typeof err === 'string' ? err : 'Could not delete locations');
                    });
            }
        });
    });

    getUserStats();
});

const loadLocations = (page = 1) => {
    currentPage = page;

    const params = new URLSearchParams({
        page,
        pageSize
    });

    // append any filters the user set (will be {} on initial load)
    Object.entries(currentFilters).forEach(([key, value]) => {
        if (value != null && value !== '') {
            params.append(key, value);
        }
    });

    const url = `/api/Location/search?${params.toString()}`;

    fetch(url, { credentials: 'include' })
        .then(r => r.json())
        .then(res => {
            locations = res.data || res.Data || [];
            displayLocationsInTable(locations);
            updatePagination(res.TotalItems || res.totalItems, currentPage);

            // clear the “select all” checkbox
            const selectAll = document.getElementById('selectAll');
            selectAll.checked = false;
            selectAll.indeterminate = false;
        })
        .catch(console.error);
};


const displayLocationsInTable = (locations) => {
    const tbody = document.querySelector('#locationsTable tbody');
    tbody.innerHTML = '';
    if (!locations.length) {
        tbody.innerHTML = `<tr><td colspan="11" class="text-center">No locations match the criteria.</td></tr>`;
        return;
    }
    for (const loc of locations) {
        tbody.insertAdjacentHTML('beforeend', `
      <tr>
        <td><input type="checkbox" name="locationCheckbox" value="${loc.id}"></td>
        <td>${new Date(loc.localTimestamp).toISOString().replace('T', ' ').slice(0, 19)} 
        <i class="bi bi-clock"></i> <span class="text-muted" title="Timezone">${loc.timezone || loc.timeZoneId}</span></td>
        <td>${loc.coordinates.latitude}</td>
        <td>${loc.coordinates.longitude}</td>
        <td class="text-center">${loc.accuracy ?? '<i class="bi bi-patch-question" title="No available data for Accuracy"></i>'}</td>
        <td class="text-center">${loc.altitude ?? '<i class="bi bi-patch-question" title="No available data for Altitude"></i>'}</td>
        <td>${loc.activityType || '<i class="bi bi-patch-question" title="No available data for Activity"></i>'}</td>
        <td>${loc.fullAddress || '<i class="bi bi-patch-question" title="No available data for Address"></i>'}</td>
        <td>${loc.place || '<i class="bi bi-patch-question" title="No available data for Place"></i>'}</td>
        <td>${loc.country || '<i class="bi bi-patch-question" title="No available data for Country"></i>'}</td>
        <td>
          <a href="#" class="btn btn-primary btn-sm view-location" data-id="${loc.id}">View</a>
          <a href="/User/Location/Edit/${loc.id}" class="btn btn-secondary btn-sm">Edit</a>
        </td>
      </tr>
    `);
    }
}

const updatePagination = (totalItems, currentPage) => {
    const totalPages = Math.ceil(totalItems / pageSize);
    const container  = document.getElementById('pagination');
    if (totalPages <= 1) {
        container.innerHTML = ''; // no pager needed
        return;
    }

    // remember for jumpToPage
    window._totalPages = totalPages;

    // sliding window
    const delta = 3;
    const start = Math.max(1, currentPage - delta);
    const end   = Math.min(totalPages, currentPage + delta);

    // build the nav
    let html = `
<nav aria-label="Page navigation" class="mt-2">
  <div class="d-flex justify-content-center align-items-center">
    <ul class="pagination mb-0">
      <li class="page-item ${currentPage===1 ? 'disabled':''}">
        <a class="page-link" href="#" onclick="goToPage(1)">First</a>
      </li>
      <li class="page-item ${currentPage===1 ? 'disabled':''}">
        <a class="page-link" href="#" onclick="goToPage(${Math.max(1, currentPage-1)})">Previous</a>
      </li>`;

    if (start>1) {
        html += `
      <li class="page-item disabled">
        <span class="page-link">…</span>
      </li>`;
    }

    for (let i=start; i<=end; i++) {
        html += `
      <li class="page-item ${i===currentPage ? 'active':''}">
        <a class="page-link" href="#" onclick="goToPage(${i})">${i}</a>
      </li>`;
    }

    if (end<totalPages) {
        html += `
      <li class="page-item disabled">
        <span class="page-link">…</span>
      </li>`;
    }

    html += `
      <li class="page-item ${currentPage===totalPages ? 'disabled':''}">
        <a class="page-link" href="#" onclick="goToPage(${Math.min(totalPages, currentPage+1)})">Next</a>
      </li>
      <li class="page-item ${currentPage===totalPages ? 'disabled':''}">
        <a class="page-link" href="#" onclick="goToPage(${totalPages})">Last</a>
      </li>
    </ul>

    <!-- Jump-to-Page input -->
    <div class="input-group input-group-sm ms-2" style="width:120px;">
      <input
        type="number"
        id="jumpPageInput"
        class="form-control"
        min="1" max="${totalPages}"
        placeholder="Page"
      />
      <button
        class="btn btn-outline-secondary"
        type="button"
        onclick="jumpToPage()"
      >Go</button>
    </div>

  </div>
</nav>`;

    container.innerHTML = html;
}

// helper to read the input, clamp it, and call goToPage
window.jumpToPage = function() {
    const inp = document.getElementById('jumpPageInput');
    const val = parseInt(inp.value, 10);
    if (!isNaN(val) && val >= 1 && val <= window._totalPages) {
        goToPage(val);
    } else {
        // optional: reset invalid entries
        inp.value = '';
    }
};

const viewLocationDetails = (loc) => {
    document.getElementById('modalContent').innerHTML = generateLocationModalContent(loc);
    new bootstrap.Modal(document.getElementById('locationModal')).show();
}

// --- Modal content & helpers ---
const generateLocationModalContent = (location) => {
    const charCount = location.notes ? location.notes.length : 0;
    const style = charCount === 0 ? 'display:none;' : `min-height:${16 * Math.ceil(charCount / 50) * 1.5}px;`;
    return `
    <div class="container-fluid">
      <div class="row mb-2">
        <div class="col-6">
            <strong>Local Datetime:</strong> ${new Date(location.localTimestamp).toISOString().replace('T', ' ').split('.')[0]}
        </div>
        <div class="col-6">
            <strong>Timezone:</strong> ${location.timezone || location.timeZoneId}
        </div>
      </div>
      <div class="row mb-2">
        <div class="col-6">
            <strong>Latitude:</strong> ${location.coordinates.latitude}
        </div>
        <div class="col-6">
            <strong>Longitude:</strong> ${location.coordinates.longitude}
        </div>
      </div>
      <div class="row mb-2">
        <div class="col-6">
            <strong>Activity:</strong> ${location.activityType || '<i class="bi bi-patch-question" title="No available data for Activity"></i>'}
        </div>
        <div class="col-6">
            <strong>Altitude:</strong> ${location.altitude || '<i class="bi bi-patch-question" title="No available data for Altitude"></i>'}
        </div>
      </div>
      <div class="row mb-2">
        <div class="col-12">
            <strong>Address:</strong> ${location.fullAddress || '<i class="bi bi-patch-question" title="No available data for Address"></i>'} <br>
                ${location.fullAddress ? generateGoogleMapsLink(location.fullAddress) : '' }
            ${generateWikipediaLink(location)}
        </div>
      </div>
      <div class="row mb-2">
        <div class="col-12" style="${style}">
            <strong>Notes:</strong><div class="border p-1">${location.notes || ''}
        </div>
      </div>
      <div class="row">
        <div class="col-5">
            <a href="/User/Location/Edit/${location.id}" class="btn-link">Edit</a>
        </div>
        <div class="col-5 offset-2">
            <a href="#" class="btn-link text-danger delete-location-from-popup" data-location-id="${location.id}">Delete</a>
        </div>
      </div>
    </div>
  `;
}

const generateGoogleMapsLink = (address) => {
    const q = encodeURIComponent(address || '');
    return `<a href="https://www.google.com/maps/search/?api=1&query=${q}" target="_blank" class="ms-2" title="View in Google Maps">📍 Maps</a>`;
}

const generateWikipediaLink = (location) => {
    return `<a href="#" class="ms-2 wikipedia-link" data-lat="${location.coordinates.latitude}" data-lon="${location.coordinates.longitude}">📖 Wiki</a>`;
}

const initWikipediaPopovers = (modalEl) => {
    modalEl.querySelectorAll('.wikipedia-link').forEach(el => {
        tippy(el, {
            appendTo: () => document.body,
            popperOptions: {modifiers: [{name: 'zIndex', options: {value: 2000}}]},
            interactiveBorder: 20,
            content: 'Loading…',
            allowHTML: true,
            interactive: true,
            hideOnClick: false,
            placement: 'right',
            onShow: async instance => {
                if (instance._loaded) return;
                instance._loaded = true;
                const lat = el.dataset.lat;
                const lon = el.dataset.lon;
                try {
                    const geoUrl = new URL('https://en.wikipedia.org/w/api.php');
                    geoUrl.search = new URLSearchParams({
                        action: 'query',
                        list: 'geosearch',
                        gscoord: `${lat}|${lon}`,
                        gsradius: 100,
                        gslimit: 5,
                        format: 'json',
                        origin: '*'
                    });
                    const geoRes = await fetch(geoUrl);
                    const geoJson = await geoRes.json();
                    if (!geoJson.query.geosearch.length) return instance.setContent('<em>No nearby article</em>');
                    const title = encodeURIComponent(geoJson.query.geosearch[0].title);
                    const sumRes = await fetch(`https://en.wikipedia.org/api/rest_v1/page/summary/${title}`);
                    const data = await sumRes.json();
                    instance.setContent(`<strong>${data.title}</strong><p>${data.extract}</p><a href="${data.content_urls.desktop.page}" target="_blank">Read more »</a>`);
                } catch {
                    instance.setContent('<em>Could not load article.</em>');
                }
            }
        });
    });
}

const getUserStats = async () => {

    const url = `/api/users/stats`;

    const response = await fetch(url, {
        method: "GET",
        headers: {
            "Accept": "application/json"
        }
    });

    if (!response.ok) {
        // You can handle 404/403/other errors differently if you like
        const errorText = await response.text();
        throw new Error(`Error ${response.status}: ${errorText}`);
    }

    // assuming the response is JSON matching UserLocationStatsDto
    const stats = await response.json();
    const summaryParts = [];

    if (stats.totalLocations != null)
        summaryParts.push(`<strong>Total Locations:</strong> ${stats.totalLocations}`);
    if (stats.fromDate)
        summaryParts.push(`<strong>From Date:</strong> ${new Date(stats.fromDate).toISOString().split('T')[0]}`);
    if (stats.toDate)
        summaryParts.push(`<strong>To Date:</strong> ${new Date(stats.toDate).toISOString().split('T')[0]}`);
    if (stats.countriesVisited != null)
        summaryParts.push(`<strong>Countries:</strong> ${stats.countriesVisited}`);
    if (stats.regionsVisited != null)
        summaryParts.push(`<strong>Regions:</strong> ${stats.regionsVisited}`);
    if (stats.citiesVisited != null)
        summaryParts.push(`<strong>Cities:</strong> ${stats.citiesVisited}`);

    const summary = summaryParts.join(" | ");
    document.getElementById("timeline-summary").innerHTML  = summary;
};