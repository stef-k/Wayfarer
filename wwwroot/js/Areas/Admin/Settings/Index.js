import { message, number } from '../../../localization/messages.js';

/** Keeps the cache controls and feedback in the document locale. */
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

    const tileMetadataHotCacheInput = document.getElementById('TileMetadataHotCacheSizeMB');
    const tileMetadataHotCacheHint = document.getElementById('tileMetadataHotCacheHint');
    if (tileMetadataHotCacheInput && tileMetadataHotCacheHint) {
        const estimatedBytesPerEntry = Number(tileMetadataHotCacheInput.dataset.entryBytes || '768');

        const updateTileMetadataHotCacheHint = () => {
            const sizeMb = Number(tileMetadataHotCacheInput.value);
            if (tileMetadataHotCacheInput.value === '' || (sizeMb !== -1 && (sizeMb < 16 || sizeMb > 512))) {
                tileMetadataHotCacheHint.textContent = message('AdminSettings.Validation.HotCache',
                    'Tile metadata hot cache size must be -1 (disable) or between 16 and 512 MB.');
                return;
            }

            if (sizeMb === -1) {
                tileMetadataHotCacheHint.textContent = message('AdminSettings.HotCacheDisabled', 'Hot metadata cache disabled.');
                return;
            }

            const approximateEntries = Math.floor((sizeMb * 1024 * 1024) / estimatedBytesPerEntry);
            tileMetadataHotCacheHint.textContent = message('AdminSettings.HotCacheEntries', '{0} MB ≈ {1} metadata entries',
                number(sizeMb), number(approximateEntries));
        };

        updateTileMetadataHotCacheHint();
        tileMetadataHotCacheInput.addEventListener('input', updateTileMetadataHotCacheHint);
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

        const setApiKeyVisibility = (requiresApiKey, clearValue) => {
            tileProviderApiKeyRow.classList.toggle('d-none', !requiresApiKey);
            if (!requiresApiKey && clearValue) {
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
            setApiKeyVisibility(requiresApiKey, true);
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

        // Initialization is display-only: persisted recovery fields must not be rewritten.
        const selectedOption = tileProviderKey.options[tileProviderKey.selectedIndex];
        const isCustom = selectedOption?.value === customKey;
        const isPreserved = selectedOption?.dataset.preserved === 'true';
        tileProviderTemplate.readOnly = !isCustom;
        tileProviderAttribution.readOnly = !isCustom;
        setApiKeyVisibility(
            isPreserved || tileProviderTemplate.value.includes('{apiKey}') ||
            selectedOption?.dataset.requiresKey === 'true',
            false);
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
                wayfarer.showAlert('success', purgeCompletedMessage(data.purgeType));
                closePurgeSse();
                break;

            case 'failed':
                hidePurgeProgress();
                wayfarer.showAlert('danger', message('AdminSettings.PurgeFailed', 'Cache purge failed. Please try again.'));
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
    const percentText = number(percent / 100, { style: 'percent', maximumFractionDigits: 1 });
    if (bar) {
        bar.style.width = `${percent}%`;
        bar.textContent = percentText;
        bar.setAttribute('aria-valuenow', percent);
    }
    if (text) {
        text.textContent = total > 0
            ? message('AdminSettings.PurgeProgress', 'Deleting… {0} / {1} files ({2})',
                number(deleted), number(total), percentText)
            : message('AdminSettings.PurgeStarting', 'Starting purge...');
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
    set('TotalCacheFiles', number(status.totalCacheFiles));
    set('LruTotalFiles', number(status.lruTotalFiles));
    set('TotalCacheSize', number(status.totalCacheSize) + ' MB');
    set('TotalCacheSizeGB', number(status.totalCacheSizeGB) + ' GB');
    set('TotalLru', number(status.totalLru) + ' MB');
    set('TotalLruGB', number(status.totalLruGB) + ' GB');
};

/** Projects the completion type without using the background worker's locale. */
const purgeCompletedMessage = (purgeType) => {
    if (purgeType === 'all') return message('AdminSettings.PurgeFullCompleted', 'Full cache purge completed successfully.');
    if (purgeType === 'lru') return message('AdminSettings.PurgeLruCompleted', 'LRU cache purge completed successfully.');
    return message('AdminSettings.PurgeCompleted', 'Cache purge completed.');
};

/** Starts the existing operation and presents its response in the requesting document. */
const startCachePurge = async (url, failureMessage, startedMessage) => {
    try {
        const response = await fetch(url, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                'RequestVerificationToken': getAntiForgeryToken()
            }
        });
        if (response.status === 202) {
            wayfarer.showAlert('info', startedMessage);
            showPurgeProgress();
            connectPurgeSse();
        } else if (response.status === 409) {
            wayfarer.showAlert('warning', message('AdminSettings.PurgeBusy', 'A cache purge is already in progress.'));
        } else {
            wayfarer.showAlert('danger', failureMessage);
        }
    } catch (error) {
        console.error('Error starting cache purge:', error);
        wayfarer.showAlert('danger', failureMessage);
    }
};

/**
 * Deletes all map tile cache from zoom level 1 to max from file system and database.
 */
const deleteAllMapTileCache = () => {
    wayfarer.showConfirmationModal({
        title: message('AdminSettings.PurgeConfirmTitle', 'Confirm deletion'),
        message: message('AdminSettings.PurgeFullConfirm', 'Delete all cached map tiles? This cannot be undone.'),
        confirmText: message('Shell.Delete', 'Delete'),
        onConfirm: () => startCachePurge('/Admin/Settings/DeleteAllMapTileCache',
            message('AdminSettings.PurgeFullStartFailed', 'Could not start the full cache purge.'),
            message('AdminSettings.PurgeFullStarted', 'Full cache purge started.'))
    });
};

/**
 * Deletes Least Recently Used map tile cache (zoom levels >= 9) from file system and database.
 */
const deleteLruCache = () => {
    wayfarer.showConfirmationModal({
        title: message('AdminSettings.PurgeConfirmTitle', 'Confirm deletion'),
        message: message('AdminSettings.PurgeLruConfirm', 'Delete cached LRU tiles (zoom levels ≥ 9)? This cannot be undone.'),
        confirmText: message('Shell.Delete', 'Delete'),
        onConfirm: () => startCachePurge('/Admin/Settings/DeleteLruCache',
            message('AdminSettings.PurgeLruStartFailed', 'Could not start the LRU cache purge.'),
            message('AdminSettings.PurgeLruStarted', 'LRU cache purge started.'))
    });
};

/**
 * Deletes all MBTiles cache used for mobile app.
 */
const deleteMbtilesCache = () => {
    wayfarer.showConfirmationModal({
        title: message('AdminSettings.PurgeMbtilesConfirmTitle', 'Confirm MBTiles deletion'),
        message: message('AdminSettings.PurgeMbtilesConfirm', 'Delete all MBTiles files used for mobile app caching?'),
        confirmText: message('Shell.Delete', 'Delete'),
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
                        wayfarer.showAlert('danger', message('AdminSettings.PurgeMbtilesFailed', 'Failed to delete MBTiles cache.'));
                    }
                })
                .catch(error => {
                    console.error("error:", error);
                    wayfarer.showAlert('danger', message('AdminSettings.PurgeMbtilesFailed', 'Failed to delete MBTiles cache.'));
                });
        }
    });
};
