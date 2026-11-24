import { message as tr } from '../../../../wwwroot/js/localization/messages.js';
import type { Content, Kind } from './compositionTypes';

/** Display labels are independent of authored fields and the identifiers sent on the wire. */
export function tripKind(kind: Kind): string {
  return { trip: tr('Editor.Trip', 'Trip'), region: tr('Editor.Region', 'Region'), place: tr('Editor.CompositionPlace', 'Place'), area: tr('Editor.Area', 'Area'), segment: tr('Editor.CompositionSegment', 'Segment') }[kind];
}

/** Only the reserved Region and generated fallbacks are translated. */
export function displayLabel(item: Content | undefined, kind?: Kind): string {
  if (!item) return tr('Editor.CompositionUnavailableObject', 'Unavailable object');
  if (kind === 'region' && item.name === 'Unassigned Places') return tr('Shell.UnassignedPlaces', 'Unassigned Places');
  return item.name ?? item.title ?? tr('Editor.Composition0Route', '{0} route', item.mode || tr('Editor.CompositionHistorical', 'Historical'));
}

/** Presents durable mapping decisions without changing their stored values. */
export function compositionDecision(decision: string): string {
  const labels: Record<string, string> = { included: tr('Editor.CompositionInC', 'In C'), reuse: tr('Editor.CompositionMatched', 'Matched'), separate: tr('Editor.CompositionKeptSeparate', 'Kept separate'),
    skip: tr('Editor.CompositionSkipped', 'Skipped'), destinationRemoved: tr('Editor.CompositionRemovedFromC', 'Removed from C') };
  return labels[decision] ?? tr('Editor.CompositionReviewNeeded', 'Review needed');
}
