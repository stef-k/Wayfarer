/* trips.js Trip index view functionality */
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
     * Renders the backfill preview results.
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

        // New visits
        const newVisitsList = document.getElementById('new-visits-list');
        const noNewVisits = document.getElementById('no-new-visits');
        const newVisitsHint = document.getElementById('new-visits-hint');
        document.getElementById('new-visits-count').textContent = data.newVisits.length;

        if (data.newVisits.length === 0) {
            newVisitsList?.classList.add('d-none');
            noNewVisits?.classList.remove('d-none');
            newVisitsHint?.classList.add('d-none');
        } else {
            newVisitsList?.classList.remove('d-none');
            noNewVisits?.classList.add('d-none');
            newVisitsHint?.classList.remove('d-none');
            newVisitsList.innerHTML = data.newVisits.map(v => `
                <div class="list-group-item d-flex justify-content-between align-items-center py-2">
                    <div>
                        <div class="fw-medium text-success"><i class="bi bi-plus-circle me-1"></i>${escapeHtml(v.placeName)}</div>
                        <small class="text-muted">${escapeHtml(v.regionName)} &middot; ${v.visitDate}</small>
                    </div>
                    <div class="text-end">
                        <span class="badge ${getConfidenceBadgeClass(v.confidence)}"
                              title="Confidence based on location count and proximity (avg ${Math.round(v.avgDistanceMeters)}m from place)">${v.confidence}% Confidence</span>
                        <small class="text-muted d-block">${v.locationCount} hits</small>
                    </div>
                </div>
            `).join('');
        }

        // Suggested visits (Consider Also tab)
        const suggestedVisitsList = document.getElementById('suggested-visits-list');
        const noSuggestedVisits = document.getElementById('no-suggested-visits');
        const suggestedVisitsHint = document.getElementById('suggested-visits-hint');
        const suggestedSelectAllWrapper = document.getElementById('suggested-select-all-wrapper');
        const suggestedVisits = data.suggestedVisits || [];
        document.getElementById('suggested-visits-count').textContent = suggestedVisits.length;

        if (suggestedVisits.length === 0) {
            suggestedVisitsList?.classList.add('d-none');
            noSuggestedVisits?.classList.remove('d-none');
            suggestedVisitsHint?.classList.add('d-none');
            suggestedSelectAllWrapper?.classList.add('d-none');
        } else {
            suggestedVisitsList?.classList.remove('d-none');
            noSuggestedVisits?.classList.add('d-none');
            suggestedVisitsHint?.classList.remove('d-none');
            suggestedSelectAllWrapper?.classList.remove('d-none');
            suggestedVisitsList.innerHTML = suggestedVisits.map(v => `
                <div class="list-group-item d-flex justify-content-between align-items-center py-2">
                    <div class="form-check">
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
                    <div class="text-end">
                        <span class="badge bg-info" title="${escapeHtml(v.suggestionReason)}">
                            ${v.hasUserCheckin ? '<i class="bi bi-check-circle me-1"></i>' : ''}
                            ${escapeHtml(v.suggestionReason)}
                        </span>
                        <small class="text-muted d-block">${Math.round(v.minDistanceMeters)}m min dist</small>
                    </div>
                </div>
            `).join('');

            // Add change listeners to update action summary
            suggestedVisitsList.querySelectorAll('.suggested-visit-check').forEach(cb => {
                cb.addEventListener('change', updateActionSummary);
            });
        }

        // Stale visits
        const staleVisitsList = document.getElementById('stale-visits-list');
        const noStaleVisits = document.getElementById('no-stale-visits');
        const staleVisitsHint = document.getElementById('stale-visits-hint');
        const staleSelectAllWrapper = document.getElementById('stale-select-all-wrapper');
        document.getElementById('stale-visits-count').textContent = data.staleVisits.length;

        if (data.staleVisits.length === 0) {
            staleVisitsList?.classList.add('d-none');
            noStaleVisits?.classList.remove('d-none');
            staleVisitsHint?.classList.add('d-none');
            staleSelectAllWrapper?.classList.add('d-none');
        } else {
            staleVisitsList?.classList.remove('d-none');
            noStaleVisits?.classList.add('d-none');
            staleVisitsHint?.classList.remove('d-none');
            staleSelectAllWrapper?.classList.remove('d-none');
            staleVisitsList.innerHTML = data.staleVisits.map(v => `
                <div class="list-group-item d-flex justify-content-between align-items-center py-2">
                    <div class="form-check">
                        <input class="form-check-input stale-visit-check" type="checkbox"
                               value="${v.visitId}" id="stale-${v.visitId}" checked>
                        <label class="form-check-label" for="stale-${v.visitId}">
                            <span class="fw-medium text-danger"><i class="bi bi-trash me-1"></i>${escapeHtml(v.placeName)}</span>
                            <small class="text-muted d-block">${v.visitDate}</small>
                        </label>
                    </div>
                    <span class="badge bg-warning text-dark">${escapeHtml(v.reason)}</span>
                </div>
            `).join('');

            // Add change listeners to update action summary
            staleVisitsList.querySelectorAll('.stale-visit-check').forEach(cb => {
                cb.addEventListener('change', updateActionSummary);
            });
        }

        // Existing visits (with checkboxes for manual deletion)
        const existingVisitsList = document.getElementById('existing-visits-list');
        const noExistingVisits = document.getElementById('no-existing-visits');
        const existingVisitsHint = document.getElementById('existing-visits-hint');
        const existingSelectAllWrapper = document.getElementById('existing-select-all-wrapper');
        const existingVisits = data.existingVisits || [];
        document.getElementById('existing-visits-count').textContent = existingVisits.length;

        if (existingVisits.length === 0) {
            existingVisitsList?.classList.add('d-none');
            noExistingVisits?.classList.remove('d-none');
            existingVisitsHint?.classList.add('d-none');
            existingSelectAllWrapper?.classList.add('d-none');
        } else {
            existingVisitsList?.classList.remove('d-none');
            noExistingVisits?.classList.add('d-none');
            existingVisitsHint?.classList.remove('d-none');
            existingSelectAllWrapper?.classList.remove('d-none');
            existingVisitsList.innerHTML = existingVisits.map(v => `
                <div class="list-group-item d-flex justify-content-between align-items-center py-2">
                    <div class="form-check">
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
            `).join('');

            // Add change listeners to update action summary
            existingVisitsList.querySelectorAll('.existing-visit-check').forEach(cb => {
                cb.addEventListener('change', updateActionSummary);
            });
        }

        // Show apply button and action summary (always show for any available actions)
        const hasChanges = data.newVisits.length > 0 || suggestedVisits.length > 0 || data.staleVisits.length > 0 || existingVisits.length > 0;
        if (hasChanges) {
            applyBtn?.classList.remove('d-none');
            updateActionSummary();
        }
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

        const newVisitsCount = previewData.newVisits?.length || 0;
        const confirmedSuggestionsCount = document.querySelectorAll('.suggested-visit-check:checked').length;
        const staleDeleteCount = document.querySelectorAll('.stale-visit-check:checked').length;
        const manualDeleteCount = document.querySelectorAll('.existing-visit-check:checked').length;

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
     * Handles the analyze button click - fetches preview data from API.
     */
    const handleAnalyzeClick = async () => {
        if (!currentTripId) return;

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

    // Select/Deselect all handlers for suggested visits
    document.getElementById('suggested-select-all')?.addEventListener('click', (e) => {
        e.preventDefault();
        document.querySelectorAll('.suggested-visit-check').forEach(cb => cb.checked = true);
        updateActionSummary();
    });
    document.getElementById('suggested-deselect-all')?.addEventListener('click', (e) => {
        e.preventDefault();
        document.querySelectorAll('.suggested-visit-check').forEach(cb => cb.checked = false);
        updateActionSummary();
    });

    // Select/Deselect all handlers for stale visits
    document.getElementById('stale-select-all')?.addEventListener('click', (e) => {
        e.preventDefault();
        document.querySelectorAll('.stale-visit-check').forEach(cb => cb.checked = true);
        updateActionSummary();
    });
    document.getElementById('stale-deselect-all')?.addEventListener('click', (e) => {
        e.preventDefault();
        document.querySelectorAll('.stale-visit-check').forEach(cb => cb.checked = false);
        updateActionSummary();
    });

    // Select/Deselect all handlers for existing visits
    document.getElementById('existing-select-all')?.addEventListener('click', (e) => {
        e.preventDefault();
        document.querySelectorAll('.existing-visit-check').forEach(cb => cb.checked = true);
        updateActionSummary();
    });
    document.getElementById('existing-deselect-all')?.addEventListener('click', (e) => {
        e.preventDefault();
        document.querySelectorAll('.existing-visit-check').forEach(cb => cb.checked = false);
        updateActionSummary();
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
