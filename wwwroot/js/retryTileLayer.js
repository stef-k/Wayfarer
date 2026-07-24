/**
 * Custom Leaflet TileLayer that uses fetch() instead of <img>.src for tile loading.
 * This enables HTTP status code inspection so we can retry on 503 (budget exhaustion)
 * while treating 404 as permanent failure.
 *
 * Concurrency control:
 * - Global pool limits concurrent tile fetches to prevent overwhelming the server's
 *   outbound budget and per-IP budget. Pool size is derived from the server's burst
 *   capacity (injected via wayfarerTileConfig.burstCapacity) — set to 60% of burst
 *   to leave headroom for other concurrent users.
 *
 * Retry strategy (two phases):
 * - Fast phase: up to 5 retries with exponential backoff (respects Retry-After header)
 * - Slow phase: if fast retries exhaust on 503 or network error, makes single-shot
 *   attempts at slowRetryDelayMs intervals (derived from retryAfterSeconds * 3) until
 *   the tile loads or is removed. Each slow attempt is one fetch — if it gets 503,
 *   it schedules the next slow poll directly (no fast-retry cycle replay).
 * - 404 and other HTTP errors are permanent failures (no retry)
 *
 * Design note: upstream HTTP 500/502/504 errors are treated as permanent failures
 * (not retried). The 503 retry strategy specifically targets outbound budget exhaustion
 * on our proxy. If upstream OSM is down, retrying would not help and would only pile
 * up stale retry timers. Users will see gray tiles until upstream recovers.
 */

// ---------- Server config (injected by _Layout.cshtml) ----------
const _config = window.wayfarerTileConfig || {};

// ---------- Global concurrency pool ----------
// Pool size derived from server's outbound burst capacity: 75% of burst leaves headroom
// for other concurrent users while still allowing a cold-cache load to progress quickly.
// Falls back to 9 if config is unavailable (e.g., inline scripts outside _Layout).
const _poolSize = Math.ceil((_config.burstCapacity || 12) * 0.75);
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

// ---------- Retry timing derived from server config ----------
// retryAfterSeconds is the Retry-After value the server sends on 503 (matches the budget
// replenishment cycle). Slow retry uses 3x that interval to give the per-IP sliding window
// time to decay between attempts. Falls back to 6s if config unavailable.
const _retryAfterSeconds = _config.retryAfterSeconds || 6;
const _defaultSlowRetryDelayMs = _retryAfterSeconds * 3 * 1000;

const RetryTileLayer = L.TileLayer.extend({
    options: {
        maxRetries: 5,
        retryDelayMs: 1000,
        slowRetryDelayMs: _defaultSlowRetryDelayMs,
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
     * Single-shot slow-phase fetch for a tile whose fast retries have been exhausted.
     * Acquires a concurrency slot, makes ONE fetch attempt, and either:
     * - Succeeds → loads the tile image
     * - Gets 503 → schedules another slow poll after slowRetryDelayMs
     * - Gets a permanent error (404, 500) → gives up
     * - Network error → schedules another slow poll
     * This avoids the ~30s lag of replaying a full fast-retry cycle on each slow poll.
     * Each slow attempt consumes only 1 per-IP budget hit instead of 6.
     * @param {string} url - The tile URL.
     * @param {HTMLImageElement} tile - The tile image element.
     * @param {Function} done - Leaflet callback to signal completion.
     * @param {AbortSignal} signal - Abort signal from the tile's AbortController.
     */
    _slowRetryOnce: function (url, tile, done, signal) {
        const layer = this;

        _acquireSlot(signal).then(function (acquired) {
            if (!acquired) return;
            if (signal.aborted) { _releaseSlot(); return; }

            fetch(url, { signal: signal }).then(function (response) {
                _releaseSlot();

                if (response.ok) {
                    return response.blob().then(function (blob) {
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

                // Still 503 — schedule next slow poll.
                if (response.status === 503) {
                    layer._scheduleSlowRetry(url, tile, done, signal);
                    return;
                }

                // Non-retryable (404, 400, 500, etc.) — permanent failure.
                done(new Error('Tile fetch failed: ' + response.status), tile);
            }).catch(function (err) {
                _releaseSlot();
                if (err.name === 'AbortError') return;
                // Network error — schedule next slow poll.
                layer._scheduleSlowRetry(url, tile, done, signal);
            });
        });
    },

    /**
     * Schedules a slow-phase retry after slowRetryDelayMs (derived from retryAfterSeconds * 3).
     * Uses single-shot fetch (_slowRetryOnce) — one request per poll, not a full fast-retry cycle.
     * Jitter ±25% prevents synchronized bursts from multiple tiles polling simultaneously.
     * @param {string} url - The tile URL.
     * @param {HTMLImageElement} tile - The tile image element.
     * @param {Function} done - Leaflet callback to signal completion.
     * @param {AbortSignal} signal - Abort signal from the tile's AbortController.
     */
    _scheduleSlowRetry: function (url, tile, done, signal) {
        const layer = this;
        var delayMs = this.options.slowRetryDelayMs;
        // Jitter ±25% to spread slow retries across time and avoid synchronized bursts.
        delayMs *= (0.75 + Math.random() * 0.5);
        setTimeout(function () {
            if (!signal.aborted) {
                layer._slowRetryOnce(url, tile, done, signal);
            }
        }, delayMs);
    },

    /**
     * Fetches a tile via fetch(), retries on 503 or network error with backoff.
     * Two retry phases:
     * - Fast: attempts 0..maxRetries with exponential backoff (seconds)
     * - Slow: after fast retries exhaust on 503/network error, single-shot polls
     *   every ~18s (derived from retryAfterSeconds * 3) until the tile loads or
     *   is removed — each poll is one fetch, not a full fast-retry cycle
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
                if (response.status === 503) {
                    // Fast phase: exponential backoff with Retry-After support.
                    if (attempt < maxRetries) {
                        const retryAfter = response.headers.get('Retry-After');
                        const parsed = retryAfter ? parseInt(retryAfter, 10) : NaN;
                        let delayMs = !isNaN(parsed) && parsed > 0
                            ? parsed * 1000
                            : baseDelay * Math.pow(2, attempt);
                        delayMs = Math.max(delayMs, baseDelay); // floor: never below base delay
                        delayMs = Math.min(delayMs, 10000);     // cap: never above 10s
                        delayMs *= (0.75 + Math.random() * 0.5); // jitter ±25%

                        setTimeout(function () {
                            if (!signal.aborted) {
                                layer._fetchWithRetry(url, tile, done, attempt + 1, signal);
                            }
                        }, delayMs);
                        return;
                    }

                    // Slow phase: fast retries exhausted but 503 is transient (budget will
                    // recover). Switch to single-shot polling.
                    layer._scheduleSlowRetry(url, tile, done, signal);
                    return;
                }

                // Non-retryable (404, 400, 500, etc.) — permanent failure.
                done(new Error('Tile fetch failed: ' + response.status), tile);
            }).catch(function (err) {
                _releaseSlot();

                // Tile was removed (panned/zoomed away) — silently stop.
                if (err.name === 'AbortError') return;

                // Network error (or body-read failure mid-transfer) — retry if attempts remain.
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

                // Slow phase for network errors: could be transient (e.g., brief connectivity
                // loss). Switch to single-shot polling.
                layer._scheduleSlowRetry(url, tile, done, signal);
            });
        });
    }
});

/**
 * Creates a tile layer with retry support using the app's tile proxy config.
 * Reads URL and attribution from window.wayfarerTileConfig (injected by _Layout.cshtml).
 * @param {Object} [opts] - Additional L.TileLayer options to merge. Supports standard Leaflet
 *   options (e.g., {zoomAnimation: true}) plus retry tuning: maxRetries (default 5),
 *   retryDelayMs (default 1000), slowRetryDelayMs (derived from retryAfterSeconds * 3).
 * @returns {L.TileLayer} The tile layer instance (call .addTo(map) on the result).
 */
export const createTileLayer = (opts) => {
    const url = _config.tilesUrl || (window.location.origin + '/Public/tiles/{z}/{x}/{y}.png');
    // Preserve an explicitly empty server result; only an absent legacy config uses OSM.
    const attribution = _config.attribution ??
        '\u00a9 <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors';
    return new RetryTileLayer(url, Object.assign({
        maxZoom: 19,
        attribution: attribution,
    }, opts || {}));
};
