/* trips.js Trip index view functionality */
import { addZoomLevelControl } from '../../../map-utils.js';
import {
    generateWikipediaLinkHtml,
    initWikipediaPopovers,
} from '../../../util/wikipedia-utils.js';

(() => {
    /* ------------------------------------------------ Delete trip */
    const handleDeleteClick = (btn, e) => {
        if (e) e.preventDefault();
        const tripId = btn.dataset.tripId;

        wayfarer.showConfirmationModal({
            title: "Delete Trip",
            message: "Are you sure you want to delete this trip?",
            confirmText: "Delete",
            onConfirm: async () => {
                const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value;
                const fd = new FormData();
                fd.set('__RequestVerificationToken', token);
                fd.set('id', tripId);

                const resp = await fetch('/User/Trip/Delete', {
                    method: 'POST',
                    body: fd
                });

                if (resp.ok) {
                    document.querySelector(`[data-trip-id="${tripId}"]`)?.closest('tr')?.remove();
                    wayfarer.showAlert("success", "Trip deleted.");
                } else {
                    wayfarer.showAlert("danger", "Failed to delete trip.");
                }
            }
        });
    };

    /* ------------------------------------------------ Import trip */
    const importInput      = document.getElementById('importFile');       // Wayfarer
    const importInputMyMap = document.getElementById('importFileMyMaps'); // My Maps
    const dupModalEl = document.getElementById('dupModal');
    const dupModal = dupModalEl ? new bootstrap.Modal(dupModalEl) : null;
    let pendingFile;           // holds the File until user picks Upsert / Copy

    /* ------------------------------------------------ file-picker hooks ----- */
    [importInput, importInputMyMap].forEach(inp => {
        if (!inp) return;
        inp.addEventListener('change', e => {
            const file = e.target.files?.[0];
            if (file) upload(file);            // default mode = Auto
            inp.value = '';
        });
    });
    const upload = async (file, mode = 'Auto') => {
        const fd = new FormData();
        fd.append('file', file);
        fd.append('mode', mode);

        const resp = await fetch('/User/Trip/Import', {method: 'POST', body: fd});

        /* 1 ── success: server replied 302 Location → browser-side redirect */
        if (resp.redirected) {
            dupModal?.hide();
            window.location.href = resp.url;
            return;
        }

        /* 2 ── try to read JSON only when response is JSON ---------------- */
        let payload = null;
        const isJson = resp.headers
            .get('Content-Type')
            ?.startsWith('application/json');

        if (isJson) {
            try {
                payload = await resp.json();
            } catch (e) { /* silence parse errors */
            }
        }

        if (payload?.status === 'duplicate') {
            pendingFile = file;
            dupModal?.show();
        } else {
            const msg = payload?.message || await resp.text();      // fallback
            wayfarer.showAlert('danger', msg || 'Import failed.');
        }
    };

    document.getElementById('btnUpdate')?.addEventListener('click', () => {
        dupModal?.hide();
        if (pendingFile) upload(pendingFile, 'Upsert');
    });

    document.getElementById('btnCopy')?.addEventListener('click', () => {
        dupModal?.hide();
        if (pendingFile) upload(pendingFile, 'CreateNew');
    });

    /* ------------------------------------------------ Backfill functionality */
    const backfillModalEl = document.getElementById('backfillModal');
    const backfillModal = backfillModalEl ? new bootstrap.Modal(backfillModalEl) : null;
    let currentTripId = null;
    let previewData = null;

    /** @type {{ newVisits: Array, suggestedVisits: Array, staleVisits: Array, existingVisits: Array }|null} */
    let rawBackfillData = null;

    /**
     * Persistent checkbox selection state that survives DOM rebuilds and search filtering.
     * Maps checkbox id → boolean (true=checked, false=unchecked).
     * Only stores explicit user interactions; items not in this map use their HTML defaults.
     * @type {Map<string, boolean>}
     */
    const persistentCheckState = new Map();

    // DOM elements for backfill modal
    const configSection = document.getElementById('backfill-config');
    const loadingSection = document.getElementById('backfill-loading');
    const resultsSection = document.getElementById('backfill-results');
    const tripNameEl = document.getElementById('backfill-trip-name');
    const fromDateInput = document.getElementById('backfill-from-date');
    const toDateInput = document.getElementById('backfill-to-date');
    const analyzeBtn = document.getElementById('btn-backfill-analyze');
    const applyBtn = document.getElementById('btn-backfill-apply');

    /**
     * Resets the backfill modal to its initial configuration state.
     */
    const resetBackfillModal = () => {
        configSection?.classList.remove('d-none');
        loadingSection?.classList.add('d-none');
        resultsSection?.classList.add('d-none');
        analyzeBtn?.classList.remove('d-none');
        applyBtn?.classList.add('d-none');
        document.getElementById('action-summary')?.classList.add('d-none');
        document.getElementById('new-visits-hint')?.classList.add('d-none');
        document.getElementById('suggested-visits-hint')?.classList.add('d-none');
        document.getElementById('stale-visits-hint')?.classList.add('d-none');
        document.getElementById('existing-visits-hint')?.classList.add('d-none');
        document.getElementById('suggested-select-all-wrapper')?.classList.add('d-none');
        document.getElementById('stale-select-all-wrapper')?.classList.add('d-none');
        document.getElementById('existing-select-all-wrapper')?.classList.add('d-none');
        if (fromDateInput) fromDateInput.value = '';
        if (toDateInput) toDateInput.value = '';
        previewData = null;
        rawBackfillData = null;
        persistentCheckState.clear();

        // Reset search input and hide wrapper
        const searchInput = document.getElementById('backfill-search');
        const searchWrapper = document.getElementById('backfill-search-wrapper');
        if (searchInput) searchInput.value = '';
        searchWrapper?.classList.add('d-none');

        // Reset to first tab
        const confirmedTab = document.getElementById('confirmed-tab');
        if (confirmedTab) {
            const tab = new bootstrap.Tab(confirmedTab);
            tab.show();
        }
    };

    /**
     * Shows the loading state in the backfill modal.
     * @param {string} message - The main loading message.
     * @param {string|null} subMessage - Optional sub-message with additional info.
     */
    const showBackfillLoading = (message = 'Analyzing location history...', subMessage = null) => {
        configSection?.classList.add('d-none');
        loadingSection?.classList.remove('d-none');
        resultsSection?.classList.add('d-none');
        analyzeBtn?.classList.add('d-none');
        applyBtn?.classList.add('d-none');

        // Update loading message
        const loadingText = loadingSection?.querySelector('p');
        if (loadingText) {
            loadingText.innerHTML = message + (subMessage ? `<br><small class="text-muted">${subMessage}</small>` : '');
        }
    };

    /**
     * Renders the backfill preview results with grouped display and search support.
     * Stores raw data for search filtering and delegates rendering to rerenderBackfillTabs.
     * @param {Object} data - The preview data from the API.
     */
    const renderBackfillResults = (data) => {
        previewData = data;
        configSection?.classList.add('d-none');
        loadingSection?.classList.add('d-none');
        resultsSection?.classList.remove('d-none');
        analyzeBtn?.classList.add('d-none');

        // Summary stats
        document.getElementById('result-locations').textContent = data.locationsScanned.toLocaleString();
        document.getElementById('result-places').textContent = data.placesAnalyzed.toLocaleString();
        document.getElementById('result-time').textContent = `${data.analysisDurationMs}ms`;

        // Cache raw data for search filtering
        rawBackfillData = {
            newVisits: data.newVisits || [],
            suggestedVisits: data.suggestedVisits || [],
            staleVisits: data.staleVisits || [],
            existingVisits: data.existingVisits || []
        };

        // Show search input
        const searchWrapper = document.getElementById('backfill-search-wrapper');
        const searchInput = document.getElementById('backfill-search');
        searchWrapper?.classList.remove('d-none');
        if (searchInput) searchInput.value = '';

        // Render all tabs (unfiltered)
        rerenderBackfillTabs();

        // Show apply button and action summary (always show for any available actions)
        const hasChanges = rawBackfillData.newVisits.length > 0 ||
            rawBackfillData.suggestedVisits.length > 0 ||
            rawBackfillData.staleVisits.length > 0 ||
            rawBackfillData.existingVisits.length > 0;
        if (hasChanges) {
            applyBtn?.classList.remove('d-none');
        }
    };

    /**
     * Adds click handlers to place context items and buttons.
     */
    const addPlaceContextClickHandlers = () => {
        // Click handler for confirmed visit list items (full row click)
        document.querySelectorAll('.place-context-item').forEach(item => {
            item.addEventListener('click', (e) => {
                // Don't trigger if clicking on a checkbox or button
                if (e.target.closest('input, button')) return;

                const placeData = {
                    placeId: item.dataset.placeId,
                    placeName: item.dataset.placeName,
                    regionName: item.dataset.regionName,
                    latitude: parseFloat(item.dataset.latitude) || null,
                    longitude: parseFloat(item.dataset.longitude) || null,
                    iconName: item.dataset.iconName || null,
                    markerColor: item.dataset.markerColor || null,
                    firstSeenUtc: item.dataset.firstSeen,
                    lastSeenUtc: item.dataset.lastSeen
                };

                showPlaceContext(placeData, item.dataset.type || 'candidate');
            });
        });

        // Click handler for suggestion map buttons
        document.querySelectorAll('.place-context-btn').forEach(btn => {
            btn.addEventListener('click', (e) => {
                e.preventDefault();
                e.stopPropagation(); // Don't trigger parent click handlers

                const placeData = {
                    placeId: btn.dataset.placeId,
                    placeName: btn.dataset.placeName,
                    regionName: btn.dataset.regionName,
                    latitude: parseFloat(btn.dataset.latitude) || null,
                    longitude: parseFloat(btn.dataset.longitude) || null,
                    iconName: btn.dataset.iconName || null,
                    markerColor: btn.dataset.markerColor || null,
                    firstSeenUtc: btn.dataset.firstSeen,
                    lastSeenUtc: btn.dataset.lastSeen
                };

                showPlaceContext(placeData, btn.dataset.type || 'suggestion');
            });
        });
    };

    /**
     * Updates the action summary to show what will happen when Apply is clicked.
     */
    const updateActionSummary = () => {
        const actionSummary = document.getElementById('action-summary');
        const summaryCreate = document.getElementById('summary-create');
        const summaryConfirmSuggestions = document.getElementById('summary-confirm-suggestions');
        const summaryDeleteStale = document.getElementById('summary-delete-stale');
        const summaryDeleteManual = document.getElementById('summary-delete-manual');
        const summaryNothing = document.getElementById('summary-nothing');
        const summaryCreateCount = document.getElementById('summary-create-count');
        const summaryConfirmCount = document.getElementById('summary-confirm-count');
        const summaryDeleteStaleCount = document.getElementById('summary-delete-stale-count');
        const summaryDeleteManualCount = document.getElementById('summary-delete-manual-count');

        if (!actionSummary || !previewData) return;

        // Count checked items from persistentCheckState to include items hidden by search filter.
        // For items never interacted with, use their HTML default (stale=checked, others=unchecked).
        const countChecked = (items, idFn, defaultChecked) =>
            items.reduce((n, v) => {
                const id = idFn(v);
                const checked = persistentCheckState.has(id) ? persistentCheckState.get(id) : defaultChecked;
                return n + (checked ? 1 : 0);
            }, 0);

        const newVisitsCount = previewData.newVisits?.length || 0;
        const confirmedSuggestionsCount = rawBackfillData
            ? countChecked(rawBackfillData.suggestedVisits, v => `suggested-${v.placeId}-${v.visitDate}`, false)
            : document.querySelectorAll('.suggested-visit-check:checked').length;
        const staleDeleteCount = rawBackfillData
            ? countChecked(rawBackfillData.staleVisits, v => `stale-${v.visitId}`, true)
            : document.querySelectorAll('.stale-visit-check:checked').length;
        const manualDeleteCount = rawBackfillData
            ? countChecked(rawBackfillData.existingVisits, v => `existing-${v.visitId}`, false)
            : document.querySelectorAll('.existing-visit-check:checked').length;

        // Update counts
        if (summaryCreateCount) summaryCreateCount.textContent = newVisitsCount;
        if (summaryConfirmCount) summaryConfirmCount.textContent = confirmedSuggestionsCount;
        if (summaryDeleteStaleCount) summaryDeleteStaleCount.textContent = staleDeleteCount;
        if (summaryDeleteManualCount) summaryDeleteManualCount.textContent = manualDeleteCount;

        // Show/hide appropriate lines
        if (newVisitsCount > 0) {
            summaryCreate?.classList.remove('d-none');
        } else {
            summaryCreate?.classList.add('d-none');
        }

        if (confirmedSuggestionsCount > 0) {
            summaryConfirmSuggestions?.classList.remove('d-none');
        } else {
            summaryConfirmSuggestions?.classList.add('d-none');
        }

        if (staleDeleteCount > 0) {
            summaryDeleteStale?.classList.remove('d-none');
        } else {
            summaryDeleteStale?.classList.add('d-none');
        }

        if (manualDeleteCount > 0) {
            summaryDeleteManual?.classList.remove('d-none');
        } else {
            summaryDeleteManual?.classList.add('d-none');
        }

        if (newVisitsCount === 0 && confirmedSuggestionsCount === 0 && staleDeleteCount === 0 && manualDeleteCount === 0) {
            summaryNothing?.classList.remove('d-none');
        } else {
            summaryNothing?.classList.add('d-none');
        }

        // Show/hide the summary section
        actionSummary.classList.remove('d-none');
    };

    /**
     * Returns the appropriate Bootstrap badge class based on confidence level.
     * @param {number} confidence - The confidence percentage (0-100).
     * @returns {string} Bootstrap badge class.
     */
    const getConfidenceBadgeClass = (confidence) => {
        if (confidence >= 80) return 'bg-success';
        if (confidence >= 60) return 'bg-info';
        if (confidence >= 40) return 'bg-warning text-dark';
        return 'bg-secondary';
    };

    /**
     * Escapes HTML special characters to prevent XSS.
     * @param {string} text - The text to escape.
     * @returns {string} Escaped text.
     */
    const escapeHtml = (text) => {
        if (!text) return '';
        const div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    };

    /**
     * Renders a list of items grouped by regionName with sticky region headers.
     * Items are expected to be pre-sorted by region from the backend.
     * @param {Array} items - Array of objects with at least a regionName property.
     * @param {Function} renderItemFn - Function that receives an item and returns an HTML string.
     * @returns {string} Combined HTML string with region headers and items.
     */
    const renderGroupedList = (items, renderItemFn) => {
        if (!items || items.length === 0) return '';
        let html = '';
        let currentRegion = null;
        for (const item of items) {
            const region = item.regionName || 'Unknown';
            if (region !== currentRegion) {
                currentRegion = region;
                html += `<div class="list-group-item bg-light fw-semibold small text-muted py-1 pe-none user-select-none">${escapeHtml(region)}</div>`;
            }
            html += renderItemFn(item);
        }
        return html;
    };

    /**
     * Filters an array of items by matching query against placeName and regionName.
     * Uses case-insensitive substring matching.
     * @param {Array} items - Array of objects with placeName and regionName properties.
     * @param {string} query - The search query string.
     * @returns {Array} Filtered items.
     */
    const filterBySearch = (items, query) => {
        if (!query) return items;
        const q = query.toLowerCase();
        return items.filter(v =>
            (v.placeName || '').toLowerCase().includes(q) ||
            (v.regionName || '').toLowerCase().includes(q)
        );
    };

    /**
     * Re-renders all backfill tabs with optionally filtered data.
     * Called on search input changes and on initial render.
     * @param {string} [searchQuery=''] - Optional search query to filter results.
     */
    const rerenderBackfillTabs = (searchQuery = '') => {
        if (!rawBackfillData) return;

        // Snapshot current DOM checkbox states into persistent store before rebuild.
        // This captures user interactions even for items about to be filtered out.
        document.querySelectorAll('.suggested-visit-check, .stale-visit-check, .existing-visit-check')
            .forEach(cb => persistentCheckState.set(cb.id, cb.checked));

        const filteredNew = filterBySearch(rawBackfillData.newVisits, searchQuery);
        const filteredSuggested = filterBySearch(rawBackfillData.suggestedVisits, searchQuery);
        const filteredStale = filterBySearch(rawBackfillData.staleVisits, searchQuery);
        const filteredExisting = filterBySearch(rawBackfillData.existingVisits, searchQuery);

        const isFiltered = searchQuery.length > 0;
        const totalNew = rawBackfillData.newVisits.length;
        const totalSuggested = rawBackfillData.suggestedVisits.length;
        const totalStale = rawBackfillData.staleVisits.length;
        const totalExisting = rawBackfillData.existingVisits.length;

        // Helper: format badge count, showing "filtered of total" when searching
        const badgeText = (filtered, total) => isFiltered ? `${filtered} of ${total}` : `${total}`;

        // --- Confirmed (New Visits) ---
        const newVisitsList = document.getElementById('new-visits-list');
        const noNewVisits = document.getElementById('no-new-visits');
        const newVisitsHint = document.getElementById('new-visits-hint');
        document.getElementById('new-visits-count').textContent = badgeText(filteredNew.length, totalNew);

        if (filteredNew.length === 0) {
            newVisitsList?.classList.add('d-none');
            noNewVisits?.classList.remove('d-none');
            newVisitsHint?.classList.add('d-none');
        } else {
            newVisitsList?.classList.remove('d-none');
            noNewVisits?.classList.add('d-none');
            newVisitsHint?.classList.remove('d-none');
            newVisitsList.innerHTML = renderGroupedList(filteredNew, v => `
                <div class="list-group-item list-group-item-action d-flex justify-content-between align-items-center py-2 place-context-item"
                     style="cursor: pointer;"
                     data-place-id="${v.placeId}"
                     data-place-name="${escapeHtml(v.placeName)}"
                     data-region-name="${escapeHtml(v.regionName)}"
                     data-latitude="${v.latitude || ''}"
                     data-longitude="${v.longitude || ''}"
                     data-icon-name="${v.iconName || ''}"
                     data-marker-color="${v.markerColor || ''}"
                     data-first-seen="${v.firstSeenUtc}"
                     data-last-seen="${v.lastSeenUtc}"
                     data-type="candidate">
                    <div>
                        <div class="fw-medium text-success"><i class="bi bi-plus-circle me-1"></i>${escapeHtml(v.placeName)}</div>
                        <small class="text-muted">${escapeHtml(v.regionName)} &middot; ${v.visitDate}</small>
                    </div>
                    <div class="text-end d-flex align-items-center gap-2">
                        <div>
                            <span class="badge ${getConfidenceBadgeClass(v.confidence)}"
                                  title="Confidence based on location count and proximity (avg ${Math.round(v.avgDistanceMeters)}m from place)">${v.confidence}% Confidence</span>
                            <small class="text-muted d-block">${v.locationCount} hits</small>
                        </div>
                        <i class="bi bi-geo-alt text-muted" title="View on map"></i>
                    </div>
                </div>
            `);
        }

        // --- Consider Also (Suggestions) ---
        const suggestedVisitsList = document.getElementById('suggested-visits-list');
        const noSuggestedVisits = document.getElementById('no-suggested-visits');
        const suggestedVisitsHint = document.getElementById('suggested-visits-hint');
        const suggestedSelectAllWrapper = document.getElementById('suggested-select-all-wrapper');
        document.getElementById('suggested-visits-count').textContent = badgeText(filteredSuggested.length, totalSuggested);

        if (filteredSuggested.length === 0) {
            suggestedVisitsList?.classList.add('d-none');
            noSuggestedVisits?.classList.remove('d-none');
            suggestedVisitsHint?.classList.add('d-none');
            suggestedSelectAllWrapper?.classList.add('d-none');
        } else {
            suggestedVisitsList?.classList.remove('d-none');
            noSuggestedVisits?.classList.add('d-none');
            suggestedVisitsHint?.classList.remove('d-none');
            suggestedSelectAllWrapper?.classList.remove('d-none');
            suggestedVisitsList.innerHTML = renderGroupedList(filteredSuggested, v => `
                <div class="list-group-item d-flex justify-content-between align-items-center py-2">
                    <div class="form-check flex-grow-1">
                        <input class="form-check-input suggested-visit-check" type="checkbox"
                               value="${v.placeId}"
                               data-visit-date="${v.visitDate}"
                               data-first-seen="${v.firstSeenUtc}"
                               data-last-seen="${v.lastSeenUtc}"
                               id="suggested-${v.placeId}-${v.visitDate}">
                        <label class="form-check-label" for="suggested-${v.placeId}-${v.visitDate}">
                            <span class="fw-medium text-info"><i class="bi bi-lightbulb me-1"></i>${escapeHtml(v.placeName)}</span>
                            <small class="text-muted d-block">${escapeHtml(v.regionName)} &middot; ${v.visitDate}</small>
                        </label>
                    </div>
                    <div class="text-end d-flex align-items-center gap-2">
                        <div>
                            <span class="badge bg-info" title="${escapeHtml(v.suggestionReason)}">
                                ${v.hasUserCheckin ? '<i class="bi bi-check-circle me-1"></i>' : ''}
                                ${escapeHtml(v.suggestionReason)}
                            </span>
                            <small class="text-muted d-block">${Math.round(v.minDistanceMeters)}m min dist</small>
                        </div>
                        <button type="button" class="btn btn-sm btn-outline-secondary place-context-btn"
                                data-place-id="${v.placeId}"
                                data-place-name="${escapeHtml(v.placeName)}"
                                data-region-name="${escapeHtml(v.regionName)}"
                                data-latitude="${v.latitude || ''}"
                                data-longitude="${v.longitude || ''}"
                                data-icon-name="${v.iconName || ''}"
                                data-marker-color="${v.markerColor || ''}"
                                data-first-seen="${v.firstSeenUtc}"
                                data-last-seen="${v.lastSeenUtc}"
                                data-type="suggestion"
                                title="View on map">
                            <i class="bi bi-geo-alt"></i>
                        </button>
                    </div>
                </div>
            `);
            suggestedVisitsList.querySelectorAll('.suggested-visit-check').forEach(cb => {
                cb.addEventListener('change', () => {
                    persistentCheckState.set(cb.id, cb.checked);
                    updateActionSummary();
                });
            });
        }

        // --- Stale Visits ---
        const staleVisitsList = document.getElementById('stale-visits-list');
        const noStaleVisits = document.getElementById('no-stale-visits');
        const staleVisitsHint = document.getElementById('stale-visits-hint');
        const staleSelectAllWrapper = document.getElementById('stale-select-all-wrapper');
        document.getElementById('stale-visits-count').textContent = badgeText(filteredStale.length, totalStale);

        if (filteredStale.length === 0) {
            staleVisitsList?.classList.add('d-none');
            noStaleVisits?.classList.remove('d-none');
            staleVisitsHint?.classList.add('d-none');
            staleSelectAllWrapper?.classList.add('d-none');
        } else {
            staleVisitsList?.classList.remove('d-none');
            noStaleVisits?.classList.add('d-none');
            staleVisitsHint?.classList.remove('d-none');
            staleSelectAllWrapper?.classList.remove('d-none');
            staleVisitsList.innerHTML = renderGroupedList(filteredStale, v => `
                <div class="list-group-item d-flex justify-content-between align-items-center py-2">
                    <div class="form-check flex-grow-1">
                        <input class="form-check-input stale-visit-check" type="checkbox"
                               value="${v.visitId}" id="stale-${v.visitId}" checked>
                        <label class="form-check-label" for="stale-${v.visitId}">
                            <span class="fw-medium text-danger"><i class="bi bi-trash me-1"></i>${escapeHtml(v.placeName)}</span>
                            <small class="text-muted d-block">${escapeHtml(v.regionName)} &middot; ${v.visitDate}</small>
                        </label>
                    </div>
                    <span class="badge bg-warning text-dark">${escapeHtml(v.reason)}</span>
                </div>
            `);
            staleVisitsList.querySelectorAll('.stale-visit-check').forEach(cb => {
                cb.addEventListener('change', () => {
                    persistentCheckState.set(cb.id, cb.checked);
                    updateActionSummary();
                });
            });
        }

        // --- Existing Visits ---
        const existingVisitsList = document.getElementById('existing-visits-list');
        const noExistingVisits = document.getElementById('no-existing-visits');
        const existingVisitsHint = document.getElementById('existing-visits-hint');
        const existingSelectAllWrapper = document.getElementById('existing-select-all-wrapper');
        document.getElementById('existing-visits-count').textContent = badgeText(filteredExisting.length, totalExisting);

        if (filteredExisting.length === 0) {
            existingVisitsList?.classList.add('d-none');
            noExistingVisits?.classList.remove('d-none');
            existingVisitsHint?.classList.add('d-none');
            existingSelectAllWrapper?.classList.add('d-none');
        } else {
            existingVisitsList?.classList.remove('d-none');
            noExistingVisits?.classList.add('d-none');
            existingVisitsHint?.classList.remove('d-none');
            existingSelectAllWrapper?.classList.remove('d-none');
            existingVisitsList.innerHTML = renderGroupedList(filteredExisting, v => `
                <div class="list-group-item d-flex justify-content-between align-items-center py-2">
                    <div class="form-check flex-grow-1">
                        <input class="form-check-input existing-visit-check" type="checkbox"
                               value="${v.visitId}" id="existing-${v.visitId}">
                        <label class="form-check-label" for="existing-${v.visitId}">
                            <span class="fw-medium">${escapeHtml(v.placeName)}</span>
                            <small class="text-muted d-block">${escapeHtml(v.regionName)} &middot; ${v.visitDate}</small>
                        </label>
                    </div>
                    <div class="text-end">
                        ${v.isOpen
                            ? '<span class="badge bg-success">Open</span>'
                            : '<span class="badge bg-secondary">Closed</span>'}
                    </div>
                </div>
            `);
            existingVisitsList.querySelectorAll('.existing-visit-check').forEach(cb => {
                cb.addEventListener('change', () => {
                    persistentCheckState.set(cb.id, cb.checked);
                    updateActionSummary();
                });
            });
        }

        // Restore checkbox states from persistent store after DOM rebuild.
        // Items filtered out by search retain their state in the Map for when they reappear.
        persistentCheckState.forEach((wasChecked, id) => {
            const cb = document.getElementById(id);
            if (cb) cb.checked = wasChecked;
        });

        // Re-add click handlers for place context map
        addPlaceContextClickHandlers();
        updateActionSummary();
    };

    /**
     * Handles the analyze button click - fetches preview data from API.
     */
    const handleAnalyzeClick = async () => {
        if (!currentTripId) return;

        // Validate date range if both dates are provided
        if (fromDateInput?.value && toDateInput?.value) {
            const fromDate = new Date(fromDateInput.value);
            const toDate = new Date(toDateInput.value);
            if (fromDate > toDate) {
                wayfarer.showAlert('warning', 'From date must be before or equal to To date.');
                return;
            }
        }

        const params = new URLSearchParams();
        if (fromDateInput?.value) params.set('fromDate', fromDateInput.value);
        if (toDateInput?.value) params.set('toDate', toDateInput.value);
        const queryString = params.toString() ? '?' + params : '';

        // Phase 1: Get info for progress feedback
        try {
            const infoUrl = `/api/backfill/info/${currentTripId}${queryString}`;
            const infoResp = await fetch(infoUrl);
            const infoResult = await infoResp.json();

            if (infoResult.success) {
                const info = infoResult.data;
                // Show progress with real numbers
                showBackfillLoading(
                    `Analyzing ${info.placesWithCoordinates} places against ${info.estimatedLocations.toLocaleString()} locations...`,
                    `Estimated time: ~${info.estimatedSeconds} second${info.estimatedSeconds !== 1 ? 's' : ''}`
                );
            } else {
                showBackfillLoading();
            }
        } catch {
            // Fall back to generic message if info endpoint fails
            showBackfillLoading();
        }

        // Phase 2: Run actual preview
        try {
            const previewUrl = `/api/backfill/preview/${currentTripId}${queryString}`;
            const resp = await fetch(previewUrl);
            const result = await resp.json();

            if (result.success) {
                renderBackfillResults(result.data);
            } else {
                wayfarer.showAlert('danger', result.message || 'Analysis failed.');
                backfillModal?.hide();
            }
        } catch (err) {
            console.error('Backfill preview error:', err);
            wayfarer.showAlert('danger', 'An error occurred during analysis.');
            backfillModal?.hide();
        }
    };

    /**
     * Handles the apply button click - applies the backfill changes.
     */
    const handleApplyClick = async () => {
        if (!currentTripId || !previewData) return;

        // Clear any active search filter so all items are in the DOM when building the payload.
        // This prevents checked-but-filtered items from being silently dropped.
        const searchInput = document.getElementById('backfill-search');
        if (searchInput && searchInput.value) {
            searchInput.value = '';
            rerenderBackfillTabs();
        }

        // Build the request payload for strict matches
        const createVisits = previewData.newVisits.map(v => ({
            placeId: v.placeId,
            visitDate: v.visitDate,
            firstSeenUtc: v.firstSeenUtc,
            lastSeenUtc: v.lastSeenUtc
        }));

        // Build the request payload for user-confirmed suggestions
        const confirmedSuggestions = Array.from(
            document.querySelectorAll('.suggested-visit-check:checked')
        ).map(cb => ({
            placeId: cb.value,
            visitDate: cb.dataset.visitDate,
            firstSeenUtc: cb.dataset.firstSeen,
            lastSeenUtc: cb.dataset.lastSeen
        }));

        // Get selected stale visits to delete
        const staleVisitIds = Array.from(
            document.querySelectorAll('.stale-visit-check:checked')
        ).map(cb => cb.value);

        // Get manually selected existing visits to delete
        const manualDeleteIds = Array.from(
            document.querySelectorAll('.existing-visit-check:checked')
        ).map(cb => cb.value);

        // Combine all visit IDs to delete
        const deleteVisitIds = [...staleVisitIds, ...manualDeleteIds];

        if (createVisits.length === 0 && confirmedSuggestions.length === 0 && deleteVisitIds.length === 0) {
            wayfarer.showAlert('info', 'No changes to apply.');
            backfillModal?.hide();
            return;
        }

        applyBtn.disabled = true;
        applyBtn.innerHTML = '<span class="spinner-border spinner-border-sm me-1"></span>Applying...';

        try {
            const resp = await fetch(`/api/backfill/apply/${currentTripId}`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ createVisits, confirmedSuggestions, deleteVisitIds })
            });

            const result = await resp.json();

            if (result.success) {
                const data = result.data;
                const totalCreated = data.visitsCreated + data.suggestionsConfirmed;
                let message = `Created ${totalCreated} visits`;
                if (data.visitsCreated > 0 && data.suggestionsConfirmed > 0) {
                    message = `Created ${totalCreated} visits (${data.visitsCreated} confirmed, ${data.suggestionsConfirmed} from suggestions)`;
                }
                if (data.visitsDeleted > 0) {
                    message += `, deleted ${data.visitsDeleted} visits`;
                }
                message += '.';
                wayfarer.showAlert('success', message);
                backfillModal?.hide();
            } else {
                wayfarer.showAlert('danger', result.message || 'Failed to apply changes.');
            }
        } catch (err) {
            console.error('Backfill apply error:', err);
            wayfarer.showAlert('danger', 'An error occurred while applying changes.');
        } finally {
            applyBtn.disabled = false;
            applyBtn.innerHTML = '<i class="bi bi-check-lg me-1"></i>Apply Changes';
        }
    };

    /**
     * Handles the clear all visits action.
     * @param {string} tripId - The trip ID.
     * @param {string} tripName - The trip name for display.
     */
    const handleClearVisits = (tripId, tripName) => {
        wayfarer.showConfirmationModal({
            title: "Clear All Visits",
            message: `Are you sure you want to delete all visits for "${tripName}"? This action cannot be undone.`,
            confirmText: "Clear All",
            onConfirm: async () => {
                try {
                    const resp = await fetch(`/api/backfill/clear/${tripId}`, {
                        method: 'DELETE'
                    });

                    const result = await resp.json();

                    if (result.success) {
                        wayfarer.showAlert('success', result.data.message);
                    } else {
                        wayfarer.showAlert('danger', result.message || 'Failed to clear visits.');
                    }
                } catch (err) {
                    console.error('Clear visits error:', err);
                    wayfarer.showAlert('danger', 'An error occurred while clearing visits.');
                }
            }
        });
    };

    // Wire up backfill modal buttons
    analyzeBtn?.addEventListener('click', handleAnalyzeClick);
    applyBtn?.addEventListener('click', handleApplyClick);

    // Search input handler: re-renders all tabs with filtered data (debounced to avoid
    // excessive DOM rebuilds during fast typing)
    let searchDebounceTimer = null;
    document.getElementById('backfill-search')?.addEventListener('input', (e) => {
        clearTimeout(searchDebounceTimer);
        searchDebounceTimer = setTimeout(() => rerenderBackfillTabs(e.target.value.trim()), 180);
    });

    // Reset modal when hidden
    backfillModalEl?.addEventListener('hidden.bs.modal', resetBackfillModal);

    // Initialize Tippy tooltips when modal is shown
    backfillModalEl?.addEventListener('shown.bs.modal', () => {
        // Initialize Tippy for all help icons in the modal
        if (typeof tippy !== 'undefined') {
            backfillModalEl.querySelectorAll('.backfill-help[data-tippy-content]').forEach(el => {
                // Skip if already initialized
                if (el._tippy) return;
                tippy(el, {
                    appendTo: () => document.body,
                    allowHTML: true,
                    interactive: true,
                    placement: 'top',
                    maxWidth: 350,
                    theme: 'light-border',
                    popperOptions: {
                        modifiers: [{
                            name: 'zIndex',
                            options: { zIndex: 2000 }  // Must exceed Bootstrap modal (1050)
                        }]
                    }
                });
            });
        }
    });

    // Select/Deselect all handlers for suggested visits.
    // Must update persistentCheckState for ALL items (including those hidden by search filter)
    // to prevent filtered-out items from retaining stale state when the filter is cleared.
    document.getElementById('suggested-select-all')?.addEventListener('click', (e) => {
        e.preventDefault();
        document.querySelectorAll('.suggested-visit-check').forEach(cb => cb.checked = true);
        rawBackfillData?.suggestedVisits.forEach(v =>
            persistentCheckState.set(`suggested-${v.placeId}-${v.visitDate}`, true));
        updateActionSummary();
    });
    document.getElementById('suggested-deselect-all')?.addEventListener('click', (e) => {
        e.preventDefault();
        document.querySelectorAll('.suggested-visit-check').forEach(cb => cb.checked = false);
        rawBackfillData?.suggestedVisits.forEach(v =>
            persistentCheckState.set(`suggested-${v.placeId}-${v.visitDate}`, false));
        updateActionSummary();
    });

    // Select/Deselect all handlers for stale visits
    document.getElementById('stale-select-all')?.addEventListener('click', (e) => {
        e.preventDefault();
        document.querySelectorAll('.stale-visit-check').forEach(cb => cb.checked = true);
        rawBackfillData?.staleVisits.forEach(v =>
            persistentCheckState.set(`stale-${v.visitId}`, true));
        updateActionSummary();
    });
    document.getElementById('stale-deselect-all')?.addEventListener('click', (e) => {
        e.preventDefault();
        document.querySelectorAll('.stale-visit-check').forEach(cb => cb.checked = false);
        rawBackfillData?.staleVisits.forEach(v =>
            persistentCheckState.set(`stale-${v.visitId}`, false));
        updateActionSummary();
    });

    // Select/Deselect all handlers for existing visits
    document.getElementById('existing-select-all')?.addEventListener('click', (e) => {
        e.preventDefault();
        document.querySelectorAll('.existing-visit-check').forEach(cb => cb.checked = true);
        rawBackfillData?.existingVisits.forEach(v =>
            persistentCheckState.set(`existing-${v.visitId}`, true));
        updateActionSummary();
    });
    document.getElementById('existing-deselect-all')?.addEventListener('click', (e) => {
        e.preventDefault();
        document.querySelectorAll('.existing-visit-check').forEach(cb => cb.checked = false);
        rawBackfillData?.existingVisits.forEach(v =>
            persistentCheckState.set(`existing-${v.visitId}`, false));
        updateActionSummary();
    });

    /* ------------------------------------------------ Place Context Map */
    const placeContextModalEl = document.getElementById('placeContextModal');
    const placeContextModal = placeContextModalEl ? new bootstrap.Modal(placeContextModalEl) : null;
    let contextMap = null;
    let contextMarkersGroup = null;
    let mapResizeObserver = null;

    // Map tiles config (proxy URL + attribution) injected by layout
    const tilesConfig = window.wayfarerTileConfig || {};
    const tilesUrl = tilesConfig.tilesUrl || `${window.location.origin}/Public/tiles/{z}/{x}/{y}.png`;
    const tilesAttribution = tilesConfig.attribution || '&copy; OpenStreetMap contributors';

    // Wayfarer icon dimensions
    const WF_WIDTH = 28;
    const WF_HEIGHT = 45;
    const WF_ANCHOR = [14, 45];

    /**
     * Double requestAnimationFrame helper for deterministic layout settling.
     * Waits for two paint cycles to ensure all CSS/layout calculations are complete.
     * @returns {Promise<void>}
     */
    const waitForLayoutSettle = () => new Promise(resolve => {
        requestAnimationFrame(() => requestAnimationFrame(resolve));
    });

    /**
     * Builds the PNG icon URL for Wayfarer markers.
     * @param {string} iconName - The icon name.
     * @param {string} bgClass - The background color class.
     * @returns {string} The icon URL.
     */
    const buildPngIconUrl = (iconName, bgClass) =>
        `/icons/wayfarer-map-icons/dist/png/marker/${bgClass}/${iconName}.png`;

    /**
     * Initializes the place context map.
     * Only creates the map if the container has valid dimensions.
     * @returns {boolean} True if map was initialized, false if container not ready.
     */
    const initContextMap = () => {
        // Prevent double initialization
        if (contextMap) {
            return true;
        }

        const container = document.getElementById('placeContextMap');
        if (!container) return false;

        // Check if container has actual dimensions
        const rect = container.getBoundingClientRect();
        if (rect.width === 0 || rect.height === 0) {
            return false;
        }

        contextMap = L.map(container, { zoomAnimation: true }).setView([0, 0], 13);

        L.tileLayer(tilesUrl, {
            maxZoom: 19,
            attribution: tilesAttribution
        }).addTo(contextMap);

        contextMap.attributionControl.setPrefix(
            '&copy; <a href="https://wayfarer.stefk.me" title="Powered by Wayfarer, made by Stef" target="_blank">Wayfarer</a> | ' +
            '<a href="https://stefk.me" title="Check my blog" target="_blank">Stef K</a> | ' +
            '&copy; <a href="https://leafletjs.com/" target="_blank">Leaflet</a>'
        );

        // Add zoom level control with ruler tool
        addZoomLevelControl(contextMap);

        contextMarkersGroup = L.featureGroup().addTo(contextMap);

        return true;
    };

    /**
     * Destroys the current map instance for clean re-initialization.
     */
    const destroyContextMap = () => {
        if (contextMap) {
            contextMap.off();
            contextMap.remove();
            contextMap = null;
            contextMarkersGroup = null;
        }
    };

    /**
     * Fetches candidate locations from the API.
     * @param {Object} params - The query parameters.
     * @returns {Promise<Object>} The API response data.
     */
    const fetchCandidateLocations = async (params) => {
        const queryParams = new URLSearchParams({
            placeId: params.placeId,
            lat: params.lat,
            lon: params.lon,
            firstSeenUtc: params.firstSeenUtc,
            lastSeenUtc: params.lastSeenUtc,
            radius: params.radius || 7500,
            pageSize: params.pageSize || 200
        });

        const resp = await fetch(`/api/backfill/candidate-locations?${queryParams}`);
        const result = await resp.json();

        if (!result.success) {
            throw new Error(result.message || 'Failed to fetch locations');
        }

        return result.data;
    };

    /**
     * Renders markers on the context map.
     * @param {Object} place - The place data with lat, lon, iconName, markerColor.
     * @param {Array} locations - Array of location pings.
     */
    const renderContextMarkers = async (place, locations) => {
        if (!contextMap || !contextMarkersGroup) return;

        contextMarkersGroup.clearLayers();

        // Add place marker (main marker)
        const iconUrl = buildPngIconUrl(place.iconName || 'marker', place.markerColor || 'bg-blue');
        const placeMarker = L.marker([place.lat, place.lon], {
            icon: L.icon({
                iconUrl,
                iconSize: [WF_WIDTH, WF_HEIGHT],
                iconAnchor: WF_ANCHOR,
                className: 'map-icon'
            }),
            zIndexOffset: 1000 // Ensure place marker is on top
        });

        placeMarker.bindTooltip(`
            <strong>${escapeHtml(place.name)}</strong>
            ${place.regionName ? `<br><small class="text-muted">${escapeHtml(place.regionName)}</small>` : ''}
        `, {
            direction: 'top',
            className: 'place-context-tooltip'
        });

        contextMarkersGroup.addLayer(placeMarker);

        // Add location ping markers
        locations.forEach(loc => {
            const pingMarker = L.circleMarker([loc.latitude, loc.longitude], {
                radius: 6,
                color: '#0d6efd',
                fillColor: '#0d6efd',
                fillOpacity: 0.6,
                weight: 2
            });

            // Build tooltip content for the ping
            const timestamp = new Date(loc.localTimestamp);
            const timeStr = timestamp.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
            const dateStr = timestamp.toLocaleDateString();

            let tooltipHtml = `
                <div class="ping-tooltip">
                    <strong>${timeStr}</strong> <small class="text-muted">${dateStr}</small>
                    <br><span class="badge bg-secondary">${Math.round(loc.distanceMeters)}m from place</span>
            `;

            if (loc.accuracy) {
                tooltipHtml += `<br><small>Accuracy: ±${Math.round(loc.accuracy)}m</small>`;
            }
            if (loc.speed) {
                tooltipHtml += `<br><small>Speed: ${(loc.speed * 3.6).toFixed(1)} km/h</small>`;
            }
            if (loc.activity) {
                tooltipHtml += `<br><small>Activity: ${escapeHtml(loc.activity)}</small>`;
            }
            if (loc.address) {
                tooltipHtml += `<br><small>${escapeHtml(loc.address)}</small>`;
            }

            tooltipHtml += '</div>';

            pingMarker.bindTooltip(tooltipHtml, {
                direction: 'top',
                className: 'ping-context-tooltip'
            });

            contextMarkersGroup.addLayer(pingMarker);
        });

        // Fit map to show all markers with padding
        if (contextMarkersGroup.getLayers().length > 0) {
            contextMap.fitBounds(contextMarkersGroup.getBounds(), { padding: [40, 40] });
            // Wait for layout to settle after bounds change, then recalculate tiles
            await waitForLayoutSettle();
            contextMap.invalidateSize();
        }
    };

    /**
     * Generates a Google Maps link for a place.
     * @param {Object} placeData - The place data with coordinates and name.
     * @returns {string} HTML string with Google Maps link.
     */
    const generateGoogleMapsLink = (placeData) => {
        if (!placeData?.latitude || !placeData?.longitude) return '';
        const query = placeData.placeName
            ? `${placeData.placeName} (${placeData.latitude.toFixed(6)},${placeData.longitude.toFixed(6)})`
            : `${placeData.latitude.toFixed(6)},${placeData.longitude.toFixed(6)}`;
        const q = encodeURIComponent(query);
        return `<a href="https://www.google.com/maps/search/?api=1&query=${q}" target="_blank" class="btn btn-outline-primary btn-sm" title="View in Google Maps"><i class="bi bi-globe-europe-africa"></i> Maps</a>`;
    };

    /**
     * Updates the info panel below the map.
     * @param {Array} locations - Array of location pings.
     * @param {Object} [placeData] - Optional place data for external links.
     */
    const updateContextInfo = (locations, placeData = null) => {
        const countEl = document.getElementById('context-ping-count');
        const timeEl = document.getElementById('context-time-range');
        const distEl = document.getElementById('context-avg-distance');
        const closestEl = document.getElementById('context-closest-ping');
        const furthestEl = document.getElementById('context-furthest-ping');
        const linksEl = document.getElementById('context-external-links');

        if (countEl) countEl.textContent = locations.length;

        if (locations.length > 0) {
            // Calculate time range with date and time
            const times = locations.map(l => new Date(l.localTimestamp).getTime());
            const minTime = new Date(Math.min(...times));
            const maxTime = new Date(Math.max(...times));

            const formatDateTime = (d) => {
                const date = d.toLocaleDateString([], { month: 'short', day: 'numeric' });
                const time = d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
                return `${date} ${time}`;
            };

            if (timeEl) {
                if (minTime.toDateString() === maxTime.toDateString()) {
                    // Same day - show date once with time range
                    const date = minTime.toLocaleDateString([], { month: 'short', day: 'numeric' });
                    const startTime = minTime.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
                    const endTime = maxTime.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
                    timeEl.textContent = `${date} ${startTime} - ${endTime}`;
                } else {
                    // Different days - show full date+time for both
                    timeEl.textContent = `${formatDateTime(minTime)} - ${formatDateTime(maxTime)}`;
                }
            }

            // Calculate average distance
            const avgDist = locations.reduce((sum, l) => sum + l.distanceMeters, 0) / locations.length;
            if (distEl) distEl.textContent = `${Math.round(avgDist)}m`;

            // Calculate closest and furthest pings
            const distances = locations.map(l => l.distanceMeters);
            const closestDist = Math.min(...distances);
            const furthestDist = Math.max(...distances);

            if (closestEl) closestEl.textContent = `${Math.round(closestDist)}m`;
            if (furthestEl) furthestEl.textContent = `${Math.round(furthestDist)}m`;
        } else {
            if (timeEl) timeEl.textContent = '-';
            if (distEl) distEl.textContent = '-';
            if (closestEl) closestEl.textContent = '-';
            if (furthestEl) furthestEl.textContent = '-';
        }

        // Update external links
        if (linksEl && placeData) {
            linksEl.innerHTML = generateGoogleMapsLink(placeData) + ' ' + generateWikipediaLinkHtml(placeData.latitude, placeData.longitude, { query: placeData.placeName });
            initWikipediaPopovers(linksEl, { placement: 'top' });
        } else if (linksEl) {
            linksEl.innerHTML = '';
        }
    };

    // Store pending place data for when modal is shown
    let pendingPlaceData = null;
    let pendingPlaceType = null;

    /**
     * Shows the place context modal with map and location pings.
     * @param {Object} placeData - The place data from the analysis.
     * @param {string} type - 'candidate', 'suggestion', 'stale', or 'existing'.
     */
    const showPlaceContext = (placeData, type) => {
        // Update modal title with link to trip place
        const titleEl = document.getElementById('placeContextTitle');
        if (titleEl) {
            const typeLabel = {
                'candidate': 'Confirmed Visit',
                'suggestion': 'Suggested Visit',
                'stale': 'Stale Visit',
                'existing': 'Existing Visit'
            }[type] || 'Place Context';

            // Build link to user's private trip view page with place coordinates for focus
            const tripViewUrl = currentTripId && placeData.latitude && placeData.longitude
                ? `/User/Trip/View/${currentTripId}?lat=${placeData.latitude.toFixed(6)}&lon=${placeData.longitude.toFixed(6)}&zoom=17&placeId=${placeData.placeId}`
                : null;

            const placeNameHtml = tripViewUrl
                ? `<a href="${tripViewUrl}" target="_blank" class="text-decoration-none" title="View place in trip">${escapeHtml(placeData.placeName)} <i class="bi bi-box-arrow-up-right small"></i></a>`
                : escapeHtml(placeData.placeName);

            titleEl.innerHTML = `<i class="bi bi-geo-alt me-1"></i>${placeNameHtml} <small class="text-muted fw-normal">- ${typeLabel}</small>`;
        }

        // Store data for when modal is fully shown
        pendingPlaceData = placeData;
        pendingPlaceType = type;

        // Show modal - map will be initialized in shown.bs.modal event
        placeContextModal?.show();
    };

    /**
     * Loads place data after map is initialized.
     * Map should already be created by ResizeObserver before this is called.
     */
    const initPlaceContextAfterShow = async () => {
        if (!pendingPlaceData) return;
        if (!contextMap) return; // Map must be initialized first

        const placeData = pendingPlaceData;
        pendingPlaceData = null;
        pendingPlaceType = null;

        // Wait for layout to settle, then ensure map knows its size
        await waitForLayoutSettle();
        contextMap.invalidateSize();

        // Check if we have coordinates
        if (!placeData.latitude || !placeData.longitude) {
            // No coordinates available - show message
            renderContextMarkers({
                lat: 0,
                lon: 0,
                name: placeData.placeName,
                regionName: placeData.regionName
            }, []);
            updateContextInfo([], placeData);
            return;
        }

        // Render place marker immediately
        renderContextMarkers({
            lat: placeData.latitude,
            lon: placeData.longitude,
            name: placeData.placeName,
            regionName: placeData.regionName,
            iconName: placeData.iconName,
            markerColor: placeData.markerColor
        }, []);

        // Fetch and render location pings (only if we have time data)
        if (placeData.firstSeenUtc && placeData.lastSeenUtc) {
            try {
                const data = await fetchCandidateLocations({
                    placeId: placeData.placeId,
                    lat: placeData.latitude,
                    lon: placeData.longitude,
                    firstSeenUtc: placeData.firstSeenUtc,
                    lastSeenUtc: placeData.lastSeenUtc
                });

                renderContextMarkers({
                    lat: placeData.latitude,
                    lon: placeData.longitude,
                    name: placeData.placeName,
                    regionName: placeData.regionName,
                    iconName: placeData.iconName,
                    markerColor: placeData.markerColor
                }, data.locations);

                updateContextInfo(data.locations, placeData);
            } catch (err) {
                console.error('Failed to fetch candidate locations:', err);
                updateContextInfo([], placeData);
            }
        } else {
            updateContextInfo([], placeData);
        }
    };

    // Reset map and cleanup when modal is hidden
    placeContextModalEl?.addEventListener('hidden.bs.modal', () => {
        // Disconnect resize observer
        if (mapResizeObserver) {
            mapResizeObserver.disconnect();
            mapResizeObserver = null;
        }
        // Cleanup map
        destroyContextMap();
        pendingPlaceData = null;
        pendingPlaceType = null;
    });

    // Initialize map when modal is fully shown using ResizeObserver for deterministic timing
    placeContextModalEl?.addEventListener('shown.bs.modal', () => {
        const container = document.getElementById('placeContextMap');
        if (!container) return;

        // Use ResizeObserver to detect when container has dimensions
        mapResizeObserver = new ResizeObserver((entries) => {
            for (const entry of entries) {
                const { width, height } = entry.contentRect;

                // Container has valid dimensions - initialize map once
                if (width > 0 && height > 0) {
                    if (!contextMap) {
                        // First time - initialize map then load data
                        if (initContextMap()) {
                            initPlaceContextAfterShow();
                        }
                    } else {
                        // Subsequent resizes - just invalidate size
                        contextMap.invalidateSize();
                    }
                }
            }
        });

        mapResizeObserver.observe(container);
    });

    /* ------------------------------------------------ boot */
    document.addEventListener('DOMContentLoaded', () => {
        // Clipboard copy handling
        document.addEventListener('click', async (e) => {
            const el = e.target.closest('a.copy-url');
            if (!el) return;

            e.preventDefault();

            const url = el.dataset.url;
            try {
                await navigator.clipboard.writeText(`${window.location.origin}${url}`);
                // Use toast instead of alert to avoid viewport jumps
                if (wayfarer.showToast) {
                    wayfarer.showToast('success', 'URL copied to clipboard!');
                } else {
                    wayfarer.showAlert('success', 'URL copied to clipboard!');
                }
            } catch (err) {
                if (wayfarer.showToast) {
                    wayfarer.showToast('danger', 'Failed to copy URL.');
                } else {
                    wayfarer.showAlert('danger', 'Failed to copy URL.');
                }
            }
        });

        // Delete trip handlers (both standalone buttons and dropdown items)
        document.querySelectorAll('.btn-trip-delete')
            .forEach(btn => btn.addEventListener('click', (e) => handleDeleteClick(btn, e)));

        // Backfill analyze handlers
        document.querySelectorAll('.btn-backfill-analyze')
            .forEach(btn => btn.addEventListener('click', (e) => {
                e.preventDefault();
                currentTripId = btn.dataset.tripId;
                if (tripNameEl) tripNameEl.textContent = btn.dataset.tripName || '';
                resetBackfillModal();
                backfillModal?.show();
            }));

        // Clear all visits handlers
        document.querySelectorAll('.btn-backfill-clear')
            .forEach(btn => btn.addEventListener('click', (e) => {
                e.preventDefault();
                handleClearVisits(btn.dataset.tripId, btn.dataset.tripName || 'this trip');
            }));
    });
})();
