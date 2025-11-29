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
});

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
                headers: { "Content-Type": "application/json" }
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
                headers: { "Content-Type": "application/json" }
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
                headers: { "Content-Type": "application/json" }
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
