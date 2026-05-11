import L, { type LayerGroup, type LeafletMouseEvent, type Map as LeafletMap } from 'leaflet';
import 'leaflet/dist/leaflet.css';
import type { EditorTarget } from '../composables/useEditorSurface';
import type { EditorArea, EditorCoordinate, EditorPlace, EditorRegion, EditorSegment, EditorTripMetadata, EditorTripState, Guid } from '../types';
import { createAreaPolygonWorkLayer } from './areaPolygonWorkLayer';
import { createSegmentRouteWorkLayer } from './segmentRouteWorkLayer';
export type { AreaPolygonWorkOptions } from './areaPolygonWorkLayer';
export type { SegmentRouteWorkOptions } from './segmentRouteWorkLayer';

export type FitAllGeometryResult = 'moved' | 'no-geometry';
export type FocusSavedTripViewResult = 'moved' | 'missing-view';
export type FocusActiveEntityResult = 'moved' | 'missing-target' | 'no-geometry' | 'unsupported-target';

interface TripEditorMapAdapter {
  render: (state: EditorTripState, hiddenSegmentIds?: ReadonlySet<Guid>) => void;
  clearSearchPreview: () => void;
  startCoordinatePick: (options: CoordinatePickOptions) => () => void;
  startAreaPolygonWork: (options: AreaPolygonWorkOptions) => () => void;
  startSegmentRouteWork: (options: SegmentRouteWorkOptions) => () => void;
  setSegmentRouteWorkRoute: (route: EditorSegment['route']) => void;
  fitAllGeometry: (state: EditorTripState) => FitAllGeometryResult;
  focusSavedTripView: (metadata: EditorTripMetadata) => FocusSavedTripViewResult;
  focusActiveEntity: (state: EditorTripState, target: EditorTarget | null) => FocusActiveEntityResult;
  showSearchPreview: (coordinate: EditorCoordinate, label: string) => void;
  dispose: () => void;
}

export const createTripEditorMap = (element: HTMLElement, tilesUrl: string): TripEditorMapAdapter => {
  const map = L.map(element, { zoomControl: true }).setView([20, 0], 2);
  const layers = L.layerGroup().addTo(map);
  const searchPreview = createSearchPreviewLayer(map);
  const coordinatePick = createCoordinatePickLayer(map);
  const areaPolygonWork = createAreaPolygonWorkLayer(map);
  const segmentRouteWork = createSegmentRouteWorkLayer(map);

  L.tileLayer(tilesUrl, {
    attribution: window.wayfarerTileConfig?.attribution ?? '&copy; OpenStreetMap contributors',
    maxZoom: 19
  }).addTo(map);

  const render = (state: EditorTripState, hiddenSegmentIds: ReadonlySet<Guid> = new Set()): void => {
    searchPreview.clear();
    coordinatePick.clearRegisteredMarkers();
    areaPolygonWork.stop();
    segmentRouteWork.stop();
    layers.clearLayers();

    Object.values(state.regionsById).forEach(region => renderRegion(region, layers));
    Object.values(state.areasById).forEach(area => renderArea(area, layers));
    Object.values(state.placesById).forEach(place => renderPlace(place, layers, coordinatePick));
    Object.values(state.segmentsById).forEach(segment => {
      if (!hiddenSegmentIds.has(segment.id)) {
        renderSegment(segment, state, layers);
      }
    });

    fitMapToState(map, state);
  };

  return {
    render,
    clearSearchPreview: searchPreview.clear,
    startCoordinatePick: options => {
      searchPreview.clear();
      return coordinatePick.start(options);
    },
    startAreaPolygonWork: options => {
      searchPreview.clear();
      return areaPolygonWork.start(options);
    },
    startSegmentRouteWork: options => {
      searchPreview.clear();
      return segmentRouteWork.start(options);
    },
    setSegmentRouteWorkRoute: route => segmentRouteWork.setRoute(route),
    fitAllGeometry: state => fitAllGeometry(map, state),
    focusSavedTripView: metadata => focusSavedTripView(map, metadata),
    focusActiveEntity: (state, target) => focusActiveEntity(map, state, target),
    showSearchPreview: searchPreview.show,
    dispose: () => {
      searchPreview.dispose();
      coordinatePick.dispose();
      areaPolygonWork.dispose();
      segmentRouteWork.dispose();
      map.remove();
    }
  };
};

const createSearchPreviewLayer = (map: LeafletMap): {
  clear: () => void;
  dispose: () => void;
  show: (coordinate: EditorCoordinate, label: string) => void;
} => {
  const layer = L.layerGroup().addTo(map);

  const clear = (): void => {
    layer.clearLayers();
  };

  const show = (coordinate: EditorCoordinate, label: string): void => {
    clear();
    L.marker([coordinate.latitude, coordinate.longitude], {
      interactive: false,
      keyboard: false,
      title: `Search result preview: ${label}`,
      alt: `Search result preview: ${label}`
    }).addTo(layer);
    map.setView([coordinate.latitude, coordinate.longitude], Math.max(map.getZoom(), 13));
  };

  return {
    clear,
    dispose: clear,
    show
  };
};

export interface CoordinatePickOptions {
  initialCoordinate: EditorCoordinate | null;
  onPicked: (coordinate: EditorCoordinate) => void;
}

const createCoordinatePickLayer = (map: LeafletMap): {
  clearRegisteredMarkers: () => void;
  dispose: () => void;
  isActive: () => boolean;
  pick: (coordinate: EditorCoordinate) => void;
  registerMarker: (marker: L.Marker, coordinate: EditorCoordinate) => void;
  start: (options: CoordinatePickOptions) => () => void;
  stop: () => void;
} => {
  const layer = L.layerGroup().addTo(map);
  let clickHandler: ((event: LeafletMouseEvent) => void) | null = null;
  let onPicked: ((coordinate: EditorCoordinate) => void) | null = null;
  const markers: Array<{ marker: L.Marker; coordinate: EditorCoordinate }> = [];
  const markerListeners: Array<() => void> = [];

  const setPreview = (coordinate: EditorCoordinate): void => {
    layer.clearLayers();
    L.marker([coordinate.latitude, coordinate.longitude], {
      interactive: false,
      keyboard: false,
      title: 'Selected place location preview'
    }).addTo(layer);
  };

  const pick = (coordinate: EditorCoordinate): void => {
    setPreview(coordinate);
    onPicked?.(coordinate);
  };

  const stop = (): void => {
    if (clickHandler) {
      map.off('click', clickHandler);
      clickHandler = null;
    }

    onPicked = null;
    detachMarkerListeners();
    layer.clearLayers();
  };

  const attachMarkerListener = (marker: L.Marker, coordinate: EditorCoordinate): void => {
    const handler = (event: LeafletMouseEvent): void => {
      if (event.originalEvent) {
        L.DomEvent.stop(event.originalEvent);
      }

      marker.closePopup();
      pick(coordinate);
    };
    marker.on('click', handler);
    markerListeners.push(() => marker.off('click', handler));
  };

  const attachMarkerListeners = (): void => {
    detachMarkerListeners();
    markers.forEach(({ marker, coordinate }) => attachMarkerListener(marker, coordinate));
  };

  const detachMarkerListeners = (): void => {
    markerListeners.splice(0).forEach(detach => detach());
  };

  const registerMarker = (marker: L.Marker, coordinate: EditorCoordinate): void => {
    markers.push({ marker, coordinate });
    if (clickHandler) {
      attachMarkerListener(marker, coordinate);
    }
  };

  const clearRegisteredMarkers = (): void => {
    detachMarkerListeners();
    markers.splice(0);
  };

  const start = (options: CoordinatePickOptions): (() => void) => {
    stop();
    onPicked = options.onPicked;
    attachMarkerListeners();

    if (options.initialCoordinate) {
      setPreview(options.initialCoordinate);
    }

    clickHandler = event => {
      const coordinate = { latitude: event.latlng.lat, longitude: event.latlng.lng };
      pick(coordinate);
    };
    map.on('click', clickHandler);
    return stop;
  };

  return {
    clearRegisteredMarkers,
    dispose: () => {
      stop();
      clearRegisteredMarkers();
    },
    isActive: () => clickHandler !== null,
    pick,
    registerMarker,
    start,
    stop
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

const renderPlace = (place: EditorPlace, layers: LayerGroup, coordinatePick: ReturnType<typeof createCoordinatePickLayer>): void => {
  if (!place.location) {
    return;
  }

  const marker = L.marker([place.location.latitude, place.location.longitude], {
    title: place.name
  });
  const visitText = place.visitSummary.isVisited ? ` · ${place.visitSummary.visitCount} visit(s)` : '';
  marker.bindPopup(`<strong>${escapeHtml(place.name)}</strong>${visitText}`);
  coordinatePick.registerMarker(marker, place.location);
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

const areaBounds = (area: EditorArea | undefined): L.LatLngBounds => {
  const bounds = L.latLngBounds([]);
  if (area) {
    extendArea(bounds, area);
  }

  return bounds;
};

const segmentBounds = (segment: EditorSegment | undefined, state: EditorTripState): L.LatLngBounds => {
  const bounds = L.latLngBounds([]);
  if (segment) {
    extendSegment(bounds, segment, state);
  }

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
