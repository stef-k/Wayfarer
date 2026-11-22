import { number } from '../../../localization/messages.js';
import { isoDate, periodBounds } from './calendar-scope.js';
import { basisText, count, observedAt, periodCaption, scopeText, statusText, text, weekdayName } from './timeline-text.js';

/** Formatting applies only to reported measurements. Connection inputs retain dot decimals. */
export const distanceText = metres => {
    const rounded = value => number(value, { maximumFractionDigits: 2 });
    if (metres > 0 && metres < 0.01) return text('LessThanMetres', '<{0} m', rounded(0.01));
    return metres < 1000 ? text('Metres', '{0} m', rounded(metres)) : text('Kilometres', '{0} km', rounded(metres / 1000));
};

const pathState = state => ({
    off: () => text('Off', 'Off'),
    'not-requested': () => text('Off', 'Off'),
    available: () => text('GeometryAvailable', 'Available'),
    unavailable: () => text('GeometryUnavailable', 'Unavailable')
})[state]?.() ?? statusText('pending');

const pathReason = reason => ({
    geometryBudgetExceeded: () => text('GeometryBudget', 'The path exceeds the rendering limit.'),
    unplaceableEventTime: () => text('UnplaceableEventTime', 'A source event has no usable timestamp.')
})[reason]?.();

const coverageText = coverage => ({
    complete: () => text('CoverageComplete', 'Complete'),
    partial: () => text('CoveragePartial', 'Partial')
})[coverage]?.() ?? text('CoverageUnknown', 'Unknown');

/** Describe the strongest supported distance for this observation and the separate path result. */
export const distanceSummary = (analysis, status, path) => {
    const label = !analysis || analysis.coverage === 'unknown' ? text('DistanceUnavailable', 'Distance unavailable')
        : analysis.coverage === 'partial' ? text('DistancePartial', 'Distance in available segments — Partial')
            : text('DistanceComplete', 'Distance between recorded locations');
    return [
        analysis?.distanceMetres != null ? text('LabelValue', '{0}: {1}', label, distanceText(analysis.distanceMetres)) : label,
        statusText(status), analysis ? text('ObservedAt', 'As of {0}', observedAt(analysis.asOfUtc)) : '',
        path ? text('PathState', 'Path: {0}', pathState(analysis?.geometry.state)) : ''
    ].filter(Boolean).join(' · ');
};

const analysisCounts = counts => [
    text('ExaminedRows', 'Source records examined: {0}', count(counts.examinedSourceRows)),
    text('KnownMatches', 'Known matches: {0}', count(counts.knownMatches)),
    text('MappedMatches', 'Mapped matches: {0}', count(counts.mappedMatches)),
    text('UnmappedMatches', 'Matches without a map position: {0}', count(counts.unmappedMatches)),
    text('UnresolvedPlaceable', 'Unresolved candidates with a usable timestamp: {0}', count(counts.unresolvedPlaceableCandidates)),
    text('EligibleEdges', 'Eligible connections: {0}', count(counts.eligibleEdges)),
    text('EqualTimeDisagreements', 'Conflicting same-time groups: {0}', count(counts.equalTimeDisagreementGroups)),
    text('GapBreaks', 'Breaks due to time gap: {0}', count(counts.gapBreaks)),
    text('SpeedBreaks', 'Breaks due to speed: {0}', count(counts.speedBreaks))
];

/** Render the snapshot's known analysis facts instead of exposing its JSON envelope. */
export const analysisFacts = analysis => {
    if (!analysis) return text('AnalysisPending', 'No successful analysis observation yet. Refresh / Retry retains the applied scope.');
    const { selection, counts, geometry, rules } = analysis;
    const { from, through } = periodBounds(selection);
    // Preserve significant digits of very small/large rules in the displayed applied facts.
    const ruleNumber = value => number(value, { maximumSignificantDigits: 17 });
    const speed = rules.speedEnabled ? text('MetresPerSecond', '{0} m/s', ruleNumber(rules.maximumSpeedMetresPerSecond))
        : text('Off', 'Off');
    return [
        text('SourceHistory', 'Source: stored location history for this account'),
        text('AnalysisCalendar', 'Calendar: {0} · {1} · {2} · {3}', scopeText(selection.dateType),
            periodCaption(isoDate(from), isoDate(through)), basisText(selection.timeBasis), selection.viewerZone),
        selection.weekStartsOn ? text('FrozenWeek', 'Frozen week starts on {0}', weekdayName(selection.weekStartsOn)) : '',
        text('ObservedAt', 'As of {0}', observedAt(analysis.asOfUtc)),
        text('AnalysisCoverage', 'Coverage: {0}', coverageText(analysis.coverage)),
        text('AppliedGap', 'Applied gap: {0} s', ruleNumber(rules.maximumGapSeconds)),
        text('SpeedHeuristic', 'Speed heuristic: {0}', speed),
        ...analysisCounts(counts),
        analysis.unplaceableEventExists ? text('UnplaceableEventPresent', 'At least one source event lacks a usable timestamp.')
            : text('UnplaceableEventAbsent', 'All source events have a usable timestamp.'),
        text('PathState', 'Path: {0}', pathState(geometry.state)), pathReason(geometry.reason),
        text('SeparateObservations', 'Analysis is a separate live observation from the map, records and date marks.')
    ].filter(Boolean).join('\n');
};
