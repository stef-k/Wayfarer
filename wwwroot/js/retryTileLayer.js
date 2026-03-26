/**
 * Custom Leaflet TileLayer that uses fetch() instead of <img>.src for tile loading.
 * This enables HTTP status code inspection so we can retry on 503 (budget exhaustion)
 * while treating 404 as permanent failure.
 *
 * Concurrency control:
 * - Global pool limits concurrent tile fetches (default 6) to prevent overwhelming
 *   the server's outbound budget (10 burst, 2/sec) and per-IP budget (default 120/min).
 *   Without this, a cold-cache load at zoom 17 (~35 tiles) sends all requests
 *   simultaneously, exhausting both budgets and causing cascading 503 failures where
 *   retries also get rejected (the per-IP counter increments on every request, even
 *   rejected ones, so the count quickly snowballs past the limit).
 *
 * Retry strategy:
 * - Only retries on HTTP 503 or network errors
 * - Reads Retry-After header from server (falls back to exponential backoff)
 * - Max 5 retries per tile, delay capped at 10 seconds
 * - 404 and other status codes are NOT retried
 *
 * Design note: upstream HTTP 500/502/504 errors are treated as permanent failures
 * (not retried). The 503 retry strategy specifically targets outbound budget exhaustion
 * on our proxy. If upstream OSM is down, retrying would not help and would only pile
 * up stale retry timers. Users will see gray tiles until upstream recovers.
 */

// ---------- Global concurrency pool ----------
// Limits concurrent tile fetches to prevent overwhelming the server's per-IP outbound
// budget (default 120/min) and global token budget (10 burst, 2/sec). Tiles beyond the
// limit queue client-side and proceed as slots free up, producing the progressive
// "stream-in" effect on cold-cache loads instead of a wall of 503s.
const _poolSize = 6;
let _inFlight = 0;
const _waiting = [];

/**
 * Acquires a concurrency slot. Resolves with true when a slot is available,
 * or false if the signal was aborted while queued (tile panned/zoomed away).
 * @param {AbortSignal} signal - Abort signal from the tile's AbortController.
 * @returns {Promise<boolean>} True if a slot was acquired (caller must call _releaseSlot).
 */
const _acquireSlot = (signal) => {
    if (signal.aborted) return Promise.resolve(false);
    if (_inFlight < _poolSize) {
        _inFlight++;
        return Promise.resolve(true);
    }
    return new Promise((resolve) => {
        const entry = { resolve };
        _waiting.push(entry);
        // When the tile is removed (panned/zoomed away), dequeue so the slot isn't
        // wasted on a fetch that will be immediately aborted.
        signal.addEventListener('abort', () => {
            const i = _waiting.indexOf(entry);
            if (i !== -1) {
                _waiting.splice(i, 1);
                resolve(false);
            }
            // If entry was already shifted by _releaseSlot, the slot was transferred
            // to us — caller checks signal.aborted and releases.
        }, { once: true });
    });
};

/**
 * Releases a concurrency slot, allowing the next queued fetch to proceed.
 * Must be called exactly once for every _acquireSlot that resolved with true.
 */
const _releaseSlot = () => {
    if (_waiting.length > 0) {
        // Transfer slot directly to next waiter (don't decrement _inFlight).
        _waiting.shift().resolve(true);
    } else {
        _inFlight--;
    }
};

const RetryTileLayer = L.TileLayer.extend({
    options: {
        maxRetries: 5,
        retryDelayMs: 1000,
    },

    /**
     * Override createTile to use fetch() with AbortController for HTTP status code access.
     * An AbortController is stored on the tile element so _removeTile can cancel in-flight
     * fetches and pending retry timers when tiles are panned/zoomed out of view.
     * @param {Object} coords - Tile coordinates {x, y, z}.
     * @param {Function} done - Callback to signal tile load complete.
     * @returns {HTMLImageElement} The tile image element.
     */
    createTile: function (coords, done) {
        const tile = document.createElement('img');
        tile.alt = '';
        tile.setAttribute('role', 'presentation');

        if (this.options.crossOrigin || this.options.crossOrigin === '') {
            tile.crossOrigin = this.options.crossOrigin === true
                ? '' : this.options.crossOrigin;
        }
        if (typeof this.options.referrerPolicy === 'string') {
            tile.referrerPolicy = this.options.referrerPolicy;
        }

        // AbortController lets _removeTile cancel in-flight fetch and pending retries.
        const controller = new AbortController();
        tile._abortController = controller;

        const url = this.getTileUrl(coords);
        this._fetchWithRetry(url, tile, done, 0, controller.signal);
        return tile;
    },

    /**
     * Override _removeTile to abort in-flight fetches and revoke blob URLs.
     * Leaflet calls this when tiles are panned/zoomed out of view. Without this,
     * fetch() continues running and retry timers keep firing for removed tiles.
     * @param {string} key - Leaflet internal tile key.
     */
    _removeTile: function (key) {
        const tile = this._tiles[key];
        if (tile && tile.el) {
            // Abort any in-flight fetch or pending retry for this tile.
            if (tile.el._abortController) {
                tile.el._abortController.abort();
                tile.el._abortController = null;
            }
            // Revoke blob URL to prevent memory leaks. Leaflet replaces onload/onerror
            // with falseFn before removal, so our revocation callbacks would never fire.
            if (tile.el.src && tile.el.src.startsWith('blob:')) {
                URL.revokeObjectURL(tile.el.src);
            }
        }
        L.TileLayer.prototype._removeTile.call(this, key);
    },

    /**
     * Fetches a tile via fetch(), retries on 503 or network error with backoff.
     * Acquires a concurrency slot before each fetch attempt to prevent overwhelming
     * the server's budget. Respects AbortSignal so removed tiles stop immediately.
     * @param {string} url - The tile URL.
     * @param {HTMLImageElement} tile - The tile image element.
     * @param {Function} done - Leaflet callback to signal completion.
     * @param {number} attempt - Current retry attempt (0-based).
     * @param {AbortSignal} signal - Abort signal from the tile's AbortController.
     */
    _fetchWithRetry: function (url, tile, done, attempt, signal) {
        const layer = this;
        const maxRetries = this.options.maxRetries;
        const baseDelay = this.options.retryDelayMs;

        _acquireSlot(signal).then(function (acquired) {
            // No slot acquired — tile was removed while queued, nothing to do.
            // done() is intentionally not called: Leaflet's _removeTile already replaced
            // it with falseFn, so calling it would be a no-op at best.
            if (!acquired) return;
            // Slot acquired but tile removed in the meantime — release immediately.
            // Same reasoning: _removeTile already handled Leaflet cleanup.
            if (signal.aborted) { _releaseSlot(); return; }

            fetch(url, { signal: signal }).then(function (response) {
                _releaseSlot();

                if (response.ok) {
                    return response.blob().then(function (blob) {
                        // Guard: if the tile was removed (panned/zoomed away) while the blob
                        // was being read, skip assigning the blob URL. Without this check,
                        // Leaflet's _removeTile would have already replaced onload/onerror
                        // with falseFn, so the revokeObjectURL callback would never fire,
                        // leaking the blob URL.
                        if (signal.aborted) return;
                        tile.onload = function () {
                            URL.revokeObjectURL(tile.src);
                            done(null, tile);
                        };
                        tile.onerror = function (e) {
                            URL.revokeObjectURL(tile.src);
                            done(e, tile);
                        };
                        tile.src = URL.createObjectURL(blob);
                    });
                }

                // 503 = budget exhausted, transient — retry with Retry-After or backoff.
                // Jitter (±25%) prevents thundering-herd retries when many tiles 503 simultaneously.
                if (response.status === 503 && attempt < maxRetries) {
                    const retryAfter = response.headers.get('Retry-After');
                    const parsed = retryAfter ? parseInt(retryAfter, 10) : NaN;
                    let delayMs = !isNaN(parsed) && parsed > 0
                        ? parsed * 1000
                        : baseDelay * Math.pow(2, attempt);
                    delayMs = Math.max(delayMs, baseDelay); // floor: never below base delay
                    delayMs = Math.min(delayMs, 10000);     // cap: never above 10s
                    delayMs *= (0.75 + Math.random() * 0.5); // jitter ±25%

                    setTimeout(function () {
                        // Check if tile was removed while waiting — abort signal is set.
                        if (!signal.aborted) {
                            layer._fetchWithRetry(url, tile, done, attempt + 1, signal);
                        }
                    }, delayMs);
                    return;
                }

                // Non-retryable (404, 400, 500, etc.)
                done(new Error('Tile fetch failed: ' + response.status), tile);
            }).catch(function (err) {
                _releaseSlot();

                // Tile was removed (panned/zoomed away) — silently stop.
                if (err.name === 'AbortError') return;

                // Network error (or body-read failure mid-transfer) — retry if attempts remain
                if (attempt < maxRetries) {
                    let delayMs = Math.min(baseDelay * Math.pow(2, attempt), 10000);
                    delayMs *= (0.75 + Math.random() * 0.5); // jitter ±25%
                    setTimeout(function () {
                        if (!signal.aborted) {
                            layer._fetchWithRetry(url, tile, done, attempt + 1, signal);
                        }
                    }, delayMs);
                    return;
                }
                done(err, tile);
            });
        });
    }
});

/**
 * Creates a tile layer with retry support using the app's tile proxy config.
 * Reads URL and attribution from window.wayfarerTileConfig (injected by _Layout.cshtml).
 * @param {Object} [opts] - Additional L.TileLayer options to merge. Supports standard Leaflet
 *   options (e.g., {zoomAnimation: true}) plus retry tuning: maxRetries (default 5),
 *   retryDelayMs (default 1000).
 * @returns {L.TileLayer} The tile layer instance (call .addTo(map) on the result).
 */
export const createTileLayer = (opts) => {
    const config = window.wayfarerTileConfig || {};
    const url = config.tilesUrl || (window.location.origin + '/Public/tiles/{z}/{x}/{y}.png');
    const attribution = config.attribution || '\u00a9 OpenStreetMap contributors';
    return new RetryTileLayer(url, Object.assign({
        maxZoom: 19,
        attribution: attribution,
    }, opts || {}));
};
