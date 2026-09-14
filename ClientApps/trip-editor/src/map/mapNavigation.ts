import L, { type Map as LeafletMap } from 'leaflet';
import type { EditorTarget } from '../composables/useEditorSurface';
import type { EditorArea, EditorCoordinate, EditorSegment, EditorTripMetadata, EditorTripState, Guid } from '../types';
import { resolveUrlMapView } from './mapViewport';

/** Geometry and saved-view navigation results; these commands never own metadata drafts. */
export type FitAllGeometryResult = 'moved' | 'no-geometry';
export type FocusSavedTripViewResult = 'moved' | 'missing-view';
export type FocusActiveEntityResult = 'moved' | 'missing-target' | 'no-geometry' | 'unsupported-target';

/** Resolve the best base first, then apply each valid URL override without emitting navigation. */
export const applyInitialMapView = (map: LeafletMap, state: EditorTripState): void => {
  if (hasSavedTripView(state.metadata)) {
    map.setView([state.metadata.center.latitude, state.metadata.center.longitude], state.metadata.zoom, { animate: false });
  } else {
    fitBounds(map, allGeometryBounds(state), false);
  }
  const center = map.getCenter();
  const view = resolveUrlMapView(window.location.search, {
    center: { latitude: center.lat, longitude: center.lng }, zoom: map.getZoom()
  });
  map.setView([view.center.latitude, view.center.longitude], view.zoom, { animate: false });
};

/** Fit all loaded geometry when no saved view or specific target is available. */
const fitAllGeometry = (map: LeafletMap, state: EditorTripState): FitAllGeometryResult =>
  fitBounds(map, allGeometryBounds(state));

/** Navigate to a valid persisted default, leaving draft ownership to MetadataEditor. */
export const focusSavedTripView = (map: LeafletMap, metadata: EditorTripMetadata): FocusSavedTripViewResult => {
  if (!hasSavedTripView(metadata)) {
    return 'missing-view';
  }

  map.setView([metadata.center.latitude, metadata.center.longitude], metadata.zoom);
  return 'moved';
};

/** Resolve each editor target to its available geometry without inventing missing coordinates. */
export const focusActiveEntity = (map: LeafletMap, state: EditorTripState, target: EditorTarget | null): FocusActiveEntityResult => {
  if (!target) {
    return 'missing-target';
  }

  if (target.kind === 'metadata') {
    return focusSavedTripView(map, state.metadata) === 'moved' ? 'moved' : fitAllGeometry(map, state);
  }

  if (target.kind === 'region') {
    if (target.mode !== 'edit' || !target.entityId) {
      return 'no-geometry';
    }

    return fitBounds(map, regionGeometryBounds(state, target.entityId));
  }

  if (target.kind === 'place') {
    if (target.mode === 'add') {
      return target.parentRegionId ? fitBounds(map, regionGeometryBounds(state, target.parentRegionId)) : 'no-geometry';
    }

    if (!target.entityId) {
      return 'missing-target';
    }

    const place = state.placesById[target.entityId];
    if (!place) {
      return 'missing-target';
    }

    return fitBounds(map, coordinateBounds(place.location));
  }

  if (target.kind === 'area') {
    if (target.mode === 'add') {
      return target.parentRegionId ? fitBounds(map, regionGeometryBounds(state, target.parentRegionId)) : 'no-geometry';
    }

    if (!target.entityId) {
      return 'missing-target';
    }

    const area = state.areasById[target.entityId];
    return area ? fitBounds(map, areaBounds(area)) : 'missing-target';
  }

  if (target.kind === 'segment') {
    if (target.mode !== 'edit' || !target.entityId) {
      return allGeometryBounds(state).isValid() ? fitAllGeometry(map, state) : 'no-geometry';
    }

    const segment = state.segmentsById[target.entityId];
    return segment ? fitBounds(map, segmentBounds(segment, state)) : 'missing-target';
  }

  return 'unsupported-target';
};

/** Drive Fit All availability from the same bounds used by navigation. */
export const hasAnyGeometry = (state: EditorTripState): boolean => allGeometryBounds(state).isValid();

/** Validate persisted view components before giving them to Leaflet. */
export const hasSavedTripView = (metadata: EditorTripMetadata): metadata is EditorTripMetadata & { center: EditorCoordinate; zoom: number } =>
  metadata.center !== null &&
  isFiniteCoordinate(metadata.center) &&
  Math.abs(metadata.center.latitude) <= 90 && Math.abs(metadata.center.longitude) <= 180 &&
  metadata.zoom !== null &&
  Number.isFinite(metadata.zoom) &&
  metadata.zoom >= 0 &&
  metadata.zoom <= 19;

/** Mirror target focus availability for the toolbar without moving the map. */
export const canFocusActiveEntity = (state: EditorTripState, target: EditorTarget | null): boolean => {
  if (!target) {
    return false;
  }

  if (target.kind === 'metadata') {
    return hasSavedTripView(state.metadata) || hasAnyGeometry(state);
  }

  if (target.kind === 'region') {
    return target.mode === 'edit' && Boolean(target.entityId) && regionGeometryBounds(state, target.entityId!).isValid();
  }

  if (target.kind === 'place') {
    if (target.mode === 'add') {
      return Boolean(target.parentRegionId) && regionGeometryBounds(state, target.parentRegionId!).isValid();
    }

    if (!target.entityId) {
      return false;
    }

    return coordinateBounds(state.placesById[target.entityId]?.location ?? null).isValid();
  }

  if (target.kind === 'area') {
    if (target.mode === 'add') {
      return Boolean(target.parentRegionId) && regionGeometryBounds(state, target.parentRegionId!).isValid();
    }

    return Boolean(target.entityId) && areaBounds(state.areasById[target.entityId!]).isValid();
  }

  if (target.kind === 'segment') {
    if (target.mode === 'add') {
      return hasAnyGeometry(state);
    }

    return Boolean(target.entityId) && segmentBounds(state.segmentsById[target.entityId!], state).isValid();
  }

  return false;
};

/** Fit valid bounds with toolbar padding; initial placement explicitly disables animation. */
export const fitBounds = (map: LeafletMap, bounds: L.LatLngBounds, animate?: boolean): FitAllGeometryResult => {
  if (!bounds.isValid()) {
    return 'no-geometry';
  }

  map.fitBounds(bounds, { padding: [32, 32], maxZoom: 12, animate });
  return 'moved';
};

/** Combine region centers, places, areas, and effective segment routes. */
export const allGeometryBounds = (state: EditorTripState): L.LatLngBounds => {
  const bounds = L.latLngBounds([]);
  Object.values(state.regionsById).forEach(region => extendCoordinate(bounds, region.center));
  Object.values(state.placesById).forEach(place => extendCoordinate(bounds, place.location));
  Object.values(state.areasById).forEach(area => extendArea(bounds, area));
  Object.values(state.segmentsById).forEach(segment => extendSegment(bounds, segment, state));
  return bounds;
};

/** Include the region center and its children plus segments touching its places. */
const regionGeometryBounds = (state: EditorTripState, regionId: Guid): L.LatLngBounds => {
  const bounds = L.latLngBounds([]);
  const regionPlaceIds = new Set<Guid>();

  Object.values(state.placesById).forEach(place => {
    if (place.regionId === regionId) {
      regionPlaceIds.add(place.id);
      extendCoordinate(bounds, place.location);
    }
  });
  Object.values(state.areasById).forEach(area => {
    if (area.regionId === regionId) {
      extendArea(bounds, area);
    }
  });
  Object.values(state.segmentsById).forEach(segment => {
    if ((segment.fromPlaceId && regionPlaceIds.has(segment.fromPlaceId)) || (segment.toPlaceId && regionPlaceIds.has(segment.toPlaceId))) {
      extendSegment(bounds, segment, state);
    }
  });
  extendCoordinate(bounds, state.regionsById[regionId]?.center ?? null);
  return bounds;
};

/** Represent an optional coordinate as possibly empty Leaflet bounds. */
const coordinateBounds = (coordinate: EditorCoordinate | null): L.LatLngBounds => {
  const bounds = L.latLngBounds([]);
  extendCoordinate(bounds, coordinate);
  return bounds;
};

/** Build bounds only when the requested area exists. */
const areaBounds = (area: EditorArea | undefined): L.LatLngBounds => {
  const bounds = L.latLngBounds([]);
  if (area) {
    extendArea(bounds, area);
  }

  return bounds;
};

/** Build bounds only when the requested segment exists. */
const segmentBounds = (segment: EditorSegment | undefined, state: EditorTripState): L.LatLngBounds => {
  const bounds = L.latLngBounds([]);
  if (segment) {
    extendSegment(bounds, segment, state);
  }

  return bounds;
};

/** Ignore missing or non-finite coordinates while gathering geometry. */
const extendCoordinate = (bounds: L.LatLngBounds, coordinate: EditorCoordinate | null | undefined): void => {
  if (coordinate && isFiniteCoordinate(coordinate)) {
    bounds.extend([coordinate.latitude, coordinate.longitude]);
  }
};

/** Include every polygon ring in navigation bounds. */
const extendArea = (bounds: L.LatLngBounds, area: EditorArea): void => {
  area.geometry?.coordinates.flat().forEach(coordinate => extendLongitudeLatitude(bounds, coordinate));
};

/** Prefer the effective route, then saved custom geometry, then endpoints. */
const extendSegment = (bounds: L.LatLngBounds, segment: EditorSegment, state: EditorTripState): void => {
  (segment.effectiveRoute?.coordinates ?? segment.route?.coordinates ?? fallbackSegmentCoordinates(segment, state))?.forEach(coordinate => extendLongitudeLatitude(bounds, coordinate));
};

/** Convert finite GeoJSON coordinate order to Leaflet latitude/longitude order. */
const extendLongitudeLatitude = (bounds: L.LatLngBounds, [longitude, latitude]: [number, number]): void => {
  if (Number.isFinite(latitude) && Number.isFinite(longitude)) {
    bounds.extend([latitude, longitude]);
  }
};

/** Guard bounds and persisted-view navigation against malformed numbers. */
const isFiniteCoordinate = (coordinate: EditorCoordinate): boolean =>
  Number.isFinite(coordinate.latitude) && Number.isFinite(coordinate.longitude);

/** Resolve endpoint fallback geometry shared by rendering and navigation. */
export const fallbackSegmentCoordinates = (segment: EditorSegment, state: EditorTripState): Array<[number, number]> | null => {
  const from = segment.fromPlaceId ? state.placesById[segment.fromPlaceId]?.location : null;
  const to = segment.toPlaceId ? state.placesById[segment.toPlaceId]?.location : null;
  return from && to ? [[from.longitude, from.latitude], [to.longitude, to.latitude]] : null;
};
