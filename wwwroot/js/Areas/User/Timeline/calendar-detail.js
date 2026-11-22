import { notesHtmlForDisplay } from '../../../util/notes-images.js';
import { generateActivityEditorHtml, initActivitySelect, setupActivityEditorEvents } from '../../../util/activity-editor.js';
import { initWikipediaPopovers, generateWikipediaLinkHtml } from '../../../util/wikipedia-utils.js';
import { renderLocationAddress } from '../../../util/location-address.js';
import { confirmTimelineDeletion } from './timeline-delete.js';
import { encodeFeatureText as encode } from '../../../util/feature-metadata.js';
import { date, locale } from '../../../localization/messages.js';
import { coordinate, html, observedAt, text } from './timeline-text.js';
import { locationEditUrl, measurement, notesLabel } from './timeline-location.js';

export { encode };

/** Both event and wall-clock fractions use the document's decimal separator. */
const decimal = new Intl.NumberFormat(locale).formatToParts(0.1).find(part => part.type === 'decimal')?.value ?? '.';

/** Keep the wire fraction alongside the localized UTC seconds because Date has millisecond precision. */
const eventUtcText = iso => {
    const fraction = /\.(\d+)Z$/.exec(iso);
    const wholeSecond = fraction ? iso.slice(0, fraction.index) + 'Z' : iso;
    const formatter = new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'medium', timeZone: 'UTC' });
    const parts = formatter.formatToParts(new Date(wholeSecond));
    const display = parts.map(part => part.type === 'second' && fraction ? part.value + decimal + fraction[1] : part.value).join('');
    return display + ' UTC';
};

/** The server already resolved this wall clock. UTC here carries its components for display only. */
export const clockText = time => {
    if (time?.status !== 'available') return text('Unavailable', 'Unavailable');
    const sign = time.offsetSeconds < 0 ? '−' : '+';
    const absolute = Math.abs(time.offsetSeconds);
    const hours = String(Math.floor(absolute / 3600)).padStart(2, '0');
    const minutes = String(Math.floor(absolute % 3600 / 60)).padStart(2, '0');
    const seconds = absolute % 60 ? ':' + String(absolute % 60).padStart(2, '0') : '';
    const local = date(new Date(time.local + 'Z'), { dateStyle: 'medium', timeStyle: 'medium', hourCycle: 'h23', timeZone: 'UTC' });
    const fraction = time.local.split('.')[1];
    return `${local}${fraction ? decimal + fraction : ''} · ${time.zoneId} · UTC${sign}${hours}:${minutes}${seconds}`;
};
/** Selected detail is independently authorized and never sourced from marker/page mutation evidence. */
export const createCalendarDetail = (owner, mapView) => {
    let currentRecord = null, request = null;
    const modal = document.getElementById('locationModal');
    const content = document.getElementById('modalContent');
    setupActivityEditorEvents('#modalContent', () => owner.refresh(), { protectLanguage: true });
    modal.addEventListener('shown.bs.modal', () => {
        initWikipediaPopovers(modal);
        if (currentRecord) initActivitySelect(currentRecord.locationId);
    });
    const open = async id => {
        currentRecord = null; owner.selectedId = id; owner.retain(); request?.abort(); request = new AbortController();
        const epoch = owner.epoch, version = ++owner.detailVersion, signal = request.signal;
        const current = () => !owner.blocked && owner.epoch === epoch && owner.detailVersion === version && owner.selectedId === id;
        content.textContent = text('RecordLoading', 'Loading record {0}…', id); bootstrap.Modal.getOrCreateInstance(modal).show();
        try {
            const response = await fetch(`/api/location/history/${id}?viewerZone=${encodeURIComponent(owner.selection.viewerZone)}`,
                { signal, cache: 'no-store', credentials: 'same-origin' });
            if (!current()) return;
            if (response.redirected || response.status === 401 || response.status === 403) { owner.sessionEnded(); return; }
            if (!response.ok) {
                content.textContent = text('RecordUnavailable', 'Record unavailable or outside accessible history. Its retained identity is not deletion evidence.');
                return;
            }
            const detail = await response.json();
            if (!current()) return;
            if (detail.subjectId !== owner.subject) { owner.sessionEnded(); return; }
            if (detail.record?.locationId !== id) {
                content.textContent = text('RecordIdentityMismatch', 'Record identity could not be verified.');
                return;
            }
            currentRecord = detail.record; const record = currentRecord;
            const location = { ...record, id, notes: record.notesHtml, activityType: record.activityName,
                coordinates: record.position?.status === 'available' ? record.position : null };
            mapView.select(record);
            const unavailable = text('Unavailable', 'Unavailable');
            content.innerHTML = `<p class="small">${html('RecordObservation', 'Record {0} · detail as of {1}. Detail retains this identity. Absence from the map or loaded pages can mean outside this scope, sampling or incomplete traversal; it does not establish deletion.', id, observedAt(detail.observedAtUtc))}</p>
                <p><strong>${html('ViewerTimeLabel', 'Datetime (your timezone):')}</strong><br>${encode(clockText(record.viewerTime))}</p>
                <p><strong>${html('RecordedTimeLabel', 'Recorded local time:')}</strong><br>${encode(clockText(record.captureTime))}</p>
                <p><strong>${html('EventUtc', 'Event UTC:')}</strong> ${encode(record.eventUtc ? eventUtcText(record.eventUtc) : unavailable)}</p>
                <p><strong>${html('StoredZone', 'Stored timezone evidence:')}</strong> ${encode(record.storedTimeZoneId || unavailable)}</p>
                <p>${location.coordinates ? `${html('Latitude', 'Latitude:')} ${coordinate(record.position.latitude)} ·
                    ${html('Longitude', 'Longitude:')} ${coordinate(record.position.longitude)}
                    <button id="locateRecord" class="btn btn-sm btn-link">${html('Locate', 'Locate')}</button>` : html('NoPosition', 'No map position')}</p>
                ${generateActivityEditorHtml(location)}
                <p>${html('Altitude', 'Altitude:')} ${measurement(record.altitude)} ·
                    ${html('Accuracy', 'Accuracy:')} ${measurement(record.accuracy)} ·
                    ${html('Speed', 'Speed:')} ${measurement(record.speed, true)}</p>
                <div class="row">${renderLocationAddress(location)}</div>
                ${location.coordinates ? generateWikipediaLinkHtml(location, { query: record.place || record.fullAddress }) : ''}
                <strong>${notesLabel()}</strong><div class="border p-2 text-break">${notesHtmlForDisplay(record.notesHtml) || html('NoNotes', 'No notes available')}</div>
                <div class="d-flex gap-3 mt-2"><a id="editTimelineRecord" href="${locationEditUrl(id)}">${html('EditLocation', 'Edit location')}</a>
                <button id="deleteTimelineRecord" class="btn btn-link text-danger">${html('DeleteLocation', 'Delete location')}</button></div>`;
            content.querySelector('#editTimelineRecord').addEventListener('click', () => owner.retain());
            content.querySelector('#locateRecord')?.addEventListener('click', () => mapView.locate(record));
            content.querySelector('#deleteTimelineRecord').addEventListener('click', () => confirmTimelineDeletion(id, () => {
                bootstrap.Modal.getInstance(modal)?.hide();
                owner.refresh();
            }));
            if (modal.classList.contains('show')) { initWikipediaPopovers(modal); initActivitySelect(id); }
        } catch (error) {
            if (current() && error.name !== 'AbortError') content.textContent = text('RecordFailed', 'Could not load this record. Reopen the details to try again.');
        }
    };
    return { open, clear(reason) { request?.abort(); currentRecord = null; content.textContent = reason; mapView.select(null); } };
};
