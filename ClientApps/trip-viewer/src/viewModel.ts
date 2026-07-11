import type {
  EntityType,
  Guid,
  TripViewerState,
  ViewerArea,
  ViewerCoordinate,
  ViewerNotes,
  ViewerPlace,
  ViewerPlaceVisitSummary,
  ViewerRegion,
  ViewerSegment,
  ViewerSelection,
  ViewerTag
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

export function orderedTags(state: TripViewerState): ViewerTag[] {
  return state.tagOrder
    .map(slug => state.tagsBySlug[slug])
    .filter(isDefined);
}

export function visitSummaryForPlace(state: TripViewerState, place: ViewerPlace): ViewerPlaceVisitSummary | null {
  if (!state.visitProgress.canDisplayProgress || !state.visitProgress.canDisplayCounts || !state.permissions.canReadVisitCounts) {
    return null;
  }

  return state.visitProgress.placeSummariesByPlaceId[place.id] ?? place.visitSummary;
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

export interface SegmentEstimateDisplay {
  detail: string;
  compact: string | null;
}

// Formats only server-returned estimates; it never derives facts from map geometry or endpoints.
export function distanceDisplay(value: number | null): SegmentEstimateDisplay {
  if (value == null) return { detail: 'Distance not provided.', compact: null };
  if (!Number.isFinite(value) || value <= 0) return { detail: 'Distance unavailable.', compact: null };

  return {
    detail: new Intl.NumberFormat(undefined, { style: 'unit', unit: 'kilometer', unitDisplay: 'short', minimumFractionDigits: 0, maximumFractionDigits: 1 }).format(value),
    compact: new Intl.NumberFormat(undefined, { style: 'unit', unit: 'kilometer', unitDisplay: 'short', minimumFractionDigits: 0, maximumFractionDigits: 1 }).format(value)
  };
}

// Formats only server-returned estimate minutes for the viewer's neutral display surfaces.
export function durationDisplay(value: number | null): SegmentEstimateDisplay {
  if (value == null) return { detail: 'Duration not provided.', compact: null };
  if (!Number.isFinite(value) || value <= 0) return { detail: 'Duration unavailable.', compact: null };

  const roundedMinutes = Math.round(value);
  const hours = Math.floor(roundedMinutes / 60);
  const minutes = roundedMinutes % 60;
  const compact = hours === 0 ? `${roundedMinutes} min` : minutes > 0 ? `${hours} hr ${minutes} min` : `${hours} hr`;
  return { detail: compact, compact };
}

// Uses returned display text only and deliberately avoids a client-owned transport taxonomy.
export function segmentModeLabel(mode: string): string | null {
  const trimmed = mode.trim();
  return trimmed ? `${trimmed.charAt(0).toLocaleUpperCase()}${trimmed.slice(1)}` : null;
}

// Validates the returned decorative color before it reaches any presentation surface.
export function validAreaFillHex(fillHex: string | null): string | null {
  return fillHex && /^#[0-9a-f]{6}$/i.test(fillHex) ? fillHex : null;
}

// Matches the existing map-focus capability without calculating an area or exposing its geometry.
export function hasUsableAreaGeometry(geometry: ViewerArea['geometry']): boolean {
  return geometry?.type === 'Polygon'
    && geometry.coordinates.length > 0
    && geometry.coordinates.every(ring => ring.length >= 4 && ring.every(point => Number.isFinite(point[0]) && Number.isFinite(point[1])));
}

function orderedChildren<T>(ids: Guid[] | undefined, byId: Record<Guid, T>): T[] {
  return (ids ?? []).map(id => byId[id]).filter(isDefined);
}

function isDefined<T>(value: T | undefined | null): value is T {
  return value != null;
}
