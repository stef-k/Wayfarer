import L, { type LayerGroup, type Map as LeafletMap } from 'leaflet';
import 'leaflet/dist/leaflet.css';
import type { EditorTarget } from '../composables/useEditorSurface';
import type { EditorArea, EditorCoordinate, EditorPlace, EditorRegion, EditorSegment, EditorTripMetadata, EditorTripState, Guid } from '../types';

export type FitAllGeometryResult = 'moved' | 'no-geometry';
export type FocusSavedTripViewResult = 'moved' | 'missing-view';
export type FocusActiveEntityResult = 'moved' | 'missing-target' | 'no-geometry' | 'unsupported-target';

interface TripEditorMapAdapter {
  render: (state: EditorTripState) => void;
  fitAllGeometry: (state: EditorTripState) => FitAllGeometryResult;
  focusSavedTripView: (metadata: EditorTripMetadata) => FocusSavedTripViewResult;
  focusActiveEntity: (state: EditorTripState, target: EditorTarget | null) => FocusActiveEntityResult;
  dispose: () => void;
}

export const createTripEditorMap = (element: HTMLElement, tilesUrl: string): TripEditorMapAdapter => {
  const map = L.map(element, { zoomControl: true }).setView([20, 0], 2);
  const layers = L.layerGroup().addTo(map);

  L.tileLayer(tilesUrl, {
    attribution: window.wayfarerTileConfig?.attribution ?? '&copy; OpenStreetMap contributors',
    maxZoom: 19
  }).addTo(map);

  const render = (state: EditorTripState): void => {
    layers.clearLayers();

    Object.values(state.regionsById).forEach(region => renderRegion(region, layers));
    Object.values(state.areasById).forEach(area => renderArea(area, layers));
    Object.values(state.placesById).forEach(place => renderPlace(place, layers));
    Object.values(state.segmentsById).forEach(segment => renderSegment(segment, state, layers));

    fitMapToState(map, state);
  };

  return {
    render,
    fitAllGeometry: state => fitAllGeometry(map, state),
    focusSavedTripView: metadata => focusSavedTripView(map, metadata),
    focusActiveEntity: (state, target) => focusActiveEntity(map, state, target),
    dispose: () => map.remove()
  };
};

const renderRegion = (region: EditorRegion, layers: LayerGroup): void => {
  if (!region.center) {
    return;
  }

  L.circleMarker([region.center.latitude, region.center.longitude], {
    radius: region.isShadow ? 5 : 7,
    color: region.isShadow ? '#64748b' : '#2563eb',
    weight: 2,
    fillOpacity: 0.7
  }).bindTooltip(escapeHtml(region.name)).addTo(layers);
};

const renderPlace = (place: EditorPlace, layers: LayerGroup): void => {
  if (!place.location) {
    return;
  }

  const marker = L.marker([place.location.latitude, place.location.longitude], {
    title: place.name
  });
  const visitText = place.visitSummary.isVisited ? ` · ${place.visitSummary.visitCount} visit(s)` : '';
  marker.bindPopup(`<strong>${escapeHtml(place.name)}</strong>${visitText}`);
  marker.addTo(layers);
};

const renderArea = (area: EditorArea, layers: LayerGroup): void => {
  if (!area.geometry) {
    return;
  }

  const rings = area.geometry.coordinates.map(ring => ring.map(([longitude, latitude]) => [latitude, longitude] as [number, number]));
  L.polygon(rings, {
    color: area.fillHex,
    fillColor: area.fillHex,
    fillOpacity: 0.25,
    weight: 2
  }).bindTooltip(escapeHtml(area.name)).addTo(layers);
};

const renderSegment = (segment: EditorSegment, state: EditorTripState, layers: LayerGroup): void => {
  const coordinates = segment.route?.coordinates ?? fallbackSegmentCoordinates(segment, state);
  if (!coordinates || coordinates.length < 2) {
    return;
  }

  const latLngs = coordinates.map(([longitude, latitude]) => [latitude, longitude] as [number, number]);
  L.polyline(latLngs, {
    color: '#0ea5e9',
    weight: 3,
    opacity: 0.8
  }).bindTooltip(escapeHtml(segmentLabel(segment, state))).addTo(layers);
};

const fallbackSegmentCoordinates = (segment: EditorSegment, state: EditorTripState): Array<[number, number]> | null => {
  const from = segment.fromPlaceId ? state.placesById[segment.fromPlaceId]?.location : null;
  const to = segment.toPlaceId ? state.placesById[segment.toPlaceId]?.location : null;
  return from && to ? [[from.longitude, from.latitude], [to.longitude, to.latitude]] : null;
};

// Preserves the existing render-time auto-fit while toolbar commands remain explicit navigation calls.
const fitMapToState = (map: LeafletMap, state: EditorTripState): void => {
  if (fitBounds(map, allGeometryBounds(state)) === 'moved') {
    return;
  }

  focusRenderFallbackView(map, state.metadata);
};

const fitAllGeometry = (map: LeafletMap, state: EditorTripState): FitAllGeometryResult =>
  fitBounds(map, allGeometryBounds(state));

const focusSavedTripView = (map: LeafletMap, metadata: EditorTripMetadata): FocusSavedTripViewResult => {
  if (!hasSavedTripView(metadata)) {
    return 'missing-view';
  }

  map.setView([metadata.center.latitude, metadata.center.longitude], metadata.zoom);
  return 'moved';
};

const focusRenderFallbackView = (map: LeafletMap, metadata: EditorTripMetadata): void => {
  if (!metadata.center || !isFiniteCoordinate(metadata.center)) {
    return;
  }

  map.setView([metadata.center.latitude, metadata.center.longitude], metadata.zoom ?? 8);
};

const focusActiveEntity = (map: LeafletMap, state: EditorTripState, target: EditorTarget | null): FocusActiveEntityResult => {
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

  return 'unsupported-target';
};

export const hasAnyGeometry = (state: EditorTripState): boolean => allGeometryBounds(state).isValid();

export const hasSavedTripView = (metadata: EditorTripMetadata): metadata is EditorTripMetadata & { center: EditorCoordinate; zoom: number } =>
  metadata.center !== null &&
  isFiniteCoordinate(metadata.center) &&
  metadata.zoom !== null &&
  Number.isFinite(metadata.zoom) &&
  metadata.zoom >= 0 &&
  metadata.zoom <= 19;

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

  return false;
};

const fitBounds = (map: LeafletMap, bounds: L.LatLngBounds): FitAllGeometryResult => {
  if (!bounds.isValid()) {
    return 'no-geometry';
  }

  map.fitBounds(bounds, { padding: [32, 32], maxZoom: 12 });
  return 'moved';
};

const allGeometryBounds = (state: EditorTripState): L.LatLngBounds => {
  const bounds = L.latLngBounds([]);
  Object.values(state.regionsById).forEach(region => extendCoordinate(bounds, region.center));
  Object.values(state.placesById).forEach(place => extendCoordinate(bounds, place.location));
  Object.values(state.areasById).forEach(area => extendArea(bounds, area));
  Object.values(state.segmentsById).forEach(segment => extendSegment(bounds, segment, state));
  return bounds;
};

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

const coordinateBounds = (coordinate: EditorCoordinate | null): L.LatLngBounds => {
  const bounds = L.latLngBounds([]);
  extendCoordinate(bounds, coordinate);
  return bounds;
};

const extendCoordinate = (bounds: L.LatLngBounds, coordinate: EditorCoordinate | null | undefined): void => {
  if (coordinate && isFiniteCoordinate(coordinate)) {
    bounds.extend([coordinate.latitude, coordinate.longitude]);
  }
};

const extendArea = (bounds: L.LatLngBounds, area: EditorArea): void => {
  area.geometry?.coordinates.flat().forEach(coordinate => extendLongitudeLatitude(bounds, coordinate));
};

const extendSegment = (bounds: L.LatLngBounds, segment: EditorSegment, state: EditorTripState): void => {
  (segment.route?.coordinates ?? fallbackSegmentCoordinates(segment, state))?.forEach(coordinate => extendLongitudeLatitude(bounds, coordinate));
};

const extendLongitudeLatitude = (bounds: L.LatLngBounds, [longitude, latitude]: [number, number]): void => {
  if (Number.isFinite(latitude) && Number.isFinite(longitude)) {
    bounds.extend([latitude, longitude]);
  }
};

const isFiniteCoordinate = (coordinate: EditorCoordinate): boolean =>
  Number.isFinite(coordinate.latitude) && Number.isFinite(coordinate.longitude);

const segmentLabel = (segment: EditorSegment, state: EditorTripState): string => {
  const fromName = segment.fromPlaceId ? state.placesById[segment.fromPlaceId]?.name : null;
  const toName = segment.toPlaceId ? state.placesById[segment.toPlaceId]?.name : null;
  return [fromName, toName].filter(Boolean).join(' to ') || segment.mode || 'Segment';
};

const escapeHtml = (value: string): string =>
  value.replace(/[&<>"']/g, character => ({
    '&': '&amp;',
    '<': '&lt;',
    '>': '&gt;',
    '"': '&quot;',
    "'": '&#39;'
  })[character] ?? character);

declare global {
  interface Window {
    wayfarerTileConfig?: {
      attribution?: string;
    };
  }
}
