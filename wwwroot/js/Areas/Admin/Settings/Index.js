document.addEventListener('DOMContentLoaded', () => {
    document.getElementById('clearAllCache')?.addEventListener('click', (e) => {
        e.preventDefault();
        if (e.currentTarget.classList.contains('disabled')) return;
        deleteAllMapTileCache();
    });

    document.getElementById('clearLruCache')?.addEventListener('click', (e) => {
        e.preventDefault();
        if (e.currentTarget.classList.contains('disabled')) return;
        deleteLruCache();
    });

    document.getElementById('clearMbtilesCache')?.addEventListener('click', (e) => {
        e.preventDefault();
        deleteMbtilesCache();
    });

    // Time threshold warning for 2-minute option
    const timeThresholdSelect = document.getElementById('timeThresholdSelect');
    const timeThresholdWarning = document.getElementById('timeThresholdWarning');

    if (timeThresholdSelect && timeThresholdWarning) {
        const updateWarningVisibility = () => {
            if (timeThresholdSelect.value === '2') {
                timeThresholdWarning.classList.remove('d-none');
            } else {
                timeThresholdWarning.classList.add('d-none');
            }
        };

        // Check on page load
        updateWarningVisibility();

        // Check on change
        timeThresholdSelect.addEventListener('change', updateWarningVisibility);
    }

    // Tile provider UI: toggle preset details, custom inputs, and API key visibility.
    const tileProviderKey = document.getElementById('TileProviderKey');
    const tileProviderTemplate = document.getElementById('TileProviderUrlTemplate');
    const tileProviderAttribution = document.getElementById('TileProviderAttribution');
    const tileProviderApiKeyRow = document.getElementById('tileProviderApiKeyRow');
    const tileProviderApiKey = document.getElementById('TileProviderApiKey');

    if (tileProviderKey && tileProviderTemplate && tileProviderAttribution && tileProviderApiKeyRow && tileProviderApiKey) {
        const customKey = tileProviderKey.dataset.customKey || 'custom';
        const customState = {
            template: tileProviderTemplate.value,
            attribution: tileProviderAttribution.value
        };

        const setApiKeyVisibility = (requiresApiKey) => {
            tileProviderApiKeyRow.classList.toggle('d-none', !requiresApiKey);
            if (!requiresApiKey) {
                tileProviderApiKey.value = '';
            }
        };

        const applyTileProviderSelection = () => {
            const selectedOption = tileProviderKey.options[tileProviderKey.selectedIndex];
            const isCustom = selectedOption?.value === customKey;
            const presetTemplate = selectedOption?.dataset.template || '';
            const presetAttribution = selectedOption?.dataset.attribution || '';
            const presetRequiresKey = selectedOption?.dataset.requiresKey === 'true';

            if (isCustom) {
                tileProviderTemplate.readOnly = false;
                tileProviderAttribution.readOnly = false;
                tileProviderTemplate.value = customState.template;
                tileProviderAttribution.value = customState.attribution;
            } else {
                tileProviderTemplate.readOnly = true;
                tileProviderAttribution.readOnly = true;
                tileProviderTemplate.value = presetTemplate;
                tileProviderAttribution.value = presetAttribution;
            }

            const requiresApiKey = isCustom
                ? tileProviderTemplate.value.includes('{apiKey}')
                : presetRequiresKey;
            setApiKeyVisibility(requiresApiKey);
        };

        tileProviderKey.addEventListener('change', applyTileProviderSelection);
        tileProviderTemplate.addEventListener('input', () => {
            if (tileProviderKey.value === customKey) {
                customState.template = tileProviderTemplate.value;
                applyTileProviderSelection();
            }
        });
        tileProviderAttribution.addEventListener('input', () => {
            if (tileProviderKey.value === customKey) {
                customState.attribution = tileProviderAttribution.value;
            }
        });

        applyTileProviderSelection();
    }

    // On page load, check if a cache purge is already in progress and reconnect SSE.
    checkPurgeStatusOnLoad();
});

/**
 * Gets the anti-forgery token from the page for AJAX POST requests.
 * @returns {string} The anti-forgery token value or empty string if not found.
 */
const getAntiForgeryToken = () => {
    return document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
};

// ── Tile cache purge (background with SSE progress) ────────────────────

/** @type {EventSource|null} Active SSE connection for purge progress. */
let purgeEventSource = null;

/**
 * Checks if a cache purge is in progress on page load and reconnects the SSE
 * progress stream if so. This handles the case where the admin refreshes the
 * page mid-purge.
 */
const checkPurgeStatusOnLoad = () => {
    fetch('/Admin/Settings/TileCachePurgeStatus')
        .then(r => r.json())
        .then(data => {
            if (data.inProgress) {
                showPurgeProgress();
                connectPurgeSse();
            }
        })
        .catch(() => { /* status endpoint unavailable — ignore */ });
};

/**
 * Opens an SSE connection to receive purge progress events and updates the UI.
 */
const connectPurgeSse = () => {
    if (purgeEventSource) return; // already connected

    purgeEventSource = new EventSource('/Admin/Settings/TileCachePurgeSse');

    purgeEventSource.onmessage = (event) => {
        let data;
        try {
            data = JSON.parse(event.data);
        } catch {
            return; // ignore malformed SSE payloads
        }

        switch (data.eventType) {
            case 'started':
                showPurgeProgress();
                break;

            case 'progress':
                updatePurgeProgress(data.percentComplete, data.deletedFiles, data.totalFiles);
                break;

            case 'completed':
                if (data.cacheStatus) updateCacheStatusDom(data.cacheStatus);
                hidePurgeProgress();
                wayfarer.showAlert('success', data.message || 'Cache purge completed.');
                closePurgeSse();
                break;

            case 'failed':
                hidePurgeProgress();
                wayfarer.showAlert('danger', `Cache purge failed: ${data.errorMessage || 'Unknown error'}`);
                closePurgeSse();
                break;
        }
    };

    purgeEventSource.onerror = () => {
        // Connection lost — close and retry after a short delay.
        // If the purge is still running, reconnect; otherwise hide the progress bar.
        closePurgeSse();
        setTimeout(() => {
            fetch('/Admin/Settings/TileCachePurgeStatus')
                .then(r => r.json())
                .then(data => {
                    if (data.inProgress) {
                        connectPurgeSse();
                    } else {
                        hidePurgeProgress();
                    }
                })
                .catch(() => hidePurgeProgress());
        }, 2000);
    };
};

/**
 * Closes the active SSE connection for purge progress.
 */
const closePurgeSse = () => {
    if (purgeEventSource) {
        purgeEventSource.close();
        purgeEventSource = null;
    }
};

/**
 * Shows the purge progress bar and disables both cache-clear buttons.
 */
const showPurgeProgress = () => {
    const progressContainer = document.getElementById('cachePurgeProgress');
    if (progressContainer) progressContainer.style.display = 'block';
    setPurgeButtonsDisabled(true);
    updatePurgeProgress(0, 0, 0);
};

/**
 * Hides the purge progress bar and re-enables the cache-clear buttons.
 */
const hidePurgeProgress = () => {
    const progressContainer = document.getElementById('cachePurgeProgress');
    if (progressContainer) progressContainer.style.display = 'none';
    setPurgeButtonsDisabled(false);
};

/**
 * Updates the progress bar width, text, and aria attributes.
 * @param {number} percent - Progress percentage (0-100).
 * @param {number} deleted - Number of files deleted so far.
 * @param {number} total - Total files to delete.
 */
const updatePurgeProgress = (percent, deleted, total) => {
    const bar = document.getElementById('cachePurgeBar');
    const text = document.getElementById('cachePurgeText');
    if (bar) {
        bar.style.width = `${percent}%`;
        bar.textContent = `${percent}%`;
        bar.setAttribute('aria-valuenow', percent);
    }
    if (text) {
        text.textContent = total > 0
            ? `Deleting... ${deleted} / ${total} files (${percent}%)`
            : 'Starting purge...';
    }
};

/**
 * Enables or disables both cache-clear buttons.
 * @param {boolean} disabled - Whether to disable the buttons.
 */
const setPurgeButtonsDisabled = (disabled) => {
    const lruBtn = document.getElementById('clearLruCache');
    const allBtn = document.getElementById('clearAllCache');
    if (lruBtn) {
        lruBtn.classList.toggle('disabled', disabled);
        lruBtn.setAttribute('aria-disabled', disabled);
    }
    if (allBtn) {
        allBtn.classList.toggle('disabled', disabled);
        allBtn.setAttribute('aria-disabled', disabled);
    }
};

/**
 * Updates the cache status DOM elements after a completed purge.
 * @param {object} status - Cache status object from the server.
 */
const updateCacheStatusDom = (status) => {
    if (!status) return;
    const set = (id, val) => { const el = document.getElementById(id); if (el) el.textContent = val; };
    set('TotalCacheFiles', status.totalCacheFiles);
    set('LruTotalFiles', status.lruTotalFiles);
    set('TotalCacheSize', status.totalCacheSize + ' MB');
    set('TotalCacheSizeGB', status.totalCacheSizeGB + ' GB');
    set('TotalLru', status.totalLru + ' MB');
    set('TotalLruGB', status.totalLruGB + ' GB');
};

/**
 * Initiates a cache purge via POST. On 202 Accepted, connects SSE for progress.
 * On 409 Conflict, shows a warning that a purge is already running.
 * @param {string} url - The purge endpoint URL.
 * @param {string} errorLabel - Human-readable label for error messages.
 */
const startCachePurge = (url, errorLabel) => {
    fetch(url, {
        method: 'POST',
        headers: {
            'Content-Type': 'application/json',
            'RequestVerificationToken': getAntiForgeryToken()
        }
    })
        .then(response => {
            if (response.status === 202) {
                showPurgeProgress();
                connectPurgeSse();
            } else if (response.status === 409) {
                response.json().then(data => {
                    wayfarer.showAlert('warning', data.message || 'A cache purge is already in progress.');
                });
            } else {
                response.json().then(data => {
                    wayfarer.showAlert('danger', data?.message || `Failed to start ${errorLabel}.`);
                }).catch(() => {
                    wayfarer.showAlert('danger', `Failed to start ${errorLabel}.`);
                });
            }
        })
        .catch(error => {
            console.error('error:', error);
            wayfarer.showAlert('danger', `Failed to start ${errorLabel}. ${error}`);
        });
};

/**
 * Deletes all map tile cache from zoom level 1 to max from file system and database.
 */
const deleteAllMapTileCache = () => {
    wayfarer.showConfirmationModal({
        title: "Confirm Deletion",
        message: "Are you sure you want to delete all map tile cache? This action cannot be undone.",
        confirmText: "Delete",
        onConfirm: () => startCachePurge('/Admin/Settings/DeleteAllMapTileCache', 'full cache purge')
    });
};

/**
 * Deletes Least Recently Used map tile cache (zoom levels >= 9) from file system and database.
 */
const deleteLruCache = () => {
    wayfarer.showConfirmationModal({
        title: "Confirm Deletion",
        message: "Are you sure you want to delete the Least Recently Used map tile cache (zoom levels >= 9)? This action cannot be undone.",
        confirmText: "Delete",
        onConfirm: () => startCachePurge('/Admin/Settings/DeleteLruCache', 'LRU cache purge')
    });
};

/**
 * Deletes all MBTiles cache used for mobile app.
 */
const deleteMbtilesCache = () => {
    wayfarer.showConfirmationModal({
        title: "Confirm MBTiles Deletion",
        message: "Are you sure you want to delete all MBTiles files used for mobile app caching?",
        confirmText: "Delete",
        onConfirm: () => {
            fetch("/Admin/Settings/ClearMbtilesCache", {
                method: "POST",
                headers: {
                    "Content-Type": "application/json",
                    "RequestVerificationToken": getAntiForgeryToken()
                }
            })
                .then(response => {
                    if (response.ok) {
                        location.reload(); // simplest way to reflect updated MB/GB/file count
                    } else {
                        wayfarer.showAlert("danger", "Failed to delete MBTiles cache.");
                    }
                })
                .catch(error => {
                    console.error("error:", error);
                    wayfarer.showAlert("danger", `Failed to delete MBTiles cache. ${error}`);
                });
        }
    });
};
