// AllLocations.js
import {
    formatViewerAndSourceTimes,
    formatDate,
    formatDecimal,
    currentDateInputValue,
    getViewerTimeZone,
} from '../../../util/datetime.js';
import {
    generateActivityEditorHtml,
    initActivitySelect,
    setupActivityEditorEvents,
} from '../../../util/activity-editor.js';
import {
    generateWikipediaLinkHtml,
    initWikipediaPopovers,
} from '../../../util/wikipedia-utils.js';

let locations = [];
let currentPage = 1;
const pageSize = 20;
let currentFilters = {};

const viewerTimeZone = getViewerTimeZone();
const getLocationSourceTimeZone = location => location?.timezone || location?.timeZoneId || location?.timeZone || null;
const getLocationTimestampInfo = location => formatViewerAndSourceTimes({
    iso: location?.localTimestamp,
    sourceTimeZone: getLocationSourceTimeZone(location),
    viewerTimeZone,
});
const returnUrlParam = encodeURIComponent(`${window.location.pathname}${window.location.search}`);
const buildEditUrl = id => `/User/Location/Edit/${id}?returnUrl=${returnUrlParam}`;
const syncLocationActivity = (locationId, activityType) => {
    // Keep in-memory location data aligned with table/modal edits.
    const target = locations.find(loc => loc.id === locationId);
    if (!target) return;

    target.activityType = activityType || null;
    if (Object.prototype.hasOwnProperty.call(target, 'activity')) {
        target.activity = activityType || null;
    }
};

const renderTimestampBlock = location => {
    const info = getLocationTimestampInfo(location);
    const sourceLabel = info.source
        ? `<div class="small text-muted">Recorded: ${info.source}</div>`
        : `<div class="small text-muted fst-italic">Recorded timezone unavailable</div>`;
    return `<div>${info.viewer}</div>${sourceLabel}`;
};

document.addEventListener('DOMContentLoaded', () => {
    const selectAllCheckbox = document.getElementById("selectAll");
    const tableBody = document.querySelector("#locationsTable tbody");
    const today = currentDateInputValue();
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
            wayfarer.showConfirmationModal({
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

    // INIT MODAL WIKI POP-OVERS and ACTIVITY EDITOR
    const modalEl = document.getElementById('locationModal');
    if (modalEl) {
        modalEl.addEventListener('shown.bs.modal', () => {
            initWikipediaPopovers(modalEl);
            // Initialize TomSelect for activity editor if present
            const activityEditor = modalEl.querySelector('.activity-editor');
            if (activityEditor) {
                const locationId = activityEditor.dataset.locationId;
                initActivitySelect(locationId);
            }
        });
    }

    // Set up event delegation for activity editor save/clear buttons.
    setupActivityEditorEvents('#modalContent', syncLocationActivity);
    setupActivityEditorEvents('#locationsTable', syncLocationActivity);

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
        wayfarer.showConfirmationModal({
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
                        wayfarer.showAlert('danger', typeof err === 'string' ? err : 'Could not delete locations');
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
        const timestampHtml = renderTimestampBlock(loc);
        // Embed the inline activity editor for table rows.
        const activityEditorHtml = generateActivityEditorHtml(loc, { showLabel: false, compact: true });
        tbody.insertAdjacentHTML('beforeend', `
      <tr>
        <td><input type="checkbox" name="locationCheckbox" value="${loc.id}"></td>
        <td>
          <i class="bi bi-clock me-1"></i>
          ${timestampHtml}
        </td>
        <td>${loc.coordinates.latitude}</td>
        <td>${loc.coordinates.longitude}</td>
        <td class="text-center">${formatDecimal(loc.accuracy) != null ? formatDecimal(loc.accuracy) : '<i class="bi bi-patch-question" title="No available data for Accuracy"></i>'}</td>
        <td class="text-center">${formatDecimal(loc.speed) != null ? formatDecimal(loc.speed) : '<i class="bi bi-patch-question" title="No available data for Speed"></i>'}</td>
        <td class="text-center">${formatDecimal(loc.altitude) != null ? formatDecimal(loc.altitude) : '<i class="bi bi-patch-question" title="No available data for Altitude"></i>'}</td>
        <td>${activityEditorHtml}</td>
        <td>${loc.fullAddress || '<i class="bi bi-patch-question" title="No available data for Address"></i>'}</td>
        <td>${loc.place || '<i class="bi bi-patch-question" title="No available data for Place"></i>'}</td>
        <td>${loc.country || '<i class="bi bi-patch-question" title="No available data for Country"></i>'}</td>
        <td>
          <a href="#" class="btn btn-primary btn-sm view-location" data-id="${loc.id}">View</a>
          <a href="${buildEditUrl(loc.id)}" class="btn btn-secondary btn-sm">Edit</a>
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
    const timestamps = getLocationTimestampInfo(location);
    const sourceZone = getLocationSourceTimeZone(location);
    const recordedBlock = timestamps.source
        ? `<div>${timestamps.source}</div>`
        : `<div class="fst-italic text-muted">Source timezone unavailable</div>`;
    return `
    <div class="container-fluid">
      <div class="row mb-2">
        <div class="col-6">
            <strong>Datetime (your timezone):</strong>
            <div>${timestamps.viewer}</div>
        </div>
        <div class="col-6">
            <strong>Recorded local time:</strong>
              ${recordedBlock}
              ${sourceZone && !timestamps.source ? `<div class="small text-muted">${sourceZone}</div>` : ''}
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
        <div class="col-6">${generateActivityEditorHtml(location)}</div>
        <div class="col-6">
            <strong>Altitude:</strong> ${formatDecimal(location.altitude) != null ? formatDecimal(location.altitude) + ' m' : '<i class="bi bi-patch-question" title="No available data for Altitude"></i>'}
        </div>
      </div>
      <div class="row mb-2">
        <div class="col-12">
            <strong>Address:</strong> ${location.fullAddress || '<i class="bi bi-patch-question" title="No available data for Address"></i>'} <br>
                ${generateGoogleMapsLink(location)}
            ${generateWikipediaLinkHtml(location, { query: location.place || location.fullAddress })}
        </div>
      </div>
      <div class="row mb-2">
        <div class="col-12" style="${style}">
            <strong>Notes:</strong><div class="border p-1">${location.notes || ''}
        </div>
      </div>
      <div class="row">
        <div class="col-5">
            <a href="${buildEditUrl(location.id)}" class="btn-link">Edit</a>
        </div>
        <div class="col-5 offset-2">
            <a href="#" class="btn-link text-danger delete-location-from-popup" data-location-id="${location.id}">Delete</a>
        </div>
      </div>
    </div>
  `;
}

/**
 * Generates a Google Maps link combining address and coordinates for precision.
 * @param {{ fullAddress?: string, coordinates: { latitude: number, longitude: number } }} location
 */
const generateGoogleMapsLink = (location) => {
    const addr = location?.fullAddress || '';
    const lat  = location?.coordinates?.latitude;
    const lon  = location?.coordinates?.longitude;
    const hasCoords = Number.isFinite(+lat) && Number.isFinite(+lon);
    const query = addr && hasCoords
        ? `${addr} (${(+lat).toFixed(6)},${(+lon).toFixed(6)})`
        : hasCoords
            ? `${(+lat).toFixed(6)},${(+lon).toFixed(6)}`
            : addr;
    const q = encodeURIComponent(query || '');
    return `<a href="https://www.google.com/maps/search/?api=1&query=${q}" target="_blank" class="ms-2 btn btn-outline-primary btn-sm" title="View in Google Maps">
    <i class="bi bi-globe-europe-africa"></i> Maps
</a>`;
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
        summaryParts.push(`<strong>From Date:</strong> ${formatDate({ iso: stats.fromDate, displayTimeZone: viewerTimeZone })}`);
    if (stats.toDate)
        summaryParts.push(`<strong>To Date:</strong> ${formatDate({ iso: stats.toDate, displayTimeZone: viewerTimeZone })}`);
    if (stats.countriesVisited != null)
        summaryParts.push(`<strong>Countries:</strong> ${stats.countriesVisited}`);
    if (stats.regionsVisited != null)
        summaryParts.push(`<strong>Regions:</strong> ${stats.regionsVisited}`);
    if (stats.citiesVisited != null)
        summaryParts.push(`<strong>Cities:</strong> ${stats.citiesVisited}`);

    const summary = summaryParts.join(" | ");
    document.getElementById("timeline-summary").innerHTML  = summary;
};
