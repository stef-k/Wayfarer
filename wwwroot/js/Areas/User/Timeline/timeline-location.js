import { encodeFeatureText as encode } from '../../../util/feature-metadata.js';
import { locationAddressText, renderLocationAddress } from '../../../util/location-address.js';
import { message, number } from '../../../localization/messages.js';
import { formatHistoryDate } from '../../../util/datetime.js';
import { generateActivityEditorHtml } from '../../../util/activity-editor.js';
import { notesHtmlForDisplay } from '../../../util/notes-images.js';
import { generateWikipediaLinkHtml } from '../../../util/wikipedia-utils.js';
import { coordinate, html } from './timeline-text.js';

/** Preserve the current calendar or map URL, including the tab's opaque filter context. */
export const locationEditUrl = id => {
    const { pathname, search, hash } = window.location;
    return `/User/Location/Edit/${id}?returnUrl=${encodeURIComponent(pathname + search + hash)}`;
};

/** Map query coordinates use the API's invariant decimal syntax. */
const googleMapsLink = location => {
    const address = locationAddressText(location);
    const lat = Number(location.coordinates.latitude), lng = Number(location.coordinates.longitude);
    const hasCoordinates = Number.isFinite(lat) && Number.isFinite(lng);
    const coordinates = hasCoordinates ? `${lat.toFixed(6)},${lng.toFixed(6)}` : '';
    const query = encodeURIComponent(hasCoordinates ? address ? `${address} (${coordinates})` : coordinates : address);
    return `<a href="https://www.google.com/maps/search/?api=1&query=${query}" target="_blank" rel="noopener"
        class="ms-2 btn btn-outline-primary btn-sm" title="${html('GoogleMaps', 'View in Google Maps')}">
        <i class="bi bi-globe-europe-africa"></i> ${html('GoogleMapsLabel', 'Maps')}</a>`;
};

/** Apply locale formatting to human-facing measurements only. */
export const measurement = (value, speed = false) => {
    if (value == null || !Number.isFinite(Number(value))) {
        const unavailable = html('NoValue', 'No data available');
        return `<i class="bi bi-patch-question" role="img" title="${unavailable}" aria-label="${unavailable}"></i>`;
    }
    const formatted = number(Number(value), { minimumFractionDigits: 2, maximumFractionDigits: 2 });
    return speed ? html('KilometresPerHour', '{0} km/h', formatted) : html('Metres', '{0} m', formatted);
};

export const notesLabel = () => encode(message('Shell.Notes', 'Notes'));

/** Read an ISO date and time with an explicit UTC designator or offset. */
const parseEventInstant = value => {
    if (typeof value !== 'string' || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?(?:Z|[+-]\d{2}:?\d{2})$/i.test(value)) return null;
    // Date can roll impossible civil dates into the following month.
    if (new Date(value.slice(0, 10)).getUTCDate() !== Number(value.slice(8, 10))) return null;
    const instant = new Date(value);
    return Number.isFinite(instant.getTime()) ? instant : null;
};

/** Render both live marker clocks from the same event instant in the current document locale. */
export const renderLiveLocation = (location, { isLive, isLatest }, viewerTimeZone) => {
    // build your badge HTML
    let badge = '';
    if (isLive) {
        badge = `<span class="badge bg-danger float-end ms-2">${html('LiveBadge', 'LIVE LOCATION')}</span>`;
    } else if (isLatest) {
        badge = `<span class="badge bg-success float-end ms-2">${html('LatestBadge', 'LATEST LOCATION')}</span>`;
    }

    const notesHeight = Math.ceil((location.notes?.length ?? 0) / 50) * 24;
    const style = notesHeight ? `min-height: ${notesHeight}px;` : 'display: none;';

    const instant = parseEventInstant(location.eventUtc) ?? parseEventInstant(location.localTimestamp);
    const viewer = formatHistoryDate(instant, viewerTimeZone, true);
    const sourceZone = location.timezone || location.timeZoneId || location.timeZone;
    const source = sourceZone ? formatHistoryDate(instant, sourceZone, true) : '';
    const recordedTime = source
        ? `<div>${encode(source)}</div>`
        : `<div class="fst-italic text-muted">${html('NoSourceZone', 'Source timezone unavailable')}</div>`;

    return `<div class="container-fluid">
        <div class="row mb-2">
            <div class="col-12">
                ${badge}
            </div>
        </div>
        <div class="row mb-2">
            <div class="col-6">
                <strong>${html('ViewerTimeLabel', 'Datetime (your timezone):')}</strong>
                <div>${encode(viewer) || html('Unavailable', 'Unavailable')}</div>
            </div>
            <div class="col-6">
                <strong>${html('RecordedTimeLabel', 'Recorded local time:')}</strong>
                ${recordedTime}
                ${sourceZone && !source ? `<div class="small text-muted">${encode(sourceZone)}</div>` : ''}
            </div>
        </div>
        <div class="row mb-2">
            <div class="col-12"><strong>${html('Coordinates', 'Coordinates:')}</strong></div>
            <div class="col-6">
                <p class="mb-0">${html('Latitude', 'Latitude:')} <span class="fw-bold text-primary">${coordinate(location.coordinates.latitude)}</span></p>
            </div>
            <div class="col-6">
                <p class="mb-0">${html('Longitude', 'Longitude:')} <span class="fw-bold text-primary">${coordinate(location.coordinates.longitude)}</span></p>
            </div>
        </div>
        <div class="row mb-2">
            <div class="col-6">${generateActivityEditorHtml(location)}</div>
            <div class="col-6"><strong>${html('Altitude', 'Altitude:')}</strong> <span>${measurement(location.altitude)}</span></div>
        </div>
        <div class="row mb-2">
            <div class="col-6"><strong>${html('Accuracy', 'Accuracy:')}</strong> <span>${measurement(location.accuracy)}</span></div>
            <div class="col-6"><strong>${html('Speed', 'Speed:')}</strong> <span>${measurement(location.speed, true)}</span></div>
        </div>
        <div class="row mb-2">
            ${renderLocationAddress(location)}<div class="col-12"><br/>
            ${googleMapsLink(location)}
            ${generateWikipediaLinkHtml(location, { query: location.place || location.fullAddress })}
            </div>
        </div>
        <div class="row mb-2">
            <div class="col-12" style="${style}"><strong>${notesLabel()}</strong>
                <div class="border p-1" >
                    ${notesHtmlForDisplay(location.notes) || html('NoNotes', 'No notes available')}
                </div>
            </div>
        </div>
        <div class="row">
            <div class="col-5">
                <a href="${locationEditUrl(location.id)}" class="btn-link">${html('EditLocation', 'Edit location')}</a>
            </div>
            <div class="col-5 offset-2">
                <a href="#" class="btn-link text-danger delete-location-from-popup" data-location-id="${location.id}">${html('DeleteLocation', 'Delete location')}</a>
            </div>
        </div>
    </div>
`;
};
