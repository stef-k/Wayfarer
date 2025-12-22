/**
 * tripPopupBuilder.js - Shared tooltip content builders for Trip views
 * Used by: Trip Edit, User Trip View, Public Trip View
 *
 * Provides consistent rich HTML tooltips for places, segments, and areas.
 */

/**
 * Maximum characters for notes preview before truncation
 */
const MAX_NOTES_LENGTH = 150;

/**
 * Truncates HTML notes to a reasonable preview length.
 * Handles content with images by indicating media presence and extracting
 * text that follows images.
 * @param {string} html - The HTML notes content
 * @returns {string} - Truncated plain text with ellipsis if needed, prefixed with media indicator
 */
const truncateNotes = (html) => {
    if (!html) return '';
    const div = document.createElement('div');
    div.innerHTML = html;

    // Check for images or other media elements
    const hasMedia = div.querySelector('img, video, audio, iframe') !== null;

    // Extract all text content (includes text after images)
    const text = div.textContent?.trim() || '';

    // Build preview with optional media indicator
    let preview = '';
    if (hasMedia && text.length === 0) {
        // Only media, no text
        preview = '[Contains media]';
    } else if (hasMedia && text.length > 0) {
        // Media + text: show indicator and text preview
        const availableLength = MAX_NOTES_LENGTH - 10; // Reserve space for indicator
        if (text.length <= availableLength) {
            preview = '[Media] ' + text;
        } else {
            preview = '[Media] ' + text.substring(0, availableLength).trim() + '…';
        }
    } else if (text.length > 0) {
        // Text only
        if (text.length <= MAX_NOTES_LENGTH) {
            preview = text;
        } else {
            preview = text.substring(0, MAX_NOTES_LENGTH).trim() + '…';
        }
    }

    return preview;
};

/**
 * Footer hint for tooltips prompting users to click for more details
 */
const TOOLTIP_FOOTER = `<div class="popup-footer"><i class="bi bi-hand-index"></i> Click for details</div>`;

/**
 * Builds tooltip HTML content for a Place marker
 * @param {Object} place - Place data object
 * @param {string} place.name - Place name
 * @param {number} place.lat - Latitude
 * @param {number} place.lon - Longitude
 * @param {string} [place.address] - Optional address
 * @param {string} [place.notes] - Optional HTML notes
 * @param {string} [place.regionName] - Optional region name
 * @returns {string} - HTML content for tooltip
 */
export const buildPlacePopup = ({ name, lat, lon, address, notes, regionName }) => {
    const notesPreview = truncateNotes(notes);
    const hasNotes = notesPreview.length > 0;

    let html = `<div class="trip-popup place-popup">`;

    // Name (bold) and region
    html += `<div class="popup-header"><strong>${name || 'Unnamed Place'}</strong>`;
    if (regionName) {
        html += `<span class="popup-region text-muted"> (${regionName})</span>`;
    }
    html += `</div>`;

    // Coordinates
    if (Number.isFinite(+lat) && Number.isFinite(+lon)) {
        html += `<div class="popup-coords">
            <span class="text-muted">Lat:</span> ${(+lat).toFixed(5)},
            <span class="text-muted">Lon:</span> ${(+lon).toFixed(5)}
        </div>`;
    }

    // Address if available
    if (address) {
        html += `<div class="popup-address"><span class="text-muted">Address:</span> ${address}</div>`;
    }

    // Notes preview if available
    if (hasNotes) {
        html += `<div class="popup-notes"><span class="text-muted">Notes:</span> ${notesPreview}</div>`;
    }

    // Footer hint
    html += TOOLTIP_FOOTER;

    html += `</div>`;
    return html;
};

/**
 * Builds tooltip HTML content for a Segment polyline
 * @param {Object} segment - Segment data object
 * @param {string} segment.fromPlace - Starting place name
 * @param {string} segment.toPlace - Destination place name
 * @param {string} [segment.fromRegion] - Starting region name
 * @param {string} [segment.toRegion] - Destination region name
 * @param {string} segment.mode - Transport mode
 * @param {string} [segment.distance] - Estimated distance (e.g., "12.5 km")
 * @param {string} [segment.duration] - Estimated duration (e.g., "45 min")
 * @param {string} [segment.notes] - Optional HTML notes
 * @returns {string} - HTML content for tooltip
 */
export const buildSegmentPopup = ({
    fromPlace, toPlace, fromRegion, toRegion,
    mode, distance, duration, notes
}) => {
    const notesPreview = truncateNotes(notes);
    const hasNotes = notesPreview.length > 0;

    // Format from/to with region in parentheses
    const fromDisplay = fromRegion ? `<strong>${fromPlace}</strong> (${fromRegion})` : `<strong>${fromPlace}</strong>`;
    const toDisplay = toRegion ? `<strong>${toPlace}</strong> (${toRegion})` : `<strong>${toPlace}</strong>`;

    // Capitalize mode
    const modeDisplay = mode ? mode.charAt(0).toUpperCase() + mode.slice(1) : 'Unknown';

    let html = `<div class="trip-popup segment-popup">`;

    // From → To line
    html += `<div class="popup-header">From ${fromDisplay} to ${toDisplay}</div>`;

    // Mode and estimates
    html += `<div class="popup-details">`;
    html += `<span class="text-muted">By:</span> ${modeDisplay}`;
    if (distance) {
        html += ` · <span class="text-muted">Distance:</span> ${distance}`;
    }
    if (duration) {
        html += ` · <span class="text-muted">Est. time:</span> ${duration}`;
    }
    html += `</div>`;

    // Notes preview if available
    if (hasNotes) {
        html += `<div class="popup-notes"><span class="text-muted">Notes:</span> ${notesPreview}</div>`;
    }

    // Footer hint
    html += TOOLTIP_FOOTER;

    html += `</div>`;
    return html;
};

/**
 * Builds tooltip HTML content for an Area polygon
 * @param {Object} area - Area data object
 * @param {string} area.name - Area name
 * @param {string} [area.notes] - Optional HTML notes
 * @returns {string} - HTML content for tooltip
 */
export const buildAreaPopup = ({ name, notes }) => {
    const notesPreview = truncateNotes(notes);
    const hasNotes = notesPreview.length > 0;

    let html = `<div class="trip-popup area-popup">`;

    // Name (bold)
    html += `<div class="popup-header"><strong>${name || 'Unnamed Area'}</strong></div>`;

    // Notes preview if available
    if (hasNotes) {
        html += `<div class="popup-notes"><span class="text-muted">Notes:</span> ${notesPreview}</div>`;
    }

    // Footer hint
    html += TOOLTIP_FOOTER;

    html += `</div>`;
    return html;
};
