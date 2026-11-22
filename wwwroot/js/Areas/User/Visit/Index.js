/**
 * Visit Index - Paginated table view of all user visits
 * Provides search, filtering, bulk delete, and view/edit actions.
 */

import { currentDateInputValue } from '../../../util/datetime.js';
import { date, message, number, plural } from '../../../localization/messages.js';
import { holdLanguageNavigation } from '../../../localization/languageNavigation.js';
import { encodeFeatureText as encode } from '../../../util/feature-metadata.js';
import { notesHtmlForDisplay } from '../../../util/notes-images.js';
import { renderHistoryPagination } from '../../../util/history-pagination.js';

let visits = [];
let currentPage = 1;
const pageSize = 20;
let currentFilters = {};

const returnUrlParam = encodeURIComponent(`${window.location.pathname}${window.location.search}`);
const buildEditUrl = id => `/User/Visit/Edit/${id}?returnUrl=${returnUrlParam}`;

/** Read existing Visit results and report known session expiry independently of other request failures. */
class VisitReadFailure extends Error {}

const readVisitResponse = async (response, failure) => {
    if (response.redirected || [401, 403].includes(response.status)) {
        throw new VisitReadFailure(message('Shell.SessionEnded', 'Your session has ended. Sign in again.'));
    }
    if (!response.ok) throw new VisitReadFailure(failure);
    return response.json();
};

/**
 * Format dwell time for display
 */
const formatDwellTime = (arrivedAt, endedAt) => {
    const arrived = new Date(arrivedAt);
    const ended = new Date(endedAt);
    const diffMs = ended - arrived;
    const diffMins = Math.floor(diffMs / 60000);

    if (diffMins < 60) {
        return message('Visits.Minutes', '{0} min', number(diffMins));
    }

    const hours = Math.floor(diffMins / 60);
    const mins = diffMins % 60;
    return message('Visits.HoursMinutes', '{0}h {1}m', number(hours), number(mins));
};

/**
 * Format visit timestamp for display
 */
const formatVisitTimestamp = (isoTimestamp) => {
    if (!isoTimestamp) return '-';
    return date(new Date(isoTimestamp), {
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

    // Both actions use the existing bulk endpoint. The detail modal closes after a confirmed single deletion.
    document.addEventListener('click', event => {
        const link = event.target.closest('.delete-visit-from-popup');
        if (!link) return;
        event.preventDefault();
        confirmVisitDeletion([link.dataset.visitId], () => {
            bootstrap.Modal.getInstance(document.getElementById('visitModal'))?.hide();
            loadVisits(currentPage);
        }, true);
    });
    document.getElementById('deleteSelected').addEventListener('click', () => {
        const ids = Array.from(document.querySelectorAll('input[name="visitCheckbox"]:checked'), row => row.value);
        if (ids.length === 0) {
            wayfarer.showAlert('warning', message('Visits.NoSelected', 'No visits selected.'));
            return;
        }
        confirmVisitDeletion(ids, () => loadVisits(currentPage));
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

    const feedback = document.getElementById('visit-read-status');
    try {
        const response = await fetch(`/api/Visit/search?${params.toString()}`, { credentials: 'include' });

        const res = await readVisitResponse(response, message('Visits.LoadFailed', 'Could not load visits.'));
        feedback.textContent = '';
        visits = res.data || [];
        displayVisitsInTable(visits);
        updatePagination(res.totalItems || 0, currentPage);
        updateSummary(res.totalItems || 0);

        const selectAll = document.getElementById('selectAll');
        selectAll.checked = false;
        selectAll.indeterminate = false;
    } catch (err) {
        console.error('[Visit] Failed to load visits:', err);
        feedback.textContent = err instanceof VisitReadFailure ? err.message : message('Visits.LoadFailed', 'Could not load visits.');
    }
};

/**
 * Load trip options for filter dropdown
 */
const loadTripOptions = async () => {
    try {
        const response = await fetch('/api/Visit/trips', { credentials: 'include' });
        const trips = await readVisitResponse(response, message('Visits.TripsFailed', 'Could not load the trips filter.'));

        const select = document.getElementById('tripFilter');
        trips.forEach(trip => {
            const option = document.createElement('option');
            option.value = trip.id;
            option.textContent = trip.name;
            select.appendChild(option);
        });
    } catch (err) {
        console.error('Failed to load trips:', err);
        document.getElementById('visit-filter-status').textContent = err instanceof VisitReadFailure
            ? err.message : message('Visits.TripsFailed', 'Could not load the trips filter.');
    }
};

/** Render the same Visit status in the row and the detail. */
const statusBadge = ended => ended
    ? `<span class="badge bg-success">${encode(message('Visits.Closed', 'Closed'))}</span>`
    : `<span class="badge bg-warning text-dark">${encode(message('Visits.Open', 'Open'))}</span>`;

/** Only the generated open badge contains markup. Duration remains plain display text. */
const dwellDisplay = (arrived, ended) => ended ? encode(formatDwellTime(arrived, ended)) : statusBadge(false);

/**
 * Display visits in table
 */
const displayVisitsInTable = (visits) => {
    const tbody = document.querySelector('#visitsTable tbody');
    tbody.innerHTML = '';

    if (!visits.length) {
        tbody.innerHTML = `<tr><td colspan="9" class="text-center">${encode(message('Visits.NoMatches', 'No visits found.'))}</td></tr>`;
        return;
    }

    for (const v of visits) {

        // Show spinner initially, will be replaced by actual count
        const locationSpinner = `<span class="location-count-cell" data-visit-id="${v.id}"><span class="spinner-border spinner-border-sm" role="status" aria-label="${encode(message('Shell.Loading', 'Loading…'))}"></span></span>`;

        tbody.insertAdjacentHTML('beforeend', `
            <tr>
                <td><input type="checkbox" name="visitCheckbox" aria-label="${encode(message('Shell.SelectRow', 'Select row'))}" value="${v.id}"></td>
                <td>
                    <i class="bi bi-clock me-1"></i>
                    ${formatVisitTimestamp(v.arrivedAtUtc)}
                </td>
                <td>
                    <strong>${encode(v.placeNameSnapshot || message('Shell.Unknown', 'Unknown'))}</strong>
                </td>
                <td>${encode(v.tripNameSnapshot || message('Shell.Unknown', 'Unknown'))}</td>
                <td>${encode(v.regionNameSnapshot || '-')}</td>
                <td class="text-center">${dwellDisplay(v.arrivedAtUtc, v.endedAtUtc)}</td>
                <td class="text-center">${locationSpinner}</td>
                <td class="text-center">${statusBadge(v.endedAtUtc)}</td>
                <td>
                    <a href="#" class="btn btn-primary btn-sm view-visit" data-id="${v.id}">${encode(message('Shell.View', 'View'))}</a>
                    <a href="${buildEditUrl(v.id)}" class="btn btn-secondary btn-sm">${encode(message('Shell.Edit', 'Edit'))}</a>
                </td>
            </tr>
        `);
    }

    // Lazy-load location counts after table renders
    loadLocationCounts(visits.map(v => v.id));
};

/**
 * Load location counts for visits and update the table cells
 */
const loadLocationCounts = async (visitIds) => {
    if (!visitIds.length) return;

    try {
        const response = await fetch('/api/Visit/location-counts', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            credentials: 'include',
            body: JSON.stringify({ visitIds })
        });

        const data = await readVisitResponse(response, message('Visits.CountsFailed', 'Could not load location counts.'));
        if (!data.success || !data.counts) throw new Error('Location counts response was incomplete');

        // Keep the fetched count available for the modal as well as the table.
        for (const [visitId, count] of Object.entries(data.counts)) {
            const cell = document.querySelector(`.location-count-cell[data-visit-id="${visitId}"]`);
            if (!cell) continue;
            cell.innerHTML = `<span class="badge ${count > 0 ? 'bg-info' : 'bg-secondary'}">${number(count)}</span>`;
            const visit = visits.find(v => v.id === visitId);
            if (visit) visit.locationCount = count;
        }
    } catch (e) {
        console.error('Failed to load location counts', e);
        // Replace spinners with error indicator
        document.querySelectorAll('.location-count-cell').forEach(cell => {
            if (cell.querySelector('.spinner-border')) {
                cell.innerHTML = `<span class="badge bg-secondary" title="${encode(message('Visits.CountsFailed', 'Could not load location counts.'))}">-</span>`;
            }
        });
    }
};

/** Visit paging retains the current filters and only changes the numeric page. */
const updatePagination = (totalItems, page) =>
    renderHistoryPagination(document.getElementById('pagination'), Math.ceil(totalItems / pageSize), page, loadVisits);

/**
 * Update summary display
 */
const updateSummary = (total) => {
    const summary = document.getElementById('visit-summary');
    if (summary) {
        summary.textContent = plural('Visits.Total', total, 'Total: {0} visit', 'Total: {0} visits');
    }
};

/**
 * View visit details in modal
 */
const viewVisitDetails = async (visit) => {
    const content = document.getElementById('modalContent');
    content.innerHTML = generateVisitModalContent(visit);
    new bootstrap.Modal(document.getElementById('visitModal')).show();

    // Load locations after showing modal (if count is known and > 0, or if still loading)
    if (visit.locationCount === undefined || visit.locationCount > 0) {
        await loadVisitLocationsForModal(visit.id);
    }
};

/**
 * Generate modal content for visit details
 */
const generateVisitModalContent = (v) => {
    const dwellTime = dwellDisplay(v.arrivedAtUtc, v.endedAtUtc);

    const locationBadge = v.locationCount === undefined
        ? `<span class="spinner-border spinner-border-sm" role="status" aria-label="${encode(message('Shell.Loading', 'Loading…'))}"></span>`
        : (v.locationCount > 0
            ? `<span class="badge bg-info">${number(v.locationCount)}</span>`
            : '<span class="badge bg-secondary">0</span>');

    return `
        <div class="container-fluid">
            <div class="row mb-3">
                <div class="col-12">
                    <h5>${encode(v.placeNameSnapshot || message('Visits.UnknownPlace', 'Unknown Place'))}</h5>
                    <small class="text-muted">${encode(v.tripNameSnapshot)} / ${encode(v.regionNameSnapshot)}</small>
                </div>
            </div>
            <div class="row mb-2">
                <div class="col-6">
                    <strong>${encode(message('Visits.Arrived', 'Arrived'))}:</strong><br/>
                    ${formatVisitTimestamp(v.arrivedAtUtc)}
                </div>
                <div class="col-6">
                    <strong>${encode(message('Visits.Ended', 'Ended'))}:</strong><br/>
                    ${v.endedAtUtc ? formatVisitTimestamp(v.endedAtUtc) : `<em>${encode(message('Visits.StillVisiting', 'Still visiting'))}</em>`}
                </div>
            </div>
            <div class="row mb-2">
                <div class="col-6">
                    <strong>${encode(message('Visits.Dwell', 'Dwell Time'))}:</strong> ${dwellTime}
                </div>
                <div class="col-6">
                    <strong>${encode(message('Visits.Status', 'Status'))}:</strong> ${statusBadge(v.endedAtUtc)}
                </div>
            </div>
            ${v.latitude && v.longitude ? `
            <div class="row mb-2">
                <div class="col-12">
                    <strong>${encode(message('Visits.VisitLocation', 'Location'))}:</strong> ${number(v.latitude, { minimumFractionDigits: 6, maximumFractionDigits: 6 })}, ${number(v.longitude, { minimumFractionDigits: 6, maximumFractionDigits: 6 })}
                    <a href="https://www.google.com/maps/search/?api=1&query=${v.latitude},${v.longitude}"
                       target="_blank" class="ms-2 btn btn-outline-primary btn-sm">
                        <i class="bi bi-globe-europe-africa"></i> ${encode(message('Locations.Maps', 'Maps'))}
                    </a>
                </div>
            </div>
            ` : ''}
            ${v.notesHtml ? `
            <div class="row mb-2">
                <div class="col-12">
                    <strong>${encode(message('Shell.Notes', 'Notes'))}:</strong>
                    <div class="border p-2 mt-1">${notesHtmlForDisplay(v.notesHtml)}</div>
                </div>
            </div>
            ` : ''}
            <div class="row mb-2">
                <div class="col-12">
                    <strong>${encode(message('Visits.RelevantLocations', 'Relevant Locations'))}:</strong> ${locationBadge}
                    <div id="modalLocationsContainer" class="mt-2">
                        ${v.locationCount === 0 ? `<span class="text-muted small">${encode(message('Visits.NoLocations', 'No relevant locations found for this visit.'))}</span>` : `<div class="text-center"><div class="spinner-border spinner-border-sm" role="status"></div> ${encode(message('Shell.Loading', 'Loading…'))}</div>`}
                    </div>
                </div>
            </div>
            <hr/>
            <div class="row">
                <div class="col-6">
                    <a href="${buildEditUrl(v.id)}" class="btn btn-primary btn-sm">${encode(message('Shell.Edit', 'Edit'))}</a>
                </div>
                <div class="col-6 text-end">
                    <a href="#" class="btn btn-outline-danger btn-sm delete-visit-from-popup" data-visit-id="${v.id}">${encode(message('Shell.Delete', 'Delete'))}</a>
                </div>
            </div>
        </div>
    `;
};

/** A definite rejection and a lost reply need different instructions at the Web boundary. */
const confirmVisitDeletion = (ids, deleted, single = false) => wayfarer.showConfirmationModal({
    title: message('Shell.LocationConfirmDeletion', 'Confirm deletion'),
    message: single ? message('Visits.DeletePrompt', 'Are you sure you want to delete this visit? This cannot be undone.')
        : plural('Visits.DeleteBulk', ids.length,
            'Delete {0} selected visit? This cannot be undone.', 'Delete {0} selected visits? This cannot be undone.'),
    confirmText: message('Shell.Delete', 'Delete'),
    onConfirm: async () => {
        const release = holdLanguageNavigation();
        try {
            const response = await fetch('/api/Visit/bulk-delete', {
                method: 'POST',
                headers: {
                    'RequestVerificationToken': document.querySelector('input[name="__RequestVerificationToken"]')?.value || '',
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify({ visitIds: ids })
            });
            if (response.redirected || [401, 403].includes(response.status)) {
                release();
                wayfarer.showAlert('warning', message('Shell.SessionEnded', 'Your session has ended. Sign in again.'));
                return;
            }
            const result = await response.json();
            if (response.ok && result.success) {
                release();
                wayfarer.showAlert('success', plural('Visits.DeleteDone', result.deletedCount, '{0} visit deleted.', '{0} visits deleted.'));
                deleted();
                return;
            }
            if ([400, 404].includes(response.status)) {
                release();
                const feedback = response.status === 404 ? message('Visits.DeleteNoMatches', 'No matching visits found.')
                    : message('Visits.DeleteNoIds', 'No visits were submitted for deletion.');
                wayfarer.showAlert('warning', feedback);
                return;
            }
        } catch (error) {
            console.error(error);
        }
        const status = document.getElementById('visit-deletion-status');
        status.textContent = message('Visits.DeleteUnknown',
            'Deletion could not be confirmed. Refresh Visit History and check which visits remain.');
        status.hidden = false;
    }
});

/**
 * Load locations for the visit modal
 */
const loadVisitLocationsForModal = async (visitId) => {
    const container = document.getElementById('modalLocationsContainer');
    if (!container) return;

    try {
        const response = await fetch(`/api/visit/${visitId}/locations?page=1&pageSize=5`);
        const failure = message('Visits.LocationsFailed', 'Failed to load relevant locations.');
        const data = await readVisitResponse(response, failure);
        if (!data.success) throw new Error('Visit locations response was incomplete');

        if (data.data.length > 0) {
            const locationsHtml = data.data.map(loc => `
                <div class="small border-bottom py-1">
                    <div class="d-flex justify-content-between align-items-center">
                        <span>
                            <i class="bi bi-geo-alt text-muted me-1"></i>
                            ${formatModalLocationTimestamp(loc.localTimestamp)}
                            <span class="font-monospace text-muted ms-2">${loc.latitude == null ? '–' : number(loc.latitude, { minimumFractionDigits: 4, maximumFractionDigits: 4 })}, ${loc.longitude == null ? '–' : number(loc.longitude, { minimumFractionDigits: 4, maximumFractionDigits: 4 })}</span>
                        </span>
                        <a href="/User/Location/Edit/${loc.id}?returnUrl=${returnUrlParam}" class="btn btn-sm btn-outline-secondary py-0 px-1" title="${encode(message('Locations.EditAction', 'Edit location'))}" aria-label="${encode(message('Locations.EditAction', 'Edit location'))}"><i class="bi bi-pencil"></i></a>
                    </div>
                    ${loc.address ? `<small class="text-muted d-block text-truncate">${encode(loc.address)}</small>` : ''}
                </div>
            `).join('');

            const moreHtml = data.totalItems > 5
                ? `<div class="text-center mt-2"><a href="${buildEditUrl(visitId)}" class="btn btn-sm btn-outline-secondary">${encode(plural('Visits.ViewLocations', data.totalItems, 'View all {0} location', 'View all {0} locations'))}</a></div>`
                : '';

            container.innerHTML = locationsHtml + moreHtml;
        } else {
            container.innerHTML = `<span class="text-muted small">${encode(message('Visits.NoLocations', 'No relevant locations found for this visit.'))}</span>`;
        }
    } catch (e) {
        console.error('Failed to load visit locations', e);
        container.innerHTML = `<span class="text-danger small">${encode(e instanceof VisitReadFailure ? e.message : message('Visits.LocationsFailed', 'Failed to load relevant locations.'))}</span>`;
    }
};

/**
 * Format timestamp for modal location display
 */
const formatModalLocationTimestamp = (ts) => {
    if (!ts) return '-';
    return date(new Date(ts), {
        month: 'short', day: 'numeric',
        hour: '2-digit', minute: '2-digit'
    });
};
