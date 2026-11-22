import { renderHistoryPagination } from '../../../util/history-pagination.js';
import { confirmLocationHistoryDeletion, bindLocationBulkDeletion } from '../../../util/location-history-actions.js';
import { locationText, locationHtml, locationMapsLink, locationTimes, sourceTimeZone, displayCoordinate,
    measurement, renderLocationStats, readLocationHistory } from '../../../util/location-history-text.js';
import { message } from '../../../localization/messages.js';
import { encodeFeatureText as encode } from '../../../util/feature-metadata.js';
import { notesHtmlForDisplay } from '../../../util/notes-images.js';
// AllLocations.js
import {
    currentDateInputValue,
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
import { renderLocationAddress, renderAddressComponent } from '../../../util/location-address.js';

let locations = [];
let currentPage = 1;
const pageSize = 20;
let currentFilters = {};

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
    const info = locationTimes(location);
    const sourceLabel = info.source
        ? `<div class="small text-muted">${locationHtml('Recorded', 'Recorded: {0}', info.source)}</div>`
        : `<div class="small text-muted fst-italic">${locationHtml('RecordedZoneUnavailable', 'Recorded timezone unavailable')}</div>`;
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
    document.addEventListener('click', async e => {
        const del = e.target.closest('.delete-location-from-popup');
        if (del) {
            e.preventDefault();
            const id = +del.dataset.locationId;
            // Get the current open modal instance
            const modalEl = document.getElementById('locationModal');
            const modalInstance = bootstrap.Modal.getInstance(modalEl);
            confirmLocationHistoryDeletion(id, () => {
                loadLocations(currentPage);
                modalInstance?.hide();
            });
        }
    });

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
    setupActivityEditorEvents('#modalContent', syncLocationActivity, { protectLanguage: true });
    setupActivityEditorEvents('#locationsTable', syncLocationActivity, { protectLanguage: true });

    bindLocationBulkDeletion(() => loadLocations(currentPage));

    renderLocationStats();
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
        .then(readLocationHistory)
        .then(res => {
            document.getElementById('location-read-status').textContent = '';
            locations = res.data || res.Data || [];
            displayLocationsInTable(locations);
            updatePagination(res.TotalItems || res.totalItems, currentPage);

            // clear the “select all” checkbox
            const selectAll = document.getElementById('selectAll');
            selectAll.checked = false;
            selectAll.indeterminate = false;
        })
        .catch(error => {
            console.error(error);
            document.getElementById('location-read-status').textContent = locationText('LoadFailed', 'Could not load locations.');
        });
};


const displayLocationsInTable = (locations) => {
    const tbody = document.querySelector('#locationsTable tbody');
    tbody.innerHTML = '';
    if (!locations.length) {
        tbody.innerHTML = `<tr><td colspan="11" class="text-center">${locationHtml('NoMatches', 'No locations match the criteria.')}</td></tr>`;
        return;
    }
    for (const loc of locations) {
        const timestampHtml = renderTimestampBlock(loc);
        // Embed the inline activity editor for table rows.
        const activityEditorHtml = generateActivityEditorHtml(loc, { showLabel: false, compact: true });
        tbody.insertAdjacentHTML('beforeend', `
      <tr>
        <td><input type="checkbox" name="locationCheckbox" aria-label="${encode(message('Shell.SelectRow', 'Select row'))}" value="${loc.id}"></td>
        <td>
          <i class="bi bi-clock me-1"></i>
          ${timestampHtml}
        </td>
        <td>${displayCoordinate(loc.coordinates.latitude)}</td>
        <td>${displayCoordinate(loc.coordinates.longitude)}</td>
        <td class="text-center">${measurement(loc.accuracy, 'Accuracy', 'Accuracy')}</td>
        <td class="text-center">${measurement(loc.speed, 'Speed', 'Speed')}</td>
        <td class="text-center">${measurement(loc.altitude, 'Altitude', 'Altitude')}</td>
        <td>${activityEditorHtml}</td>
        <td>${renderLocationAddress(loc)}</td>
        <td>${renderAddressComponent(loc.place, locationText("Place", "Place"))}</td>
        <td>${renderAddressComponent(loc.country, locationText("Country", "Country"))}</td>
        <td>
          <a href="#" class="btn btn-primary btn-sm view-location" data-id="${loc.id}">${encode(message('Shell.View', 'View'))}</a>
          <a href="${buildEditUrl(loc.id)}" class="btn btn-secondary btn-sm">${encode(message('Shell.Edit', 'Edit'))}</a>
        </td>
      </tr>
    `);
    }
}

/** Keep the selected filter values while navigating between table pages. */
const updatePagination = (totalItems, page) =>
    renderHistoryPagination(document.getElementById('pagination'), Math.ceil(totalItems / pageSize), page, loadLocations);

const viewLocationDetails = (loc) => {
    document.getElementById('modalContent').innerHTML = generateLocationModalContent(loc);
    new bootstrap.Modal(document.getElementById('locationModal')).show();
}

// --- Modal content & helpers ---
const generateLocationModalContent = (location) => {
    const charCount = location.notes ? location.notes.length : 0;
    const style = charCount === 0 ? 'display:none;' : `min-height:${16 * Math.ceil(charCount / 50) * 1.5}px;`;
    const timestamps = locationTimes(location);
    const sourceZone = sourceTimeZone(location);
    const recordedBlock = timestamps.source
        ? `<div>${encode(timestamps.source)}</div>`
        : `<div class="fst-italic text-muted">${locationHtml('SourceZoneUnavailable', 'Source timezone unavailable')}</div>`;
    return `
    <div class="container-fluid">
      <div class="row mb-2">
        <div class="col-6">
            <strong>${locationHtml('ViewerTime', 'Datetime (your timezone)')}:</strong>
            <div>${timestamps.viewer}</div>
        </div>
        <div class="col-6">
            <strong>${locationHtml('RecordedTime', 'Recorded local time')}:</strong>
              ${recordedBlock}
              ${sourceZone && !timestamps.source ? `<div class="small text-muted">${encode(sourceZone)}</div>` : ''}
        </div>
      </div>
      <div class="row mb-2">
        <div class="col-6">
            <strong>${locationHtml('Latitude', 'Latitude')}:</strong> ${displayCoordinate(location.coordinates.latitude)}
        </div>
        <div class="col-6">
            <strong>${locationHtml('Longitude', 'Longitude')}:</strong> ${displayCoordinate(location.coordinates.longitude)}
        </div>
      </div>
      <div class="row mb-2">
        <div class="col-6">${generateActivityEditorHtml(location)}</div>
        <div class="col-6">
            <strong>${locationHtml('Altitude', 'Altitude')}:</strong> ${measurement(location.altitude, 'Altitude', 'Altitude', ' m')}
        </div>
      </div>
      <div class="row mb-2">
        <div class="col-12">
            ${renderLocationAddress(location)}
                ${locationMapsLink(location)}
            ${generateWikipediaLinkHtml(location, { query: location.place || location.fullAddress })}
        </div>
      </div>
      <div class="row mb-2">
        <div class="col-12" style="${style}">
            <strong>${encode(message('Shell.Notes', 'Notes'))}:</strong><div class="border p-1">${notesHtmlForDisplay(location.notes)}
        </div>
      </div>
      <div class="row">
        <div class="col-5">
            <a href="${buildEditUrl(location.id)}" class="btn-link">${encode(message('Shell.Edit', 'Edit'))}</a>
        </div>
        <div class="col-5 offset-2">
            <a href="#" class="btn-link text-danger delete-location-from-popup" data-location-id="${location.id}">${encode(message('Shell.Delete', 'Delete'))}</a>
        </div>
      </div>
    </div>
  `;
}
