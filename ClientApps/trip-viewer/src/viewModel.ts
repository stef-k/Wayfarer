import type {
  EntityType,
  Guid,
  TripViewerState,
  ViewerArea,
  ViewerCoordinate,
  ViewerNotes,
  ViewerPlace,
  ViewerRegion,
  ViewerSegment,
  ViewerSelection
} from './types';

export interface RegionGroup {
  region: ViewerRegion;
  places: ViewerPlace[];
  areas: ViewerArea[];
}

export interface SegmentSummary {
  segment: ViewerSegment;
  fromPlace: ViewerPlace | null;
  toPlace: ViewerPlace | null;
}

export interface SelectedEntity {
  type: EntityType;
  id: Guid;
  title: string;
  eyebrow: string;
  notes: ViewerNotes;
  region: ViewerRegion | null;
  place: ViewerPlace | null;
  area: ViewerArea | null;
  segment: SegmentSummary | null;
}

export const tripSelection = (state: TripViewerState): ViewerSelection => ({ type: 'trip', id: state.trip.id });

export function isSameSelection(left: ViewerSelection, right: ViewerSelection): boolean {
  return left.type === right.type && left.id === right.id;
}

export function buildRegionGroups(state: TripViewerState): RegionGroup[] {
  return state.regionOrder
    .map(regionId => state.regionsById[regionId])
    .filter(isDefined)
    .map(region => ({
      region,
      places: orderedChildren(state.placeOrderByRegionId[region.id], state.placesById),
      areas: orderedChildren(state.areaOrderByRegionId[region.id], state.areasById)
    }));
}

export function buildSegmentSummaries(state: TripViewerState): SegmentSummary[] {
  return state.segmentOrder
    .map(id => state.segmentsById[id])
    .filter(isDefined)
    .map(segment => ({
      segment,
      fromPlace: segment.fromPlaceId ? state.placesById[segment.fromPlaceId] ?? null : null,
      toPlace: segment.toPlaceId ? state.placesById[segment.toPlaceId] ?? null : null
    }));
}

export function selectedEntity(state: TripViewerState, selection: ViewerSelection): SelectedEntity {
  if (selection.type === 'region') {
    const region = state.regionsById[selection.id];
    if (region) return { type: 'region', id: region.id, title: region.name, eyebrow: 'Region', notes: region.notes, region, place: null, area: null, segment: null };
  }

  if (selection.type === 'place') {
    const place = state.placesById[selection.id];
    if (place) return { type: 'place', id: place.id, title: place.name, eyebrow: 'Place', notes: place.notes, region: state.regionsById[place.regionId] ?? null, place, area: null, segment: null };
  }

  if (selection.type === 'area') {
    const area = state.areasById[selection.id];
    if (area) return { type: 'area', id: area.id, title: area.name, eyebrow: 'Area', notes: area.notes, region: state.regionsById[area.regionId] ?? null, place: null, area, segment: null };
  }

  if (selection.type === 'segment') {
    const segment = state.segmentsById[selection.id];
    if (segment) {
      const summary = {
        segment,
        fromPlace: segment.fromPlaceId ? state.placesById[segment.fromPlaceId] ?? null : null,
        toPlace: segment.toPlaceId ? state.placesById[segment.toPlaceId] ?? null : null
      };
      return { type: 'segment', id: segment.id, title: segmentTitle(summary), eyebrow: 'Segment', notes: segment.notes, region: null, place: null, area: null, segment: summary };
    }
  }

  return { type: 'trip', id: state.trip.id, title: state.trip.name, eyebrow: 'Trip', notes: state.trip.notes, region: null, place: null, area: null, segment: null };
}

export function segmentTitle(summary: SegmentSummary): string {
  const fromName = summary.fromPlace?.name ?? 'Start';
  const toName = summary.toPlace?.name ?? 'End';
  return `${fromName} to ${toName}`;
}

export function notesPreview(notes: ViewerNotes, maxLength = 110): string {
  if (notes.hasTextContent && notes.plainText.trim()) {
    const text = notes.plainText.trim().replace(/\s+/g, ' ');
    return text.length > maxLength ? `${text.slice(0, maxLength - 3).trimEnd()}...` : text;
  }

  return notes.hasMediaContent ? 'Contains media notes' : '';
}

export function coordinateLabel(coordinate: ViewerCoordinate | null): string {
  return coordinate ? `${coordinate.latitude.toFixed(5)}, ${coordinate.longitude.toFixed(5)}` : 'Not set';
}

export function distanceLabel(value: number | null): string {
  return value == null ? 'Not set' : `${value.toFixed(value >= 10 ? 0 : 1)} km`;
}

export function durationLabel(value: number | null): string {
  if (value == null) return 'Not set';
  if (value < 60) return `${Math.round(value)} min`;
  const hours = Math.floor(value / 60);
  const minutes = Math.round(value % 60);
  return minutes > 0 ? `${hours} hr ${minutes} min` : `${hours} hr`;
}

function orderedChildren<T>(ids: Guid[] | undefined, byId: Record<Guid, T>): T[] {
  return (ids ?? []).map(id => byId[id]).filter(isDefined);
}

function isDefined<T>(value: T | undefined | null): value is T {
  return value != null;
}
