import { message, number } from '../../../../wwwroot/js/localization/messages.js';
import type { CheckEstimate, CheckFinding, CheckLocation, CheckMovementEvidence, CheckRouteSource, CheckTreatment } from './planningCheckTypes';
import { planningFindingText } from './planningFindingText';
import { describePlanningValue } from './planningTypes';

/** Only display values are formatted. Source binding and stored estimates remain intact. */
const amount = (value: number | null | undefined, units: 'distance' | 'duration'): string => {
  if (value == null) return message('Editor.EstimateUnavailable', 'Unavailable');
  const formatted = number(value, { maximumFractionDigits: 3 });
  return units === 'distance' ? message('Editor.EstimateDistance', '{0} km', formatted)
    : message('Editor.EstimateDuration', '{0} min', formatted);
};

/** Known duration sources are display labels; the mode remains the configured transport label. */
export const durationSourceText = (source: string): string =>
  source === 'Manual' ? message('Editor.Manual', 'Manual') : message('Editor.Automatic', 'Automatic');

const locationText = (location: CheckLocation): string => {
  if (location.route) return `${location.name} · ${checkRouteText(location.route)}`;
  if (location.latitude == null || location.longitude == null) return location.name;
  const coordinate = (value: number) => number(value, { maximumFractionDigits: 6, useGrouping: false });
  return `${location.name} (${coordinate(location.latitude)} / ${coordinate(location.longitude)})`;
};

/** Formats the ordered anchors and provenance from the saved estimate snapshot. */
export const checkRouteText = (route: CheckRouteSource): string => message('Editor.CheckSourceRoute', '{0} · {1} · {2} · {3} · {4} · {5}',
  route.mode, amount(route.duration, 'duration'), durationSourceText(route.durationSource),
  route.anchors.map(anchor => anchor ? locationText(anchor) : message('Editor.CheckUnknownAnchor', 'Unknown anchor')).join(' → '),
  route.customRoute ? message('Editor.CheckCustomRoute', 'Saved custom route') : message('Editor.CheckAnchorRoute', 'Anchor route'),
  amount(route.distance, 'distance'));

const estimateText = (source: CheckEstimate): string => {
  if (source.route) return checkRouteText(source.route);
  if (source.routeUnavailable) return message('Editor.CheckSourceUnavailable', 'Selected Segment unavailable');
  return message('Editor.CheckManualEstimate', 'Check-only manual estimate: {0}', amount(source.manualEstimate, 'duration'));
};

/** Gives the relevance control the full directional evidence captured by the check. */
export const movementEvidenceText = (evidence: CheckMovementEvidence): string => {
  if (!evidence.source || !evidence.locations) return message('Editor.CheckDetailFallback', 'Review the listed entries and check assumptions for this result.');
  return `${evidence.locations.map(locationText).join(' → ')}. ${estimateText(evidence.source)}`;
};

const treatmentText = (value: CheckTreatment): string => {
  const labels = {
    unknown: ['Editor.CheckTreatmentUnknown', 'unknown'],
    none: ['Editor.CheckTreatmentNone', 'none needed'],
    included: ['Editor.CheckTreatmentIncluded', 'included in estimate'],
    additional: ['Editor.CheckTreatmentAdditional', 'additional']
  } as const;
  return message(...(labels[value] ?? labels.unknown));
};

/** Renders a movement result without parsing or rewriting authored names, locations or timing inputs. */
export const checkFindingText = (finding: CheckFinding): string => {
  const movement = finding.movement;
  if (!movement) return planningFindingText(finding.result.message);
  const predicate = movement.deadline ? message('Editor.CheckDeadline', 'Arrive by {0} · check only', describePlanningValue(movement.deadline))
    : message('Editor.CheckIntendedArrival', 'Intended arrival (saved Start/At)');
  const relevance = movement.relevant ? message('Editor.CheckRelevanceConfirmed', 'confirmed') : message('Editor.CheckRelevanceUnresolved', 'unresolved');
  const assumptions = message('Editor.CheckMovementAssumptions', 'Source: {0}. Relevance: {1}. Allowance: {2}. Connection: {3}.',
    estimateText(movement.estimate), relevance, treatmentText(movement.allowance), treatmentText(movement.connection));
  const whatIf = movement.whatIfRelease ? message('Editor.CheckWhatIfEvidence', 'What-if release: {0}. Saved intentions and history are unchanged.',
    describePlanningValue(movement.whatIfRelease)) : '';
  return message('Editor.CheckMovementResult', '{0}. {1} {2}', predicate, planningFindingText(movement.comparison), `${assumptions} ${whatIf}`.trim());
};
