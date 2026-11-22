import { message, number } from '../localization/messages.js';
import { formatHistoryDate, getViewerTimeZone } from './datetime.js';
import { encodeFeatureText as encode } from './feature-metadata.js';
import { locationAddressText } from './location-address.js';

const viewerTimeZone = getViewerTimeZone();

/** Localized Location display text, escaped separately for markup sinks. */
export const locationText = (key, english, ...args) => message(`Locations.${key}`, english, ...args);
export const locationHtml = (key, english, ...args) => encode(locationText(key, english, ...args));
export const sourceTimeZone = location => location?.timezone || location?.timeZoneId || location?.timeZone || null;
export const displayCoordinate = value => number(value, { useGrouping: false, maximumSignificantDigits: 21 });

/** Format the same viewer and recorded instants, keeping their separate time zones. */
export const locationTimes = location => {
    const recordedZone = sourceTimeZone(location);
    return {
        viewer: encode(formatHistoryDate(location?.localTimestamp, viewerTimeZone, true)),
        source: recordedZone ? formatHistoryDate(location?.localTimestamp, recordedZone, true) : ''
    };
};

/** The missing-data icon takes a translated field name; numbers and units are display-only. */
export const measurement = (value, key, english, unit = '') => {
    if (value == null || !Number.isFinite(Number(value))) {
        return `<i class="bi bi-patch-question" title="${encode(message('Shell.MissingData',
            'No available data for {0}', locationText(key, english)))}"></i>`;
    }
    return number(Number(value), { minimumFractionDigits: 2, maximumFractionDigits: 2 }) + unit;
};

/** Keep authored address and invariant coordinates in the Maps request. */
export const locationMapsLink = location => {
    const address = locationAddressText(location);
    const lat = location?.coordinates?.latitude;
    const lon = location?.coordinates?.longitude;
    const hasCoords = Number.isFinite(Number(lat)) && Number.isFinite(Number(lon));
    const coords = hasCoords ? `${Number(lat).toFixed(6)},${Number(lon).toFixed(6)}` : '';
    const query = encodeURIComponent(address && coords ? `${address} (${coords})` : coords || address);
    return `<a href="https://www.google.com/maps/search/?api=1&query=${query}" target="_blank"
        class="ms-2 btn btn-outline-primary btn-sm" title="${locationHtml('GoogleMaps', 'View in Google Maps')}">
        <i class="bi bi-globe-europe-africa"></i> ${locationHtml('Maps', 'Maps')}</a>`;
};

/** Both Location lists display the same generated statistics. */
export const renderLocationStats = async () => {
    const summary = document.getElementById('timeline-summary');
    try {
        const response = await fetch('/api/users/stats', { headers: { Accept: 'application/json' } });
        if (!response.ok) throw new Error(`Location statistics returned HTTP ${response.status}`);
        const stats = await response.json();
        const parts = [
            ['Total', 'Total Locations', stats.totalLocations, number],
            ['FromDate', 'From Date', stats.fromDate, value => formatHistoryDate(value, viewerTimeZone)],
            ['ToDate', 'To Date', stats.toDate, value => formatHistoryDate(value, viewerTimeZone)],
            ['Countries', 'Countries', stats.countriesVisited, number],
            ['Regions', 'Regions', stats.regionsVisited, number],
            ['Cities', 'Cities', stats.citiesVisited, number],
        ];
        summary.innerHTML = parts.filter(([, , value]) => value != null).map(([key, english, value, display]) =>
            `<strong>${locationHtml(key, english)}:</strong> ${encode(display(value))}`).join(' | ');
    } catch (error) {
        console.error(error);
        summary.textContent = locationText('StatsFailed', 'Could not load location statistics.');
    }
};

/** Treat an HTTP error as a failed read instead of rendering an empty history. */
export const readLocationHistory = response => {
    if (!response.ok) throw new Error(`Location history returned HTTP ${response.status}`);
    return response.json();
};
