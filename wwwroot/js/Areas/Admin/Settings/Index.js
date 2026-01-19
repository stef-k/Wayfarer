document.addEventListener('DOMContentLoaded', () => {
    document.getElementById('clearAllCache')?.addEventListener('click', (e) => {
        e.preventDefault();
        deleteAllMapTileCache();
    });

    document.getElementById('clearLruCache')?.addEventListener('click', (e) => {
        e.preventDefault();
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
});

/**
 * Gets the anti-forgery token from the page for AJAX POST requests.
 * @returns {string} The anti-forgery token value or empty string if not found.
 */
const getAntiForgeryToken = () => {
    return document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
};

/**
 * Deletes all map tile cache from zoom level 1 to max from file system and database.
 */
const deleteAllMapTileCache = () => {
    wayfarer.showConfirmationModal({
        title: "Confirm Deletion",
        message: "Are you sure you want to delete all map tile cache? This action cannot be undone.",
        confirmText: "Delete",
        onConfirm: () => {
            fetch("/Admin/Settings/DeleteAllMapTileCache", {
                method: "POST",
                headers: {
                    "Content-Type": "application/json",
                    "RequestVerificationToken": getAntiForgeryToken()
                }
            })
                .then(response => response.json())
                .then(data => {
                    if (data.success) {
                        document.getElementById('TotalCacheFiles').textContent = data.cacheStatus.totalCacheFiles;
                        document.getElementById('LruTotalFiles').textContent = data.cacheStatus.lruTotalFiles;
                        document.getElementById('TotalCacheSize').textContent = data.cacheStatus.totalCacheSize + ' MB';
                        document.getElementById('TotalCacheSizeGB').textContent = data.cacheStatus.totalCacheSizeGB + ' GB';
                        document.getElementById('TotalLru').textContent = data.cacheStatus.totalLru + ' MB';
                        document.getElementById('TotalLruGB').textContent = data.cacheStatus.totalLruGB + ' GB';
                        wayfarer.showAlert("success", data.message);
                    } else {
                        wayfarer.showAlert("danger", "Failed to delete map tile cache.");
                    }
                })
                .catch(error => {
                    console.error("danger:", error);
                    wayfarer.showAlert("danger", `Failed to delete map tile cache. ${error}`);
                });
        }
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
        onConfirm: () => {
            fetch("/Admin/Settings/DeleteLruCache", {
                method: "POST",
                headers: {
                    "Content-Type": "application/json",
                    "RequestVerificationToken": getAntiForgeryToken()
                }
            })
                .then(response => response.json())
                .then(data => {
                    if (data.success) {
                        document.getElementById('TotalCacheFiles').textContent = data.cacheStatus.totalCacheFiles;
                        document.getElementById('LruTotalFiles').textContent = data.cacheStatus.lruTotalFiles;
                        document.getElementById('TotalCacheSize').textContent = data.cacheStatus.totalCacheSize + ' MB';
                        document.getElementById('TotalCacheSizeGB').textContent = data.cacheStatus.totalCacheSizeGB + ' GB';
                        document.getElementById('TotalLru').textContent = data.cacheStatus.totalLru + ' MB';
                        document.getElementById('TotalLruGB').textContent = data.cacheStatus.totalLruGB + ' GB';
                        wayfarer.showAlert("success", data.message);
                    } else {
                        wayfarer.showAlert("danger", "Failed to delete Least Recently Used map tile cache.");
                    }
                })
                .catch(error => {
                    console.error("danger:", error);
                    wayfarer.showAlert("danger", `Failed to delete Least Recently Used map tile cache. ${error}`);
                });
        }
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
