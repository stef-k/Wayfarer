import { isoDate, periodBounds, horizonDate, moveSelection, validateSelection } from './calendar-scope.js';
import { encode, clockText } from './calendar-detail.js';
import { renderStatistics } from './statistics.js';
import { date } from '../../../localization/messages.js';
import { basisText, count, observedAt, periodCaption, scopeText, statusText, text, weekdayName } from './timeline-text.js';
import { operationText } from './calendar-read-text.js';
/** Actual endpoint IDs disambiguate equal event times across different recorded-location zones. */
export const renderPrivateStatistics = (response, caption, basis) => {
    const endpointTime = key => {
        const endpoint = response.endpoints.find(item => item.kind === key.kind && item.country === key.country
            && item.region === key.region && item.place === key.place && item[key.first ? 'first' : 'last']);
        return endpoint ? encode(clockText(basis === 'capture' ? endpoint.captureTime : endpoint.viewerTime)) : encode(text('Unavailable', 'Unavailable'));
    };
    return renderStatistics(response.statistics, 'countries', value => encode(value), caption, endpointTime);
};
/** Keeps the period and response freshness visible even when a retained observation is partial. */
const periodSummary = (period, status) => {
    if (!period) return text('PeriodPending', 'Period: {0}. No successful observation for this selection.', statusText(status));
    const { knownMatches, mappedMatches, unmappedMatches, unresolvedCandidates } = period.counts;
    const { countriesVisited, regionsVisited, citiesVisited } = period.statistics;
    return [
        text('PeriodCounts', '{0} known · {1} mapped · {2} without map position · {3} unresolved candidates · {4} map markers',
            count(knownMatches), count(mappedMatches), count(unmappedMatches), count(unresolvedCandidates), count(period.markers.length)),
        period.markersSampled ? text('MarkersSampled', 'Sampled: newest five per geohash-5 cell, up to 3,000') : '',
        text('GeographicCounts', 'Countries: {0} · Regions: {1} · Cities: {2}', count(countriesVisited), count(regionsVisited), count(citiesVisited)),
        statusText(status), text('ObservedAt', 'As of {0}', observedAt(period.asOfUtc)),
        Number(unresolvedCandidates) > 0 ? text('PartialMatches', 'Some records could not be evaluated. The known results are shown.')
            : Number(knownMatches) === 0 ? text('NoMatches', 'No matching records for this selection.') : ''
    ].filter(Boolean).join(' · ');
};

const recordsSummary = (state, period) => [
    text('RecordsLoaded', '{0} loaded', count(state.records.length)), statusText(state.status.records),
    state.recordsStarted ? state.nextContinuation ? text('MoreAvailable', 'More available')
        : text('EndAtObservation', 'end at last live observation') : '',
    period ? text('KnownAtObservation', '{0} known at period observation {1}', count(period.counts.knownMatches), observedAt(period.asOfUtc)) : '',
    state.recordsAsOf ? text('LatestPage', 'Latest page: {0}', observedAt(state.recordsAsOf)) : ''
].filter(Boolean).join(' · ');

const previousLabels = {
    day: () => text('PreviousDay', 'Previous day'),
    week: () => text('PreviousWeek', 'Previous week'),
    month: () => text('PreviousMonth', 'Previous month'),
    year: () => text('PreviousYear', 'Previous year'),
    custom: () => text('PreviousCustom', 'Previous range')
};
const nextLabels = {
    day: () => text('NextDay', 'Next day'),
    week: () => text('NextWeek', 'Next week'),
    month: () => text('NextMonth', 'Next month'),
    year: () => text('NextYear', 'Next year'),
    custom: () => text('NextCustom', 'Next range')
};

/** Uses the same validation and disabled state for localized navigation titles. */
const renderNavigation = owner => {
    const { selection, maximumOffset } = owner;
    document.getElementById('previousPeriod').textContent = previousLabels[selection.dateType]();
    document.getElementById('nextPeriod').textContent = nextLabels[selection.dateType]();
    for (const button of document.querySelectorAll('#previousPeriod, #nextPeriod, [data-jump]')) {
        const sameUnit = !button.dataset.jump;
        const direction = button.dataset.direction ? Number(button.dataset.direction) : button.id === 'previousPeriod' ? -1 : 1;
        try {
            validateSelection(moveSelection(selection, sameUnit ? selection.dateType : button.dataset.jump, direction, sameUnit), maximumOffset);
            button.disabled = owner.authEnded === true;
            button.title = sameUnit ? text('MovePeriod', 'Move the full selected period') : text('ShiftAnchor', 'Shift the retained civil anchor');
        } catch (failure) { button.disabled = true; button.title = failure.message; }
    }
};

const rowText = record => {
    const label = record.labelPreview?.text || text('RecordedLocation', 'Recorded location');
    const activity = record.activityNamePreview?.text || text('NoActivity', 'No activity');
    return [
        `#${record.locationId} · ${label}${record.labelPreview?.truncated ? '…' : ''} · ${activity}`,
        text('ViewerTime', 'Your timezone: {0}', clockText(record.viewerTime)),
        text('CaptureTime', 'Recorded local: {0}', clockText(record.captureTime)),
        record.position.status === 'available' ? '' : text('NoPosition', 'No map position')
    ].filter(Boolean).join('\n');
};

/** Read-only DOM consumers; each publication has already passed the concrete page owner's fences. */
export const createCalendarDisplay = (owner, mapView, detail) => {
    let renderedRecords = null, renderedStatistics;
    const render = () => {
        const selection = owner.selection, state = owner.state;
        const bounds = periodBounds(selection), from = isoDate(bounds.from), through = isoDate(bounds.through);
        const caption = periodCaption(from, through);
        document.getElementById('choosePeriod').textContent = caption;
        document.getElementById('periodMode').value = selection.dateType;
        document.getElementById('calendarCaption').textContent = [
            text('SelectionCaption', '{0} · {1} · frozen viewer zone: {2}', scopeText(selection.dateType),
                basisText(selection.timeBasis), selection.viewerZone),
            selection.weekStartsOn ? text('FrozenWeek', 'Frozen week starts on {0}', weekdayName(selection.weekStartsOn)) : '',
            through > horizonDate(selection, owner.maximumOffset) ? text('BeyondHorizon', 'Includes dates after the browsing horizon; results use the full range.') : ''
        ].filter(Boolean).join(' · ');
        renderNavigation(owner);
        const period = state.period;
        document.getElementById('timeline-summary').textContent = periodSummary(period, state.status.period);
        const errors = Object.entries(state.errors).map(([operation, failure]) => {
            const label = operationText(operation);
            return label ? text('OperationFailure', '{0}: {1}', label, failure) : failure;
        });
        document.getElementById('timelineNotice').textContent = [...errors, state.storageError, state.activityNotice, state.adoptionNotice, owner.preferences.error,
            state.retryAt > Date.now() ? text('RetryAt', 'Retry after {0}', date(new Date(state.retryAt), { timeStyle: 'medium' })) : ''].filter(Boolean).join(' · ');
        document.getElementById('mapPanel').hidden = owner.panel === 'records';
        document.getElementById('recordsPanel').hidden = owner.panel !== 'records';
        document.getElementById('showMap').setAttribute('aria-pressed', String(owner.panel === 'map'));
        document.getElementById('showRecords').setAttribute('aria-pressed', String(owner.panel === 'records'));
        document.getElementById('recordsStatus').textContent = recordsSummary(state, period);
        const more = document.getElementById('loadMore'); more.disabled = owner.blocked || state.status.records === 'loading' || state.recordsStarted && !state.nextContinuation;
        more.textContent = state.recordsStarted ? text('LoadMore', 'Load more') : text('LoadRecords', 'Load records / Retry');
        const recordsKey = `${owner.epoch}:${state.records.map(record => record.locationId).join(',')}`;
        if (recordsKey !== renderedRecords) {
            renderedRecords = recordsKey;
            const rows = document.getElementById('recordRows'); const scroll = rows.scrollTop || owner.scroll;
            rows.replaceChildren();
            for (const record of state.records) {
                const button = document.createElement('button'); button.type = 'button'; button.className = 'list-group-item list-group-item-action text-break';
                button.dataset.locationId = record.locationId;
                button.textContent = rowText(record);
                button.style.whiteSpace = 'pre-line'; button.addEventListener('click', () => detail.open(record.locationId)); rows.append(button);
            }
            rows.scrollTop = scroll;
        }
        if (state.statistics && state.statistics !== renderedStatistics) {
            renderedStatistics = state.statistics;
            const response = state.statistics;
            document.getElementById('statsModalContent').innerHTML = renderPrivateStatistics(response, caption, selection.timeBasis);
        }
        if (!state.statistics) document.getElementById('statsModalContent').textContent = state.status.statistics === 'loading'
            ? text('StatisticsLoading', 'Loading geographic statistics…')
            : state.errors.statistics || text('StatisticsInvalidated', 'Statistics unavailable or invalidated. Reopen geographic statistics to read this scope.');
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
