import { isoDate, periodBounds, horizonDate, moveSelection, validateSelection } from './calendar-scope.js';
import { encode, clockText } from './calendar-detail.js';
import { renderStatistics } from './statistics.js';
/** Actual endpoint IDs disambiguate equal event times across different recorded-location zones. */
export const renderPrivateStatistics = (response, caption, basis) => {
    const endpointTime = key => {
        const endpoint = response.endpoints.find(item => item.kind === key.kind && item.country === key.country
            && item.region === key.region && item.place === key.place && item[key.first ? 'first' : 'last']);
        return endpoint ? encode(clockText(basis === 'capture' ? endpoint.captureTime : endpoint.viewerTime)) : 'Unavailable';
    };
    return renderStatistics(response.statistics, 'countries', value => encode(value), caption, endpointTime);
};
/** Read-only DOM consumers; each publication has already passed the concrete page owner's fences. */
export const createCalendarDisplay = (owner, mapView, detail) => {
    let renderedRecords = null, renderedStatistics;
    const render = () => {
        const selection = owner.selection, state = owner.state;
        const bounds = periodBounds(selection), from = isoDate(bounds.from), through = isoDate(bounds.through);
        const caption = from === through ? from : `${from} – ${through}`;
        document.getElementById('choosePeriod').textContent = caption;
        document.getElementById('periodMode').value = selection.dateType;
        document.getElementById('calendarCaption').textContent = `${selection.dateType} · ${selection.timeBasis === 'capture' ? 'Recorded-location calendar' : 'My time zone'} · frozen viewer ${selection.viewerZone}${selection.weekStartsOn ? ' · frozen week start ' + selection.weekStartsOn : ''}${through > horizonDate(selection, owner.maximumOffset) ? ' · Includes dates after the browsing horizon; results use the full range.' : ''}`;
        document.getElementById('previousPeriod').textContent = selection.dateType === 'custom' ? 'Previous range' : `Previous ${selection.dateType}`;
        document.getElementById('nextPeriod').textContent = selection.dateType === 'custom' ? 'Next range' : `Next ${selection.dateType}`;
        for (const button of document.querySelectorAll('#previousPeriod, #nextPeriod, [data-jump]')) {
            const sameUnit = !button.dataset.jump;
            const direction = button.dataset.direction ? Number(button.dataset.direction) : button.id === 'previousPeriod' ? -1 : 1;
            try {
                validateSelection(moveSelection(selection, sameUnit ? selection.dateType : button.dataset.jump, direction, sameUnit), owner.maximumOffset);
                button.disabled = owner.authEnded === true; button.title = sameUnit ? 'Move the full selected period' : 'Shift the retained civil anchor';
            } catch (failure) { button.disabled = true; button.title = failure.message; }
        }
        const period = state.period;
        document.getElementById('timeline-summary').textContent = period
            ? `${period.counts.knownMatches} known · ${period.counts.mappedMatches} mapped · ${period.counts.unmappedMatches} without map position · ${period.counts.unresolvedCandidates} unresolved candidates · ${period.markers.length} map markers${period.markersSampled ? ' (sampled: newest five per geohash-5 cell, up to 3,000)' : ''} · Countries ${period.statistics.countriesVisited} · Regions ${period.statistics.regionsVisited} · Cities ${period.statistics.citiesVisited} · ${state.status.period} · as of ${period.asOfUtc}`
            : `Period ${state.status.period || 'pending'}; no successful observation for this selection.`;
        const errors = Object.entries(state.errors).map(([operation, message]) => `${operation}: ${message}`).join(' · ');
        document.getElementById('timelineNotice').textContent = [errors, state.storageError, state.activityNotice, state.adoptionNotice, owner.preferences.error,
            state.retryAt > Date.now() ? `Retry after ${new Date(state.retryAt).toLocaleTimeString()}` : ''].filter(Boolean).join(' · ');
        document.getElementById('mapPanel').hidden = owner.panel === 'records';
        document.getElementById('recordsPanel').hidden = owner.panel !== 'records';
        document.getElementById('showMap').setAttribute('aria-pressed', String(owner.panel === 'map'));
        document.getElementById('showRecords').setAttribute('aria-pressed', String(owner.panel === 'records'));
        document.getElementById('recordsStatus').textContent = `${state.records.length} loaded · ${state.status.records || 'not loaded'}${state.recordsStarted ? state.nextContinuation ? ' · more available' : ' · end at last live observation' : ''}${period ? ` · ${period.counts.knownMatches} known at period observation ${period.asOfUtc}` : ''}${state.recordsAsOf ? ' · latest page ' + state.recordsAsOf : ''}`;
        const more = document.getElementById('loadMore'); more.disabled = owner.blocked || state.status.records === 'loading' || state.recordsStarted && !state.nextContinuation;
        more.textContent = state.recordsStarted ? 'Load more' : 'Load records / Retry';
        const recordsKey = `${owner.epoch}:${state.records.map(record => record.locationId).join(',')}`;
        if (recordsKey !== renderedRecords) {
            renderedRecords = recordsKey;
            const rows = document.getElementById('recordRows'); const scroll = rows.scrollTop || owner.scroll;
            rows.replaceChildren();
            for (const record of state.records) {
                const button = document.createElement('button'); button.type = 'button'; button.className = 'list-group-item list-group-item-action text-break';
                button.dataset.locationId = record.locationId;
                button.textContent = `#${record.locationId} · ${record.labelPreview?.text || 'Recorded location'}${record.labelPreview?.truncated ? '…' : ''} · ${record.activityNamePreview?.text || 'No activity'}\nYour timezone: ${clockText(record.viewerTime)}\nRecorded local: ${clockText(record.captureTime)}${record.position.status === 'available' ? '' : '\nNo map position'}`;
                button.style.whiteSpace = 'pre-line'; button.addEventListener('click', () => detail.open(record.locationId)); rows.append(button);
            }
            rows.scrollTop = scroll;
        }
        if (state.statistics && state.statistics !== renderedStatistics) {
            renderedStatistics = state.statistics;
            const response = state.statistics;
            document.getElementById('statsModalContent').innerHTML = renderPrivateStatistics(response, caption, selection.timeBasis);
        }
        if (!state.statistics) document.getElementById('statsModalContent').textContent = 'Statistics unavailable or invalidated. Reopen geographic statistics to read this scope.';
        mapView.render(owner);
        if (period && owner.fitOnNext) { owner.fitOnNext = false; mapView.fit(); }
    };
    document.getElementById('recordRows').addEventListener('scroll', event => { owner.scroll = event.target.scrollTop; owner.retain(); });
    document.getElementById('statsModalContent').addEventListener('click', event => {
        const link = event.target.closest('.country-coords-link');
        if (!link || !owner.state.statistics) return;
        event.preventDefault(); const params = new URLSearchParams(link.getAttribute('href').slice(1));
        bootstrap.Modal.getInstance(document.getElementById('statsModal'))?.hide();
        owner.panel = 'map'; owner.emit(); mapView.map.invalidateSize();
        mapView.map.setView([Number(link.dataset.lat), Number(link.dataset.lng)], Number(params.get('zoom')) || 8);
    });
    return { render };
};
