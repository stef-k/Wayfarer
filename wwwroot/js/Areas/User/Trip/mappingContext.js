// mappingContext.js
// A shared global context object to coordinate map, search, and sidebar behaviors
// Usage: import { setMappingContext, getMappingContext, clearMappingContext } from './mappingContext.js';

let mappingContext = {
    type: null,       // 'place' | 'region' | 'segment'
    id: null,         // object UUID
    action: null,     // 'set-location' | 'set-center' | 'trace-route'
    meta: {}          // optional UI data like { name: 'Boracay Beach', color: '#FF0' }
};

/**
 * Gets the current mapping context object.
 * @returns {object} The mapping context.
 */
export const getMappingContext = () => mappingContext;

/**
 * Updates the mapping context and dispatches a change event.
 * Also updates the banner UI visibility.
 * @param {object} newContext - Partial context to merge.
 */
export const setMappingContext = (newContext) => {
    mappingContext = {
        ...mappingContext,
        ...newContext
    };

    const event = new CustomEvent('mapping-context-changed', { detail: mappingContext });
    document.dispatchEvent(event);

    // Show banner visually
    const banner = document.getElementById('mapping-context-banner');
    if (banner) banner.classList.add('active');
};

/**
 * Clears the current mapping context and dispatches a cleared event.
 * Also hides the banner.
 */
export const clearMappingContext = () => {
    mappingContext = {
        type: null,
        id: null,
        action: null,
        meta: {}
    };

    const event = new CustomEvent('mapping-context-cleared');
    document.dispatchEvent(event);

    // Hide banner visually
    const banner = document.getElementById('mapping-context-banner');
    if (banner) banner.classList.remove('active');
};
