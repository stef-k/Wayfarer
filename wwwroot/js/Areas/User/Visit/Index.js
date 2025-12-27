/**
 * Visit Index - Paginated table view of all user visits
 * Provides search, filtering, bulk delete, and view/edit actions.
 */

import {
    formatViewerAndSourceTimes,
    formatDate,
    currentDateInputValue,
    getViewerTimeZone,
} from '../../../util/datetime.js';

let visits = [];
let currentPage = 1;
const pageSize = 20;
let currentFilters = {};

const viewerTimeZone = getViewerTimeZone();
const returnUrlParam = encodeURIComponent(`${window.location.pathname}${window.location.search}`);
const buildEditUrl = id => `/User/Visit/Edit/${id}?returnUrl=${returnUrlParam}`;

/**
 * Proxy external image URLs in HTML content for display
 * @param {string} html - HTML content with potential external images
 * @returns {string} - HTML with image sources proxied
 */
const proxyImagesInHtml = (html) => {
    if (!html) return '';
    const div = document.createElement('div');
    div.innerHTML = html;
    div.querySelectorAll('img').forEach(img => {
        const src = img.getAttribute('src');
        if (src && !src.startsWith('data:') && !src.startsWith('/Public/ProxyImage')) {
            img.setAttribute('src', `/Public/ProxyImage?url=${encodeURIComponent(src)}`);
        }
    });
    return div.innerHTML;
};

/**
 * Format dwell time for display
 */
const formatDwellTime = (arrivedAt, endedAt) => {
    if (!endedAt) return '<span class="badge bg-warning text-dark">Open</span>';

    const arrived = new Date(arrivedAt);
    const ended = new Date(endedAt);
    const diffMs = ended - arrived;
    const diffMins = Math.floor(diffMs / 60000);

    if (diffMins < 60) {
        return `${diffMins} min`;
    }

    const hours = Math.floor(diffMins / 60);
    const mins = diffMins % 60;
    return `${hours}h ${mins}m`;
};

/**
 * Format visit timestamp for display
 */
const formatVisitTimestamp = (isoTimestamp) => {
    if (!isoTimestamp) return '-';
    const date = new Date(isoTimestamp);
    return date.toLocaleString(undefined, {
        year: 'numeric',
        month: 'short',
        day: 'numeric',
        hour: '2-digit',
        minute: '2-digit'
    });
};

document.addEventListener('DOMContentLoaded', () => {
    const selectAllCheckbox = document.getElementById('selectAll');
    const tableBody = document.querySelector('#visitsTable tbody');
    const today = currentDateInputValue();

    if (document.getElementById('toDate')) {
        document.getElementById('toDate').max = today;
    }

    // Initial load
    loadVisits();
    loadTripOptions();

    // Row checkbox UI
    tableBody.addEventListener('change', e => {
        if (e.target.name === 'visitCheckbox') {
            const all = [...tableBody.querySelectorAll('input[name="visitCheckbox"]')];
            const checked = all.filter(cb => cb.checked).length;
            selectAllCheckbox.checked = checked === all.length;
            selectAllCheckbox.indeterminate = checked > 0 && checked < all.length;
        }
    });

    selectAllCheckbox.addEventListener('change', () => {
        const all = tableBody.querySelectorAll('input[name="visitCheckbox"]');
        all.forEach(cb => cb.checked = selectAllCheckbox.checked);
    });

    // Toggle search panel
    document.getElementById('searchToggleBtn').onclick = () => {
        const panel = document.getElementById('advancedSearchPanel');
        panel.style.display = panel.style.display === 'none' ? 'block' : 'none';
    };

    // Clear search
    document.getElementById('clearSearchForm').onclick = () => {
        document.getElementById('advancedSearchForm').reset();
        currentFilters = {};
        loadVisits(1);
    };

    // Search submit
    document.getElementById('advancedSearchForm').onsubmit = e => {
        e.preventDefault();
        currentFilters = {
            fromDate: document.getElementById('fromDate').value,
            toDate: document.getElementById('toDate').value,
            tripId: document.getElementById('tripFilter').value,
            status: document.getElementById('statusFilter').value,
            placeName: document.getElementById('placeNameFilter').value,
            regionName: document.getElementById('regionFilter').value,
        };
        loadVisits(1);
    };

    // Row actions (view)
    document.querySelector('#visitsTable').addEventListener('click', e => {
        if (e.target.matches('.view-visit')) {
            e.preventDefault();
            const id = e.target.dataset.id;
            const visit = visits.find(v => v.id === id);
            viewVisitDetails(visit);
        }
    });

    // Delete from modal
    document.addEventListener('click', e => {
        const del = e.target.closest('.delete-visit-from-popup');
        if (del) {
            e.preventDefault();
            const id = del.dataset.visitId;
            const modalEl = document.getElementById('visitModal');
            const modalInstance = bootstrap.Modal.getInstance(modalEl);

            wayfarer.showConfirmationModal({
                title: 'Confirm Deletion',
                message: 'Are you sure you want to delete this visit? This cannot be undone.',
                confirmText: 'Delete',
                onConfirm: () => {
                    deleteVisit(id).then(() => {
                        loadVisits(currentPage);
                        modalInstance.hide();
                    });
                }
            });
        }
    });

    // Pagination
    window.goToPage = (page) => loadVisits(page);

    // Bulk delete
    document.getElementById('deleteSelected').addEventListener('click', () => {
        const selectedIds = Array.from(
            document.querySelectorAll('input[name="visitCheckbox"]:checked')
        ).map(cb => cb.value);

        if (selectedIds.length === 0) {
            alert('No visits selected');
            return;
        }

        wayfarer.showConfirmationModal({
            title: 'Confirm Bulk Deletion',
            message: `Are you sure you want to delete ${selectedIds.length} visit(s)? This cannot be undone.`,
            confirmText: 'Delete',
            onConfirm: async () => {
                try {
                    const response = await fetch('/api/Visit/bulk-delete', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ visitIds: selectedIds })
                    });
                    const data = await response.json();
                    if (data.success) {
                        loadVisits(currentPage);
                    } else {
                        alert('Bulk delete failed: ' + (data.message || 'Unknown error'));
                    }
                } catch (err) {
                    console.error(err);
                    wayfarer.showAlert('danger', 'Could not delete visits');
                }
            }
        });
    });
});

/**
 * Load visits with pagination and filters
 */
const loadVisits = async (page = 1) => {
    currentPage = page;

    const params = new URLSearchParams({ page, pageSize });

    Object.entries(currentFilters).forEach(([key, value]) => {
        if (value != null && value !== '') {
            params.append(key, value);
        }
    });

    try {
        const response = await fetch(`/api/Visit/search?${params.toString()}`, { credentials: 'include' });

        if (!response.ok) {
            console.error('Visit API error:', response.status, response.statusText);
            return;
        }

        const res = await response.json();

        visits = res.data || [];
        displayVisitsInTable(visits);
        updatePagination(res.totalItems || 0, currentPage);
        updateSummary(res.totalItems || 0);

        const selectAll = document.getElementById('selectAll');
        selectAll.checked = false;
        selectAll.indeterminate = false;
    } catch (err) {
        console.error('[Visit] Failed to load visits:', err);
    }
};

/**
 * Load trip options for filter dropdown
 */
const loadTripOptions = async () => {
    try {
        const response = await fetch('/api/Visit/trips', { credentials: 'include' });
        const trips = await response.json();

        const select = document.getElementById('tripFilter');
        trips.forEach(trip => {
            const option = document.createElement('option');
            option.value = trip.id;
            option.textContent = trip.name;
            select.appendChild(option);
        });
    } catch (err) {
        console.error('Failed to load trips:', err);
    }
};

/**
 * Display visits in table
 */
const displayVisitsInTable = (visits) => {
    const tbody = document.querySelector('#visitsTable tbody');
    tbody.innerHTML = '';

    if (!visits.length) {
        tbody.innerHTML = '<tr><td colspan="8" class="text-center">No visits found.</td></tr>';
        return;
    }

    for (const v of visits) {
        const statusBadge = v.endedAtUtc
            ? '<span class="badge bg-success">Closed</span>'
            : '<span class="badge bg-warning text-dark">Open</span>';

        tbody.insertAdjacentHTML('beforeend', `
            <tr>
                <td><input type="checkbox" name="visitCheckbox" value="${v.id}"></td>
                <td>
                    <i class="bi bi-clock me-1"></i>
                    ${formatVisitTimestamp(v.arrivedAtUtc)}
                </td>
                <td>
                    <strong>${v.placeNameSnapshot || 'Unknown'}</strong>
                </td>
                <td>${v.tripNameSnapshot || 'Unknown'}</td>
                <td>${v.regionNameSnapshot || '-'}</td>
                <td class="text-center">${formatDwellTime(v.arrivedAtUtc, v.endedAtUtc)}</td>
                <td class="text-center">${statusBadge}</td>
                <td>
                    <a href="#" class="btn btn-primary btn-sm view-visit" data-id="${v.id}">View</a>
                    <a href="${buildEditUrl(v.id)}" class="btn btn-secondary btn-sm">Edit</a>
                </td>
            </tr>
        `);
    }
};

/**
 * Update pagination controls
 */
const updatePagination = (totalItems, currentPage) => {
    const totalPages = Math.ceil(totalItems / pageSize);
    const container = document.getElementById('pagination');

    if (totalPages <= 1) {
        container.innerHTML = '';
        return;
    }

    window._totalPages = totalPages;

    const delta = 3;
    const start = Math.max(1, currentPage - delta);
    const end = Math.min(totalPages, currentPage + delta);

    let html = `
        <nav aria-label="Page navigation" class="mt-2">
            <div class="d-flex justify-content-center align-items-center">
                <ul class="pagination mb-0">
                    <li class="page-item ${currentPage === 1 ? 'disabled' : ''}">
                        <a class="page-link" href="#" onclick="goToPage(1)">First</a>
                    </li>
                    <li class="page-item ${currentPage === 1 ? 'disabled' : ''}">
                        <a class="page-link" href="#" onclick="goToPage(${Math.max(1, currentPage - 1)})">Previous</a>
                    </li>`;

    if (start > 1) {
        html += '<li class="page-item disabled"><span class="page-link">...</span></li>';
    }

    for (let i = start; i <= end; i++) {
        html += `
            <li class="page-item ${i === currentPage ? 'active' : ''}">
                <a class="page-link" href="#" onclick="goToPage(${i})">${i}</a>
            </li>`;
    }

    if (end < totalPages) {
        html += '<li class="page-item disabled"><span class="page-link">...</span></li>';
    }

    html += `
                    <li class="page-item ${currentPage === totalPages ? 'disabled' : ''}">
                        <a class="page-link" href="#" onclick="goToPage(${Math.min(totalPages, currentPage + 1)})">Next</a>
                    </li>
                    <li class="page-item ${currentPage === totalPages ? 'disabled' : ''}">
                        <a class="page-link" href="#" onclick="goToPage(${totalPages})">Last</a>
                    </li>
                </ul>
                <div class="input-group input-group-sm ms-2" style="width:120px;">
                    <input type="number" id="jumpPageInput" class="form-control" min="1" max="${totalPages}" placeholder="Page" />
                    <button class="btn btn-outline-secondary" type="button" onclick="jumpToPage()">Go</button>
                </div>
            </div>
        </nav>`;

    container.innerHTML = html;
};

window.jumpToPage = function () {
    const inp = document.getElementById('jumpPageInput');
    const val = parseInt(inp.value, 10);
    if (!isNaN(val) && val >= 1 && val <= window._totalPages) {
        goToPage(val);
    } else {
        inp.value = '';
    }
};

/**
 * Update summary display
 */
const updateSummary = (total) => {
    const summary = document.getElementById('visit-summary');
    if (summary) {
        summary.textContent = `Total: ${total} visit${total !== 1 ? 's' : ''}`;
    }
};

/**
 * View visit details in modal
 */
const viewVisitDetails = (visit) => {
    const content = document.getElementById('modalContent');
    content.innerHTML = generateVisitModalContent(visit);
    new bootstrap.Modal(document.getElementById('visitModal')).show();
};

/**
 * Generate modal content for visit details
 */
const generateVisitModalContent = (v) => {
    const dwellTime = formatDwellTime(v.arrivedAtUtc, v.endedAtUtc);
    const statusBadge = v.endedAtUtc
        ? '<span class="badge bg-success">Closed</span>'
        : '<span class="badge bg-warning text-dark">Open</span>';

    return `
        <div class="container-fluid">
            <div class="row mb-3">
                <div class="col-12">
                    <h5>${v.placeNameSnapshot || 'Unknown Place'}</h5>
                    <small class="text-muted">${v.tripNameSnapshot} / ${v.regionNameSnapshot}</small>
                </div>
            </div>
            <div class="row mb-2">
                <div class="col-6">
                    <strong>Arrived:</strong><br/>
                    ${formatVisitTimestamp(v.arrivedAtUtc)}
                </div>
                <div class="col-6">
                    <strong>Ended:</strong><br/>
                    ${v.endedAtUtc ? formatVisitTimestamp(v.endedAtUtc) : '<em>Still visiting</em>'}
                </div>
            </div>
            <div class="row mb-2">
                <div class="col-6">
                    <strong>Dwell Time:</strong> ${dwellTime}
                </div>
                <div class="col-6">
                    <strong>Status:</strong> ${statusBadge}
                </div>
            </div>
            ${v.latitude && v.longitude ? `
            <div class="row mb-2">
                <div class="col-12">
                    <strong>Location:</strong> ${v.latitude.toFixed(6)}, ${v.longitude.toFixed(6)}
                    <a href="https://www.google.com/maps/search/?api=1&query=${v.latitude},${v.longitude}"
                       target="_blank" class="ms-2 btn btn-outline-primary btn-sm">
                        <i class="bi bi-globe-europe-africa"></i> Maps
                    </a>
                </div>
            </div>
            ` : ''}
            ${v.notesHtml ? `
            <div class="row mb-2">
                <div class="col-12">
                    <strong>Notes:</strong>
                    <div class="border p-2 mt-1">${proxyImagesInHtml(v.notesHtml)}</div>
                </div>
            </div>
            ` : ''}
            <hr/>
            <div class="row">
                <div class="col-6">
                    <a href="${buildEditUrl(v.id)}" class="btn btn-primary btn-sm">Edit</a>
                </div>
                <div class="col-6 text-end">
                    <a href="#" class="btn btn-outline-danger btn-sm delete-visit-from-popup" data-visit-id="${v.id}">Delete</a>
                </div>
            </div>
        </div>
    `;
};

/**
 * Delete a single visit
 */
const deleteVisit = async (id) => {
    const response = await fetch('/api/Visit/bulk-delete', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ visitIds: [id] })
    });
    const data = await response.json();
    if (!data.success) {
        throw new Error(data.message || 'Delete failed');
    }
};
