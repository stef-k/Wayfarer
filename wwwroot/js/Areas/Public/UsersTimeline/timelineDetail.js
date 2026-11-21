import { date, message, number } from '../../../localization/messages.js';
import { formatOffsetLabel } from '../../../util/datetime.js';
import { encodeFeatureText as encode } from '../../../util/feature-metadata.js';
import { renderLocationAddress, locationAddressText } from '../../../util/location-address.js';
import { notesHtmlForDisplay } from '../../../util/notes-images.js';
import { generateWikipediaLinkHtml } from '../../../util/wikipedia-utils.js';

/** Formats the existing event instant in the requested timezone, retaining the UTC fallback. */
export const formatTimelineDate = (iso, timeZone, withTime = false) => {
    const instant = new Date(iso);
    if (!iso || !Number.isFinite(instant.getTime())) return '';
    const options = withTime ? { dateStyle: 'medium', timeStyle: 'short', hourCycle: 'h23' } : { dateStyle: 'medium' };
    try {
        const display = date(instant, { ...options, timeZone });
        return withTime ? `${display} ${formatOffsetLabel(instant, timeZone)}`.trim() : display;
    } catch (error) {
        if (!(error instanceof RangeError)) throw error;
        return date(instant, { ...options, timeZone: 'UTC' });
    }
};

/** Prepares an HTML-safe catalog phrase for the existing modal HTML sink. */
const label = (key, readable, ...args) => encode(message(`PublicTimeline.${key}`, readable, ...args));

/** Keeps the existing Google Maps query and invariant coordinates. */
const googleMapsLink = location => {
    const address = locationAddressText(location);
    const { latitude, longitude } = location.coordinates;
    const coordinates = `${latitude.toFixed(6)},${longitude.toFixed(6)}`;
    const query = encodeURIComponent(address ? `${address} (${coordinates})` : coordinates);
    return `<a href="https://www.google.com/maps/search/?api=1&query=${query}"
        target="_blank" rel="noopener" class="ms-2 btn btn-outline-primary btn-sm"
        title="${label('GoogleMaps', 'View in Google Maps')}"><i class="bi bi-globe-europe-africa"></i> ${label('Maps', 'Maps')}</a>`;
};

/** Formats measured values and their units without changing Location DTO scalars. */
const measurement = (value, key, readable, missing) => value == null || !Number.isFinite(Number(value)) ? missing
    : label(key, readable, number(Number(value), { minimumFractionDigits: 2, maximumFractionDigits: 2 }));

/** Displays authored and provider text with localized labels, values and missing-data affordances. */
export const renderTimelineLocation = (location, { isLive, isLatest }, viewerTimeZone) => {
    const { latitude, longitude } = location.coordinates;
    const sourceZone = location.timezone || location.timeZoneId || location.timeZone;
    const viewer = formatTimelineDate(location.localTimestamp, viewerTimeZone, true);
    const source = sourceZone ? formatTimelineDate(location.localTimestamp, sourceZone, true) : '';
    const badge = isLive ? `<span class="badge bg-danger float-end ms-2">${label('Live', 'LIVE LOCATION')}</span>`
        : isLatest ? `<span class="badge bg-success float-end ms-2">${label('Latest', 'LATEST LOCATION')}</span>` : '';
    const missing = `<i class="bi bi-patch-question" role="img"
        title="${label('NoValue', 'No data available')}" aria-label="${label('NoValue', 'No data available')}"></i>`;
    const activity = location.activityType && location.activityType !== 'Unknown' ? encode(location.activityType) : missing;
    const coordinate = value => encode(number(value, { useGrouping: false, maximumSignificantDigits: 21 }));
    const notesHeight = Math.ceil((location.notes?.length ?? 0) / 50) * 24;

    return `<div class="container-fluid">
        ${badge ? `<div class="row mb-2"><div class="col-12">${badge}</div></div>` : ''}
        <div class="row mb-2">
            <div class="col-6"><strong>${label('ViewerTime', 'Date and time (your timezone):')}</strong>
                <div>${encode(viewer) || missing}</div>
            </div>
            <div class="col-6"><strong>${label('RecordedTime', 'Recorded local time:')}</strong>
                ${source ? `<div>${encode(source)}</div>` :
                    `<div class="fst-italic text-muted">${label('NoSourceZone', 'Source timezone unavailable')}</div>`}
            </div>
        </div>
        <div class="row mb-2">
            <div class="col-12"><strong>${label('Coordinates', 'Coordinates:')}</strong></div>
            <div class="col-6">${label('Latitude', 'Latitude:')} <span class="fw-bold text-primary">${coordinate(latitude)}</span></div>
            <div class="col-6">${label('Longitude', 'Longitude:')} <span class="fw-bold text-primary">${coordinate(longitude)}</span></div>
        </div>
        <div class="row mb-2">
            <div class="col-6"><strong>${label('Activity', 'Activity:')}</strong> ${activity}</div>
            <div class="col-6"><strong>${label('Altitude', 'Altitude:')}</strong> ${measurement(location.altitude, 'Metres', '{0} m', missing)}</div>
            <div class="col-6"><strong>${label('Accuracy', 'Accuracy:')}</strong> ${measurement(location.accuracy, 'Metres', '{0} m', missing)}</div>
            <div class="col-6"><strong>${label('Speed', 'Speed:')}</strong> ${measurement(location.speed, 'KilometresPerHour', '{0} km/h', missing)}</div>
        </div>
        <div class="row mb-2"><div class="col-12">
            ${renderLocationAddress(location)}<br/>
            ${googleMapsLink(location)}
            ${generateWikipediaLinkHtml(location, { query: location.place || location.fullAddress })}
        </div></div>
        ${notesHeight ? `<div class="row mb-2"><div class="col-12" style="min-height: ${notesHeight}px">
            <strong>${encode(message('Shell.Notes', 'Notes'))}</strong>
            <div class="border p-1">${notesHtmlForDisplay(location.notes) || label('NoNotes', 'No notes available')}</div>
        </div></div>` : ''}
    </div>`;
};
