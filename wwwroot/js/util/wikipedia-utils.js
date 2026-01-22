/**
 * Wikipedia Utility Module
 *
 * Centralized utilities for Wikipedia geosearch, text search, and popover functionality.
 * Provides dual search strategy (geo + text) for better results and eliminates code duplication.
 *
 * @module wikipedia-utils
 */

// ─────────────────────────────────────────────────────────────────────────────
// Constants
// ─────────────────────────────────────────────────────────────────────────────

const WIKI_API_BASE = 'https://en.wikipedia.org/w/api.php';
const WIKI_SUMMARY_BASE = 'https://en.wikipedia.org/api/rest_v1/page/summary';
const DEFAULT_RADIUS = 100; // meters
const DEFAULT_LIMIT = 5;
const POPOVER_MAX_WIDTH = 250;

// ─────────────────────────────────────────────────────────────────────────────
// Coordinate Extraction
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Extracts latitude and longitude from various input formats.
 * Handles both nested (location.coordinates) and flat (lat, lon) formats.
 *
 * @param {Object|number} latOrLocation - Location object with coordinates property, or latitude number
 * @param {number} [lon] - Longitude number (required if first param is latitude)
 * @returns {{ lat: number|null, lon: number|null }} Extracted coordinates or nulls if invalid
 *
 * @example
 * // Nested format (most location objects)
 * extractCoordinates({ coordinates: { latitude: 51.5, longitude: -0.12 } })
 * // => { lat: 51.5, lon: -0.12 }
 *
 * @example
 * // Flat format (Trip/Viewer.js style)
 * extractCoordinates(51.5, -0.12)
 * // => { lat: 51.5, lon: -0.12 }
 *
 * @example
 * // With latitude/longitude properties directly on object
 * extractCoordinates({ latitude: 51.5, longitude: -0.12 })
 * // => { lat: 51.5, lon: -0.12 }
 */
export const extractCoordinates = (latOrLocation, lon) => {
    // Flat format: extractCoordinates(lat, lon)
    if (typeof latOrLocation === 'number' && typeof lon === 'number') {
        return {
            lat: Number.isFinite(latOrLocation) ? latOrLocation : null,
            lon: Number.isFinite(lon) ? lon : null
        };
    }

    // Object format
    if (latOrLocation && typeof latOrLocation === 'object') {
        // Nested: { coordinates: { latitude, longitude } }
        if (latOrLocation.coordinates) {
            const { latitude, longitude } = latOrLocation.coordinates;
            return {
                lat: Number.isFinite(+latitude) ? +latitude : null,
                lon: Number.isFinite(+longitude) ? +longitude : null
            };
        }

        // Direct: { latitude, longitude }
        if ('latitude' in latOrLocation || 'longitude' in latOrLocation) {
            const { latitude, longitude } = latOrLocation;
            return {
                lat: Number.isFinite(+latitude) ? +latitude : null,
                lon: Number.isFinite(+longitude) ? +longitude : null
            };
        }

        // Direct: { lat, lon }
        if ('lat' in latOrLocation || 'lon' in latOrLocation) {
            const { lat, lon: longitude } = latOrLocation;
            return {
                lat: Number.isFinite(+lat) ? +lat : null,
                lon: Number.isFinite(+longitude) ? +longitude : null
            };
        }
    }

    return { lat: null, lon: null };
};

// ─────────────────────────────────────────────────────────────────────────────
// Wikipedia API Functions
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Performs a Wikipedia geosearch to find articles near given coordinates.
 *
 * @param {number} lat - Latitude
 * @param {number} lon - Longitude
 * @param {Object} [options] - Search options
 * @param {number} [options.radius=100] - Search radius in meters (max 10000)
 * @param {number} [options.limit=5] - Maximum number of results
 * @returns {Promise<Array<{ pageid: number, title: string, dist: number }>>} Array of nearby articles
 */
export const geoSearch = async (lat, lon, options = {}) => {
    const { radius = DEFAULT_RADIUS, limit = DEFAULT_LIMIT } = options;

    if (!Number.isFinite(lat) || !Number.isFinite(lon)) {
        return [];
    }

    const url = new URL(WIKI_API_BASE);
    url.search = new URLSearchParams({
        action: 'query',
        list: 'geosearch',
        gscoord: `${lat}|${lon}`,
        gsradius: Math.min(radius, 10000), // Wikipedia max is 10km
        gslimit: limit,
        format: 'json',
        origin: '*'
    }).toString();

    try {
        const response = await fetch(url);
        if (!response.ok) {
            throw new Error(`GeoSearch HTTP ${response.status}`);
        }
        const json = await response.json();
        return json.query?.geosearch || [];
    } catch (err) {
        console.debug('Wikipedia geoSearch error:', err);
        return [];
    }
};

/**
 * Performs a Wikipedia text search to find articles matching a query.
 *
 * @param {string} query - Search query (e.g., place name)
 * @param {Object} [options] - Search options
 * @param {number} [options.limit=5] - Maximum number of results
 * @returns {Promise<Array<{ pageid: number, title: string }>>} Array of matching articles
 */
export const textSearch = async (query, options = {}) => {
    const { limit = DEFAULT_LIMIT } = options;

    if (!query || typeof query !== 'string' || !query.trim()) {
        return [];
    }

    const url = new URL(WIKI_API_BASE);
    url.search = new URLSearchParams({
        action: 'query',
        list: 'search',
        srsearch: query.trim(),
        srlimit: limit,
        format: 'json',
        origin: '*'
    }).toString();

    try {
        const response = await fetch(url);
        if (!response.ok) {
            throw new Error(`TextSearch HTTP ${response.status}`);
        }
        const json = await response.json();
        return json.query?.search || [];
    } catch (err) {
        console.debug('Wikipedia textSearch error:', err);
        return [];
    }
};

/**
 * Fetches the summary for a Wikipedia article by title.
 *
 * @param {string} title - Wikipedia article title
 * @returns {Promise<Object|null>} Article summary object or null on error
 */
export const fetchSummary = async (title) => {
    if (!title) return null;

    try {
        const encodedTitle = encodeURIComponent(title);
        const response = await fetch(`${WIKI_SUMMARY_BASE}/${encodedTitle}`);
        if (!response.ok) {
            throw new Error(`Summary HTTP ${response.status}`);
        }
        return await response.json();
    } catch (err) {
        console.debug('Wikipedia fetchSummary error:', err);
        return null;
    }
};

/**
 * Combined search: runs geosearch AND text search in parallel,
 * merges results, and deduplicates by page ID.
 *
 * This dual strategy ensures robust results even when one method returns nothing.
 *
 * @param {Object} params - Search parameters
 * @param {number} [params.lat] - Latitude for geosearch
 * @param {number} [params.lon] - Longitude for geosearch
 * @param {string} [params.query] - Text query for search
 * @param {Object} [params.options] - Additional options
 * @param {number} [params.options.radius=100] - Geosearch radius in meters
 * @param {number} [params.options.limit=5] - Maximum results to return
 * @returns {Promise<Array>} Combined and deduplicated search results
 */
export const searchWikipedia = async ({ lat, lon, query, options = {} }) => {
    const { radius = DEFAULT_RADIUS, limit = DEFAULT_LIMIT } = options;
    const searches = [];

    // Run both searches in parallel if applicable
    if (Number.isFinite(lat) && Number.isFinite(lon)) {
        searches.push(geoSearch(lat, lon, { radius, limit }));
    }
    if (query && typeof query === 'string' && query.trim()) {
        searches.push(textSearch(query, { limit }));
    }

    if (searches.length === 0) {
        return [];
    }

    const results = await Promise.all(searches);

    // Flatten and deduplicate by pageid
    const seen = new Set();
    const combined = results.flat().filter(item => {
        const id = item.pageid;
        if (!id || seen.has(id)) return false;
        seen.add(id);
        return true;
    });

    return combined.slice(0, limit);
};

// ─────────────────────────────────────────────────────────────────────────────
// HTML Generation
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Generates an HTML Wikipedia link button with data attributes for popover initialization.
 *
 * Supports multiple calling conventions:
 * - `generateWikipediaLinkHtml(location)` - nested coordinates
 * - `generateWikipediaLinkHtml(location, options)` - nested coordinates with options
 * - `generateWikipediaLinkHtml(lat, lon)` - flat coordinates
 * - `generateWikipediaLinkHtml(lat, lon, options)` - flat coordinates with options
 *
 * @param {Object|number} latOrLocation - Location object or latitude
 * @param {number|Object} [lonOrOptions] - Longitude or options object
 * @param {Object} [options] - Options when using flat coordinates
 * @param {string} [options.query] - Text query for dual search (e.g., place name)
 * @param {string} [options.cssClass] - Additional CSS classes
 * @returns {string} HTML string for the Wikipedia link button
 *
 * @example
 * // Nested coordinates (most common)
 * generateWikipediaLinkHtml(location)
 *
 * @example
 * // With query for dual search
 * generateWikipediaLinkHtml(location, { query: location.placeName })
 *
 * @example
 * // Flat coordinates (Trip/Viewer.js style)
 * generateWikipediaLinkHtml(51.5074, -0.1278)
 */
export const generateWikipediaLinkHtml = (latOrLocation, lonOrOptions, options) => {
    let lat, lon, opts;

    // Determine calling convention
    if (typeof latOrLocation === 'number' && typeof lonOrOptions === 'number') {
        // Flat: generateWikipediaLinkHtml(lat, lon, options?)
        lat = latOrLocation;
        lon = lonOrOptions;
        opts = options || {};
    } else if (typeof latOrLocation === 'object') {
        // Object: generateWikipediaLinkHtml(location, options?)
        const coords = extractCoordinates(latOrLocation);
        lat = coords.lat;
        lon = coords.lon;
        opts = (typeof lonOrOptions === 'object' ? lonOrOptions : {}) || {};
    } else {
        // Invalid input
        return '';
    }

    // Validate coordinates
    if (!Number.isFinite(lat) || !Number.isFinite(lon)) {
        return '';
    }

    const { query = '', cssClass = '' } = opts;
    const additionalClass = cssClass ? ` ${cssClass}` : '';
    const queryAttr = query ? ` data-query="${escapeHtml(query)}"` : '';

    return `<a href="#" class="ms-2 wikipedia-link btn btn-outline-primary btn-sm${additionalClass}" data-lat="${lat}" data-lon="${lon}"${queryAttr} title="Search Wikipedia nearby"><i class="bi bi-wikipedia"></i> Wiki</a>`;
};

/**
 * Escapes HTML special characters to prevent XSS.
 *
 * @param {string} text - Text to escape
 * @returns {string} Escaped text
 */
const escapeHtml = (text) => {
    if (!text) return '';
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
};

// ─────────────────────────────────────────────────────────────────────────────
// Popover Management
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Generates the loading content HTML for popovers.
 *
 * @returns {string} Loading message HTML
 */
const getLoadingHtml = () => 'Loading\u2026';

/**
 * Generates the "no results" content HTML for popovers.
 *
 * @returns {string} No results message HTML
 */
const getNoResultsHtml = () =>
    `<div style="max-width:${POPOVER_MAX_WIDTH}px"><em>No nearby Wikipedia article found.</em></div>`;

/**
 * Generates the error content HTML for popovers.
 *
 * @returns {string} Error message HTML
 */
const getErrorHtml = () =>
    `<div style="max-width:${POPOVER_MAX_WIDTH}px"><em>Could not load article.</em></div>`;

/**
 * Generates the article summary content HTML for popovers.
 *
 * @param {Object} summary - Wikipedia summary object
 * @returns {string} Article summary HTML
 */
const getSummaryHtml = (summary) => {
    const url = summary.content_urls?.desktop?.page || `https://en.wikipedia.org/wiki/${encodeURIComponent(summary.title)}`;
    return `<div style="max-width:${POPOVER_MAX_WIDTH}px"><strong>${escapeHtml(summary.title)}</strong><p>${summary.extract || ''}</p><a href="${url}" target="_blank">Read more \u00BB</a></div>`;
};

/**
 * Initializes Wikipedia popovers for all .wikipedia-link elements within a container.
 * Uses Tippy.js for tooltips with lazy-loading article summaries.
 *
 * @param {HTMLElement|string} containerEl - Container element or selector
 * @param {Object} [options] - Popover options
 * @param {string} [options.placement='right'] - Tippy placement ('right', 'top', 'bottom', 'left')
 * @param {number} [options.zIndex=2000] - Popover z-index (must exceed Bootstrap modal's 1050)
 * @returns {void}
 *
 * @example
 * // Basic usage (right placement for modals)
 * initWikipediaPopovers(modalEl);
 *
 * @example
 * // Custom placement (top for backfill modal)
 * initWikipediaPopovers(modalEl, { placement: 'top' });
 */
export const initWikipediaPopovers = (containerEl, options = {}) => {
    // Ensure tippy is available
    if (typeof tippy === 'undefined') {
        console.warn('Tippy.js not loaded - Wikipedia popovers disabled');
        return;
    }

    // Resolve container element
    const container = typeof containerEl === 'string'
        ? document.querySelector(containerEl)
        : containerEl;

    if (!container) {
        console.debug('Wikipedia popover container not found');
        return;
    }

    const { placement = 'right', zIndex = 2000 } = options;

    container.querySelectorAll('.wikipedia-link').forEach(el => {
        // Prevent double initialization
        if (el._tippy) return;

        tippy(el, {
            appendTo: () => document.body,
            popperOptions: {
                strategy: 'fixed',
                modifiers: [{
                    name: 'zIndex',
                    options: { value: zIndex }
                }]
            },
            interactiveBorder: 20,
            content: getLoadingHtml(),
            allowHTML: true,
            interactive: true,
            hideOnClick: false,
            placement,
            onShow: async instance => {
                // Only load once
                if (instance._loaded) return;
                instance._loaded = true;

                const lat = parseFloat(el.dataset.lat);
                const lon = parseFloat(el.dataset.lon);
                const query = el.dataset.query || '';

                try {
                    // Use dual search strategy
                    const results = await searchWikipedia({
                        lat: Number.isFinite(lat) ? lat : undefined,
                        lon: Number.isFinite(lon) ? lon : undefined,
                        query: query || undefined,
                        options: { radius: DEFAULT_RADIUS, limit: DEFAULT_LIMIT }
                    });

                    if (!results.length) {
                        instance.setContent(getNoResultsHtml());
                        return;
                    }

                    // Fetch summary of the top hit
                    const summary = await fetchSummary(results[0].title);
                    if (!summary) {
                        instance.setContent(getErrorHtml());
                        return;
                    }

                    instance.setContent(getSummaryHtml(summary));
                } catch (err) {
                    console.debug('Wikipedia popover error:', err);
                    instance.setContent(getErrorHtml());
                }
            }
        });
    });
};

/**
 * Destroys Wikipedia popovers for all .wikipedia-link elements within a container.
 * Call this before removing elements from the DOM to prevent memory leaks.
 *
 * @param {HTMLElement|string} containerEl - Container element or selector
 * @returns {void}
 */
export const destroyWikipediaPopovers = (containerEl) => {
    const container = typeof containerEl === 'string'
        ? document.querySelector(containerEl)
        : containerEl;

    if (!container) return;

    container.querySelectorAll('.wikipedia-link').forEach(el => {
        if (el._tippy) {
            el._tippy.destroy();
        }
    });
};
