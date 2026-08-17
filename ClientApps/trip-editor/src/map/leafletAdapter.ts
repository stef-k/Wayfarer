import L, { type LayerGroup, type LeafletMouseEvent, type Map as LeafletMap } from 'leaflet';
import 'leaflet/dist/leaflet.css';
import type { EditorTarget } from '../composables/useEditorSurface';
import type { SegmentRouteWorkState } from '../components/segmentRouteWorkState';
import type { EditorSegmentDraftPresentation, SegmentPresentationKey } from '../segments/editorSegmentPresentation';
import { resolveDraftSegmentPresentation, resolvePersistedSegmentPresentation } from '../segments/editorSegmentPresentation';
import type { EditorArea, EditorCoordinate, EditorPlace, EditorRegion, EditorSegment, EditorTripMetadata, EditorTripState, Guid } from '../types';
import { createAreaPolygonWorkLayer, type AreaPolygonWorkOptions } from './areaPolygonWorkLayer';
import { createMapUtilitiesControl } from './mapUtilitiesControl';
import { createPlaceCoordinatePickLayer, createPlaceDraftPreviewLayer, type CoordinatePickOptions } from './placeDraftPreviewLayer';
import { placeMarkerIcon, regionMarkerIcon } from './markerRendering';
import { placePopupHtml } from './placePopupRendering';
import { createSearchPreviewLayer } from './searchPreviewLayer';
import { createSegmentRouteDraftPreviewLayer, type SegmentDraftRoutePreview } from './segmentRouteDraftPreviewLayer';
import { createSegmentRouteWorkLayer, type SegmentRouteWorkOptions } from './segmentRouteWorkLayer';
import { createSegmentPresentationLayer } from './segmentPresentationLayer';
import { createTripEditorTileLayer } from './tileRetryLayer';
export type { AreaPolygonWorkOptions } from './areaPolygonWorkLayer';
export type { CoordinatePickOptions } from './placeDraftPreviewLayer';
export type { SegmentRouteWorkOptions } from './segmentRouteWorkLayer';
export type { SegmentDraftRoutePreview } from './segmentRouteDraftPreviewLayer';

export type FitAllGeometryResult = 'moved' | 'no-geometry';
export type FocusSavedTripViewResult = 'moved' | 'missing-view';
export type FocusActiveEntityResult = 'moved' | 'missing-target' | 'no-geometry' | 'unsupported-target';
export type InitialMapViewSource = 'url' | 'saved' | 'fit-bounds' | 'fallback';

interface TripEditorMapAdapter {
  render: (state: EditorTripState, hiddenSegmentIds?: ReadonlySet<Guid>, selectedPlaceId?: Guid | null) => void;
  clearSearchPreview: () => void;
  getMapView: () => TripEditorMapView;
  selectPlace: (state: EditorTripState, placeId: Guid | null, options?: SelectPlaceOptions) => void;
  setPlaceDraftPreview: (state: EditorTripState, preview: PlaceDraftMarkerPreview | null) => void;
  setSegmentDraftPreview: (state: EditorTripState, preview: SegmentDraftRoutePreview | null) => void;
  setSegmentPresentation: (state: EditorTripState, key: SegmentPresentationKey | null, draft: EditorSegmentDraftPresentation | null) => void;
  startCoordinatePick: (options: CoordinatePickOptions) => () => void;
  startAreaPolygonWork: (options: AreaPolygonWorkOptions) => () => void;
  startSegmentRouteWork: (options: SegmentRouteWorkOptions) => () => void;
  setSegmentRouteWorkState: (state: SegmentRouteWorkState) => void;
  fitAllGeometry: (state: EditorTripState) => FitAllGeometryResult;
  focusSavedTripView: (metadata: EditorTripMetadata) => FocusSavedTripViewResult;
  focusActiveEntity: (state: EditorTripState, target: EditorTarget | null) => FocusActiveEntityResult;
  showSearchPreview: (coordinate: EditorCoordinate, label: string) => void;
  dispose: () => void;
}

export interface TripEditorMapView {
  center: EditorCoordinate;
  zoom: number;
}

export interface PlaceDraftMarkerPreview extends Pick<EditorPlace, 'iconName' | 'markerColor'> {
  coordinate: EditorCoordinate | null;
  label: string;
  placeId: Guid | null;
}

export interface TripEditorMapOptions {
  onPlaceSelected?: (placeId: Guid) => boolean | Promise<boolean>;
  onSegmentSelected?: (key: SegmentPresentationKey) => boolean | Promise<boolean>;
}

export interface SelectPlaceOptions {
  focus?: boolean;
  openPopup?: boolean;
}

export const createTripEditorMap = (element: HTMLElement, tilesUrl: string, options: TripEditorMapOptions = {}): TripEditorMapAdapter => {
  const map = L.map(element, { zoomControl: true }).setView([20, 0], 2);
  const layers = L.layerGroup().addTo(map);
  const searchPreview = createSearchPreviewLayer(map);
  const placeDraftPreview = createPlaceDraftPreviewLayer(map);
  const coordinatePick = createPlaceCoordinatePickLayer(map, placeDraftPreview);
  const areaPolygonWork = createAreaPolygonWorkLayer(map);
  const segmentRouteWork = createSegmentRouteWorkLayer(map);
  const segmentDraftPreview = createSegmentRouteDraftPreviewLayer(map);
  const segmentPresentation = createSegmentPresentationLayer(map, key => options.onSegmentSelected?.(key) ?? true);
  const mapUtilities = createMapUtilitiesControl(element).addTo(map);
  const placeMarkers = new Map<Guid, L.Marker>();
  let activePlaceDraftPreview: PlaceDraftMarkerPreview | null = null;
  let lastRenderedState: EditorTripState | null = null;
  let lastHiddenSegmentIds: ReadonlySet<Guid> = new Set();
  const updateMapViewDataset = (): void => {
    const center = map.getCenter();
    element.dataset.tripEditorMapLat = center.lat.toFixed(6);
    element.dataset.tripEditorMapLng = center.lng.toFixed(6);
    element.dataset.tripEditorMapZoom = String(map.getZoom());
  };
  let selectedPlaceId: Guid | null = null;
  let activeSegmentKey: SegmentPresentationKey | null = null;
  let activeSegmentDraft: EditorSegmentDraftPresentation | null = null;
  let initialViewApplied = false;
  const prepareMapWork = (): void => { searchPreview.clear(); mapUtilities.cancelMeasure(); };

  map.on('moveend zoomend', updateMapViewDataset);

  // The shared layout supplies final provider-safe attribution for every map client.
  createTripEditorTileLayer(tilesUrl, {
    attribution: window.wayfarerTileConfig?.attribution,
    maxZoom: 19
  }).addTo(map);
  map.attributionControl.setPrefix('&copy; <a href="https://wayfarer.stefk.me" title="Powered by Wayfarer, made by Stef" target="_blank" rel="noopener">Wayfarer</a> | <a href="https://stefk.me" title="Check my blog" target="_blank" rel="noopener">Stef K</a> | &copy; <a href="https://leafletjs.com/" target="_blank" rel="noopener">Leaflet</a>');
  map.attributionControl.getContainer()?.setAttribute('aria-label', 'Map attribution');
  map.attributionControl.getContainer()?.setAttribute('title', 'Map attribution');

  const render = (state: EditorTripState, hiddenSegmentIds: ReadonlySet<Guid> = new Set(), nextSelectedPlaceId: Guid | null = selectedPlaceId): void => {
    lastRenderedState = state;
    lastHiddenSegmentIds = hiddenSegmentIds;
    searchPreview.clear();
    placeDraftPreview.clear();
    coordinatePick.clearRegisteredMarkers();
    areaPolygonWork.stop();
    layers.clearLayers();
    placeMarkers.clear();
    selectedPlaceId = nextSelectedPlaceId && state.placesById[nextSelectedPlaceId] ? nextSelectedPlaceId : null;

    Object.values(state.regionsById).forEach(region => renderRegion(region, layers));
    Object.values(state.areasById).forEach(area => renderArea(area, layers));
    Object.values(state.placesById).forEach(place => renderPlace(place, state, layers, coordinatePick, placeMarkers, () => {
      if (coordinatePick.isActive()) {
        return false;
      }

      return options.onPlaceSelected?.(place.id) ?? true;
    }));
    const presentations = Object.values(state.segmentsById)
      .filter(segment => !hiddenSegmentIds.has(segment.id) && activeSegmentDraft?.draft.id !== segment.id)
      .map(segment => resolvePersistedSegmentPresentation(segment, state));
    if (activeSegmentDraft && !hiddenSegmentIds.has(activeSegmentDraft.draft.id ?? '')) {
      presentations.push(resolveDraftSegmentPresentation(activeSegmentDraft, state));
    }
    segmentPresentation.render(presentations, activeSegmentKey);
    (window as typeof window & { __segmentPresentationSnapshot?: unknown }).__segmentPresentationSnapshot = segmentPresentation.snapshot();

    if (!initialViewApplied) {
      initialViewApplied = true;
      applyInitialMapView(map, state);
    }
    updateMapViewDataset();
    applySelectedPlaceMarker(placeMarkers, selectedPlaceId);
    applyActivePlaceDraftPreview(state);
    segmentDraftPreview.render(state, hiddenSegmentIds, segmentRouteWork.isActive());
  };

  const setSegmentDraftPreview = (state: EditorTripState, preview: SegmentDraftRoutePreview | null): void => {
    // The unified S/D/W registry owns the sole route representation; retain this API until callers migrate.
    segmentDraftPreview.set(null);
  };

  const applyActivePlaceDraftPreview = (state: EditorTripState): void => {
    const preview = activePlaceDraftPreview;
    if (!preview) {
      placeDraftPreview.clear();
      return;
    }

    if (preview.placeId) {
      placeMarkers.get(preview.placeId)?.remove();
    }
    // Preserve the authoritative marker instance and Pick listeners while draft styling rerenders.
    placeDraftPreview.show(preview.coordinate, preview.label, preview);
  };

  const setPlaceDraftPreview = (state: EditorTripState, preview: PlaceDraftMarkerPreview | null): void => {
    const previousPlaceId = activePlaceDraftPreview?.placeId;
    if (previousPlaceId) {
      const marker = placeMarkers.get(previousPlaceId);
      const place = state.placesById[previousPlaceId];
      if (marker && place?.location) {
        marker.setLatLng([place.location.latitude, place.location.longitude]);
        marker.setIcon(placeMarkerIcon(place));
        marker.addTo(layers);
      }
    }

    // Partial or invalid direct input cannot take coordinate ownership from the last complete pair.
    activePlaceDraftPreview = preview && !preview.coordinate && activePlaceDraftPreview?.coordinate && preview.placeId === previousPlaceId
      ? { ...preview, coordinate: activePlaceDraftPreview.coordinate }
      : preview;
    applyActivePlaceDraftPreview(state);
  };

  return {
    render,
    clearSearchPreview: searchPreview.clear,
    getMapView: () => {
      const center = map.getCenter();
      return { center: { latitude: center.lat, longitude: center.lng }, zoom: map.getZoom() };
    },
    selectPlace: (state, placeId, selectOptions = {}) => {
      selectedPlaceId = placeId && state.placesById[placeId] ? placeId : null;
      applySelectedPlaceMarker(placeMarkers, selectedPlaceId);
      if (!selectOptions.openPopup) {
        map.closePopup();
      }
      if (selectedPlaceId) {
        focusSelectedPlace(map, state, placeMarkers, selectedPlaceId, selectOptions);
      }
    },
    setPlaceDraftPreview,
    setSegmentDraftPreview,
    setSegmentPresentation: (state, key, draft) => {
      activeSegmentKey = key;
      activeSegmentDraft = draft;
      render(state, lastHiddenSegmentIds, selectedPlaceId);
    },
    startCoordinatePick: options => (prepareMapWork(), coordinatePick.start(options)),
    startAreaPolygonWork: options => (prepareMapWork(), areaPolygonWork.start(options)),
    startSegmentRouteWork: options => {
      prepareMapWork();
      if (lastRenderedState) {
        segmentDraftPreview.render(lastRenderedState, lastHiddenSegmentIds, true);
      }
      const stop = segmentRouteWork.start(options);
      return () => {
        stop();
        if (lastRenderedState) {
          segmentDraftPreview.render(lastRenderedState, lastHiddenSegmentIds, false);
        }
      };
    },
    setSegmentRouteWorkState: state => segmentRouteWork.setState(state),
    fitAllGeometry: state => fitAllGeometry(map, state),
    focusSavedTripView: metadata => focusSavedTripView(map, metadata),
    focusActiveEntity: (state, target) => focusActiveEntity(map, state, target),
    showSearchPreview: searchPreview.show,
    dispose: () => {
      searchPreview.dispose();
      placeDraftPreview.dispose();
      coordinatePick.dispose();
      areaPolygonWork.dispose();
      segmentRouteWork.dispose();
      segmentDraftPreview.dispose();
      segmentPresentation.dispose();
      mapUtilities.remove();
      map.off('moveend zoomend', updateMapViewDataset);
      map.remove();
    }
  };
};

const renderRegion = (region: EditorRegion, layers: LayerGroup): void => {
  if (!region.center) {
    return;
  }

  L.marker([region.center.latitude, region.center.longitude], {
    icon: regionMarkerIcon(region),
    interactive: !region.isShadow,
    keyboard: !region.isShadow,
    title: `${region.name} region center`,
    alt: `${region.name} region center`
  }).bindTooltip(escapeHtml(region.name)).addTo(layers);
};

const renderPlace = (
  place: EditorPlace,
  state: EditorTripState,
  layers: LayerGroup,
  coordinatePick: ReturnType<typeof createPlaceCoordinatePickLayer>,
  placeMarkers: Map<Guid, L.Marker>,
  onSelected: () => boolean | Promise<boolean>
): void => {
  if (!place.location) {
    return;
  }

  const marker = L.marker([place.location.latitude, place.location.longitude], {
    icon: placeMarkerIcon(place),
    title: place.name,
    alt: place.name
  });
  marker.on('click', async event => {
    if (event.originalEvent) {
      L.DomEvent.stop(event.originalEvent);
    }

    marker.closePopup();
    if (await onSelected()) {
      marker.openPopup();
    }
  });
  marker.bindPopup(placePopupHtml(place, state.regionsById[place.regionId]?.name), { className: 'trip-editor-place-popup' });
  // Leaflet auto-opens bound popups on marker click; selection must finish first so dirty-discard cancel keeps the old popup/halo.
  const popupMarker = marker as L.Marker & { _openPopup?: (event: LeafletMouseEvent) => void };
  if (popupMarker._openPopup) {
    marker.off('click', popupMarker._openPopup, marker);
  }
  coordinatePick.registerMarker(marker, place.location);
  marker.addTo(layers);
  placeMarkers.set(place.id, marker);
};

function applySelectedPlaceMarker(placeMarkers: Map<Guid, L.Marker>, selectedPlaceId: Guid | null): void {
  placeMarkers.forEach((marker, placeId) => {
    marker.getElement()?.classList.toggle('trip-editor-map-marker--selected', selectedPlaceId === placeId);
  });
}

function focusSelectedPlace(map: LeafletMap, state: EditorTripState, placeMarkers: Map<Guid, L.Marker>, placeId: Guid, options: SelectPlaceOptions): void {
  const place = state.placesById[placeId];
  if (!place?.location) {
    return;
  }

  if (options.focus) {
    map.setView([place.location.latitude, place.location.longitude], Math.max(map.getZoom(), 13));
  }

  if (options.openPopup) {
    placeMarkers.get(placeId)?.openPopup();
  }
}

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
  // The server-supplied effective route is authoritative for custom and waypoint fallback rendering.
  const coordinates = segment.effectiveRoute?.coordinates ?? segment.route?.coordinates ?? fallbackSegmentCoordinates(segment, state);
  if (!coordinates || coordinates.length < 2) {
    return;
  }

  const latLngs = coordinates.map(([longitude, latitude]) => [latitude, longitude] as [number, number]);
  const polyline = L.polyline(latLngs, {
    color: '#0ea5e9',
    weight: 3,
    opacity: 0.8
  }).bindTooltip(escapeHtml(segmentLabel(segment, state))).addTo(layers);
  const element = polyline.getElement();
  if (element) {
    element.dataset.segmentId = segment.id;
    element.dataset.routeOwner = 'saved';
    element.dataset.routeKind = segment.hasCustomRoute ? 'custom' : 'fallback';
  }
};

const fallbackSegmentCoordinates = (segment: EditorSegment, state: EditorTripState): Array<[number, number]> | null => {
  const from = segment.fromPlaceId ? state.placesById[segment.fromPlaceId]?.location : null;
  const to = segment.toPlaceId ? state.placesById[segment.toPlaceId]?.location : null;
  return from && to ? [[from.longitude, from.latitude], [to.longitude, to.latitude]] : null;
};

const applyInitialMapView = (map: LeafletMap, state: EditorTripState): InitialMapViewSource => {
  const urlView = readUrlMapView(window.location.search);
  if (urlView) {
    map.setView([urlView.center.latitude, urlView.center.longitude], urlView.zoom);
    return 'url';
  }

  if (focusSavedTripView(map, state.metadata) === 'moved') {
    return 'saved';
  }

  if (fitAllGeometry(map, state) === 'moved') {
    return 'fit-bounds';
  }

  return 'fallback';
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
  (segment.effectiveRoute?.coordinates ?? segment.route?.coordinates ?? fallbackSegmentCoordinates(segment, state))?.forEach(coordinate => extendLongitudeLatitude(bounds, coordinate));
};

const extendLongitudeLatitude = (bounds: L.LatLngBounds, [longitude, latitude]: [number, number]): void => {
  if (Number.isFinite(latitude) && Number.isFinite(longitude)) {
    bounds.extend([latitude, longitude]);
  }
};

const isFiniteCoordinate = (coordinate: EditorCoordinate): boolean =>
  Number.isFinite(coordinate.latitude) && Number.isFinite(coordinate.longitude);

const readUrlMapView = (search: string): { center: EditorCoordinate; zoom: number } | null => {
  const parameters = new URLSearchParams(search);
  const latitudeValue = parameters.get('lat');
  const longitudeValue = parameters.get('lng');
  const zoomValue = parameters.get('zoom');
  if (latitudeValue === null || longitudeValue === null || zoomValue === null) {
    return null;
  }

  const latitude = Number(latitudeValue);
  const longitude = Number(longitudeValue);
  const zoom = Number(zoomValue);
  if (!Number.isFinite(latitude) || !Number.isFinite(longitude) || !Number.isFinite(zoom) || zoom < 0 || zoom > 19) {
    return null;
  }

  const center = { latitude, longitude };
  return isFiniteCoordinate(center) ? { center, zoom } : null;
};

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
