document.addEventListener('DOMContentLoaded', () => {
    document.getElementById('clearAllCache').addEventListener('click', (e) => {
        e.preventDefault();
        deleteAllMapTileCache();
    });
    
    document.getElementById('clearLruCache').addEventListener('click', (e) => {
        e.preventDefault();
        deleteLruCache();
    });
});

/**
 * Deletes all map tile cache from zoom level 1 to max from file system and database.
 */
const deleteAllMapTileCache = () => {
    showConfirmationModal({
        title: "Confirm Deletion",
        message: "Are you sure you want to delete all map tile cache? This action cannot be undone.",
        confirmText: "Delete",
        onConfirm: () => {
            fetch("/Admin/Settings/DeleteAllMapTileCache", {
                method: "POST",
                headers: {"Content-Type": "application/json"},
                // body: JSON.stringify()
            })
                .then(response => response.json())
                .then(data => {
                    if (data.success) {
                        document.getElementById('TotalCacheFiles').textContent = data.cacheStatus.totalCacheFiles;
                        document.getElementById('LruTotalFiles').textContent = data.cacheStatus.truTotalFiles;
                        document.getElementById('TotalCacheSize').textContent = data.cacheStatus.totalCacheSize;
                        document.getElementById('TotalCacheSizeGB').textContent = data.cacheStatus.totalCacheSizeGB;
                        document.getElementById('TotalLru').textContent = data.cacheStatus.totalLru;
                        document.getElementById('TotalLruGB').textContent = data.cacheStatus.totalLruGB;
                        showAlert("success", data.message);
                    } else {
                        showAlert("danger", "Failed to delete map tile cache.");
                    }
                })
                .catch(error => {
                    console.error("danger:", error);
                    showAlert("danger", `Failed to delete map tile cache. ${error}`);
                });
        }
    });
}

/**
 * Deletes Least Recently Used map tile cache (zoom levels >= 9) from file system and database.
 */
const deleteLruCache = () => {
    showConfirmationModal({
        title: "Confirm Deletion",
        message: "Are you sure you want to delete the Least Recently Used map tile cache (zoom levels >= 9)? This action cannot be undone.",
        confirmText: "Delete",
        onConfirm: () => {
            fetch("/Admin/Settings/DeleteLruCache", {
                method: "POST",
                headers: {"Content-Type": "application/json"},
                // body: JSON.stringify()
            })
                .then(response => response.json())
                .then(data => {
                    if (data.success) {
                        document.getElementById('TotalCacheFiles').textContent = data.cacheStatus.totalCacheFiles;
                        document.getElementById('LruTotalFiles').textContent = data.cacheStatus.truTotalFiles;
                        document.getElementById('TotalCacheSize').textContent = data.cacheStatus.totalCacheSize;
                        document.getElementById('TotalCacheSizeGB').textContent = data.cacheStatus.totalCacheSizeGB;
                        document.getElementById('TotalLru').textContent = data.cacheStatus.totalLru;
                        document.getElementById('TotalLruGB').textContent = data.cacheStatus.totalLruGB;
                        showAlert("success", data.message);
                    } else {
                        showAlert("danger", "Failed to delete Least Recently Used map tile cache.");
                    }
                })
                .catch(error => {
                    console.error("danger:", error);
                    showAlert("danger", `Failed to delete Least Recently Used map tile cache. ${error}`);
                });
        }
    });
}