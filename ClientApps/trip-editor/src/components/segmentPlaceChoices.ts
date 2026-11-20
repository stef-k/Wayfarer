import { regionDisplayName } from '../regionDisplayName.js';
import { message } from '../../../../wwwroot/js/localization/messages.js';
import type { EditorPlace, EditorSegmentDraft, EditorSegmentWaypointDraftRow, EditorTripState, Guid } from '../types';

/** Uses authoritative membership/order, including the protected Region, without granting Region edit capabilities. */
export function segmentPlaceGroups(state: EditorTripState): Array<{ id: Guid; label: string; places: EditorPlace[] }> {
  return state.regionOrder.flatMap(id => {
    const region = state.regionsById[id];
    if (!region || region.tripId !== state.tripId) return [];
    const places = (state.placeOrderByRegionId[id] ?? []).map(placeId => state.placesById[placeId])
      .filter(place => place && place.tripId === state.tripId && place.regionId === id);
    return [{ id, label: regionDisplayName(region), places }];
  });
}

/** Only located saved Places can become new route anchors; invalid current identities remain visible but disabled. */
export function segmentPlaceHasLocation(place: EditorPlace): boolean {
  const location = place.location;
  return Boolean(location && Number.isFinite(location.latitude) && Math.abs(location.latitude) <= 90
    && Number.isFinite(location.longitude) && Math.abs(location.longitude) <= 180);
}

/** Retains a logical row's current value while excluding other waypoints and endpoints from its replacements. */
export function segmentWaypointChoices(state: EditorTripState, draft: EditorSegmentDraft, row: EditorSegmentWaypointDraftRow | null): EditorPlace[] {
  const used = new Set(draft.waypointRows.filter(candidate => candidate.clientId !== row?.clientId).map(candidate => candidate.placeId));
  return segmentPlaceGroups(state).flatMap(group => group.places)
    .filter(place => place.id === row?.placeId || (!used.has(place.id) && place.id !== draft.fromPlaceId && place.id !== draft.toPlaceId));
}

/** Describes unavailable references without replacing or clearing a persisted draft identity. */
export function segmentPlaceUnavailableLabel(state: EditorTripState, id: Guid): string {
  const place = state.placesById[id];
  if (!place || place.tripId !== state.tripId) return message('Editor.SegmentPlaceGone', 'Place no longer available');
  const region = state.regionsById[place.regionId];
  if (!region || region.tripId !== state.tripId || !state.regionOrder.includes(region.id)) {
    return message('Editor.SegmentRegionGone', '{0} — region unavailable', place.name);
  }
  if (!(state.placeOrderByRegionId[region.id] ?? []).includes(id)) {
    return message('Editor.SegmentPlaceOutsideRegion', '{0} — unavailable in this Region', place.name);
  }
  return place.location
    ? message('Editor.SegmentPlaceInvalidLocation', '{0} — invalid location', place.name)
    : message('Editor.SegmentPlaceNeedsLocation', '{0} — location required', place.name);
}
