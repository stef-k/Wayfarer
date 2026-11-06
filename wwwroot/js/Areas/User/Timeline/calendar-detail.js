import { notesHtmlForDisplay } from '../../../util/notes-images.js';
import { generateActivityEditorHtml, initActivitySelect, setupActivityEditorEvents } from '../../../util/activity-editor.js';
import { initWikipediaPopovers, generateWikipediaLinkHtml } from '../../../util/wikipedia-utils.js';
import { renderLocationAddress } from '../../../util/location-address.js';
import { prepareLocationDeletion } from '../../../util/location-mutation.js';
/** Encode all plain stored labels at the HTML boundary; Notes keep the established finite reader. */
export const encode = value => String(value ?? '').replace(/[&<>"']/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[char]);
export const clockText = time => {
    if (time?.status !== 'available') return 'Unavailable';
    const offset = time.offsetSeconds;
    const sign = offset < 0 ? '−' : '+';
    const absolute = Math.abs(offset);
    return `${time.local.replace('T', ' ')} · ${time.zoneId} · UTC${sign}${String(Math.floor(absolute / 3600)).padStart(2, '0')}:${String(Math.floor(absolute % 3600 / 60)).padStart(2, '0')}${absolute % 60 ? ':' + String(absolute % 60).padStart(2, '0') : ''}`;
};
/** Selected detail is independently authorized and never sourced from marker/page mutation evidence. */
export const createCalendarDetail = (owner, mapView) => {
    let currentRecord = null, request = null;
    const modal = document.getElementById('locationModal');
    const content = document.getElementById('modalContent');
    setupActivityEditorEvents('#modalContent', () => owner.refresh());
    modal.addEventListener('shown.bs.modal', () => {
        initWikipediaPopovers(modal);
        if (currentRecord) initActivitySelect(currentRecord.locationId);
    });
    const open = async id => {
        currentRecord = null; owner.selectedId = id; owner.retain(); request?.abort(); request = new AbortController();
        const epoch = owner.epoch, version = ++owner.detailVersion, signal = request.signal;
        const current = () => !owner.blocked && owner.epoch === epoch && owner.detailVersion === version && owner.selectedId === id;
        content.textContent = `Loading record ${id}…`; bootstrap.Modal.getOrCreateInstance(modal).show();
        try {
            const response = await fetch(`/api/location/history/${id}?viewerZone=${encodeURIComponent(owner.selection.viewerZone)}`,
                { signal, cache: 'no-store', credentials: 'same-origin' });
            if (!current()) return;
            if (response.status === 401 || response.status === 403) { owner.sessionEnded(); return; }
            if (!response.ok) throw new Error('Record unavailable or outside accessible history. Its retained identity is not deletion evidence.');
            const detail = await response.json();
            if (!current()) return;
            if (detail.subjectId !== owner.subject) { owner.sessionEnded(); return; }
            if (detail.record?.locationId !== id) throw new Error('Record identity could not be verified.');
            currentRecord = detail.record; const record = currentRecord;
            const location = { ...record, id, notes: record.notesHtml, activityType: encode(record.activityName),
                coordinates: record.position?.status === 'available' ? record.position : null };
            mapView.select(record);
            const measured = key => Number.isFinite(record[key]) ? encode(record[key]) : 'Unavailable';
            const url = `/User/Location/Edit/${id}?returnUrl=${encodeURIComponent(window.location.pathname + window.location.search)}`;
            content.innerHTML = `<p class="small">Record ${id} · detail as of ${encode(detail.observedAtUtc)}. Detail retains this identity. Absence from the map or loaded pages can mean outside this scope, sampling or incomplete traversal; it does not establish deletion.</p>
                <p><strong>Datetime (your timezone):</strong><br>${encode(clockText(record.viewerTime))}</p>
                <p><strong>Recorded local time:</strong><br>${encode(clockText(record.captureTime))}</p>
                <p><strong>Event UTC:</strong> ${encode(record.eventUtc || 'Unavailable')}</p>
                <p><strong>Stored timezone evidence:</strong> ${encode(record.storedTimeZoneId || 'Unavailable')}</p>
                <p>${location.coordinates ? `Latitude: ${record.position.latitude} · Longitude: ${record.position.longitude} <button id="locateRecord" class="btn btn-sm btn-link">Locate</button>` : 'No map position'}</p>
                ${generateActivityEditorHtml(location)}
                <p>Altitude: ${measured('altitude')} m · Accuracy: ${measured('accuracy')} m · Speed: ${measured('speed')} km/h</p>
                <div class="row">${renderLocationAddress(location)}</div>
                ${location.coordinates ? generateWikipediaLinkHtml(location, { query: record.place || record.fullAddress }) : ''}
                <strong>Notes:</strong><div class="border p-2 text-break">${notesHtmlForDisplay(record.notesHtml) || 'No notes available'}</div>
                <div class="d-flex gap-3 mt-2"><a id="editTimelineRecord" href="${url}">Edit</a><button id="deleteTimelineRecord" class="btn btn-link text-danger">Delete</button></div>`;
            content.querySelector('#editTimelineRecord').addEventListener('click', () => owner.retain());
            content.querySelector('#locateRecord')?.addEventListener('click', () => mapView.locate(record));
            content.querySelector('#deleteTimelineRecord').addEventListener('click', async () => {
                try {
                    const deleteProtected = await prepareLocationDeletion(id);
                    wayfarer.showConfirmationModal({ title: 'Confirm Deletion', message: deleteProtected.message, confirmText: 'Delete', onConfirm: async () => {
                        try {
                            const result = await deleteProtected();
                            if (result.success) { bootstrap.Modal.getInstance(modal)?.hide(); owner.refresh(); }
                            else wayfarer.showAlert('warning', result.message || 'Deletion outcome unknown. Inspect current history before deciding again.');
                        } catch (error) { wayfarer.showAlert('warning', error.message); }
                    } });
                } catch (error) { wayfarer.showAlert('warning', error.message); }
            });
            if (modal.classList.contains('show')) { initWikipediaPopovers(modal); initActivitySelect(id); }
        } catch (error) { if (current() && error.name !== 'AbortError') content.textContent = error.message; }
    };
    return { open, clear() { request?.abort(); currentRecord = null; content.textContent = ''; mapView.select(null); } };
};
