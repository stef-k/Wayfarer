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
import { allGeometryBounds, applyInitialMapView, fallbackSegmentCoordinates, fitBounds, focusActiveEntity, focusSavedTripView,
  type FitAllGeometryResult, type FocusActiveEntityResult, type FocusSavedTripViewResult } from './mapNavigation';
import { createMapViewport, type TripEditorMapView } from './mapViewport';
export type { AreaPolygonWorkOptions } from './areaPolygonWorkLayer';
export type { CoordinatePickOptions } from './placeDraftPreviewLayer';
export type { SegmentRouteWorkOptions } from './segmentRouteWorkLayer';
export type { SegmentDraftRoutePreview } from './segmentRouteDraftPreviewLayer';

export { canFocusActiveEntity, hasAnyGeometry, hasSavedTripView } from './mapNavigation';
export type { FocusActiveEntityResult } from './mapNavigation';
export type { TripEditorMapView } from './mapViewport';

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

export interface PlaceDraftMarkerPreview extends Pick<EditorPlace, 'iconName' | 'markerColor'> {
  coordinate: EditorCoordinate | null;
  label: string;
  placeId: Guid | null;
}

export interface TripEditorMapOptions {
  /** Capture is permitted only for a recognized gesture outside map-work. */
  onMapViewCaptured?: (view: TripEditorMapView) => void;
  canCaptureTripView?: () => boolean;
  onPlaceSelected?: (placeId: Guid) => boolean | Promise<boolean>;
  onSegmentSelected?: (key: SegmentPresentationKey) => boolean | Promise<boolean>;
}

export interface SelectPlaceOptions {
  focus?: boolean;
  openPopup?: boolean;
}

export const createTripEditorMap = (element: HTMLElement, tilesUrl: string, options: TripEditorMapOptions = {}): TripEditorMapAdapter => {
  const map = L.map(element, { zoomControl: true }).setView([20, 0], 2);
  const viewport = createMapViewport(map, {
    onCaptured: options.onMapViewCaptured,
    canCapture: () => options.canCaptureTripView?.() ?? true
  });
  const layers = L.layerGroup().addTo(map);
  const searchPreview = createSearchPreviewLayer(map);
  const placeDraftPreview = createPlaceDraftPreviewLayer(map);
  const coordinatePick = createPlaceCoordinatePickLayer(map, placeDraftPreview);
  const areaPolygonWork = createAreaPolygonWorkLayer(map);
  const segmentRouteWork = createSegmentRouteWorkLayer(map);
  const segmentPresentation = createSegmentPresentationLayer(map, key => options.onSegmentSelected?.(key) ?? true);
  const segmentDraftPreview = createSegmentRouteDraftPreviewLayer(map, segmentPresentation.setProposalEmphasis);
  const mapUtilities = createMapUtilitiesControl(element).addTo(map);
  const placeMarkers = new Map<Guid, L.Marker>();
  let activePlaceDraftPreview: PlaceDraftMarkerPreview | null = null;
  let lastRenderedState: EditorTripState | null = null;
  let lastHiddenSegmentIds: ReadonlySet<Guid> = new Set();
  let selectedPlaceId: Guid | null = null;
  let activeSegmentKey: SegmentPresentationKey | null = null;
  let activeSegmentDraft: EditorSegmentDraftPresentation | null = null;
  let initialViewApplied = false;
  const prepareMapWork = (): void => { searchPreview.clear(); mapUtilities.cancelMeasure(); };

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
      viewport.initialize(() => applyInitialMapView(map, state));
    }
    applySelectedPlaceMarker(placeMarkers, selectedPlaceId);
    applyActivePlaceDraftPreview(state);
    segmentDraftPreview.render(state, hiddenSegmentIds, segmentRouteWork.isActive());
  };

  const setSegmentDraftPreview = (state: EditorTripState, preview: SegmentDraftRoutePreview | null): void => {
    segmentDraftPreview.set(preview); // Ordinary drafts remain owned by the unified S/D/W registry.
    segmentDraftPreview.render(state, lastHiddenSegmentIds, segmentRouteWork.isActive());
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
    getMapView: viewport.getView,
    selectPlace: (state, placeId, selectOptions = {}) => {
      selectedPlaceId = placeId && state.placesById[placeId] ? placeId : null;
      applySelectedPlaceMarker(placeMarkers, selectedPlaceId);
      if (!selectOptions.openPopup) {
        map.closePopup();
      }
      if (selectedPlaceId) {
        viewport.navigate(() => focusSelectedPlace(map, state, placeMarkers, selectedPlaceId!, selectOptions));
      }
    },
    setPlaceDraftPreview,
    setSegmentDraftPreview,
    setSegmentPresentation: (state, key, draft) => {
      activeSegmentKey = key;
      activeSegmentDraft = draft;
      render(state, lastHiddenSegmentIds, selectedPlaceId);
    },
    startCoordinatePick: options => viewport.navigate(() => (prepareMapWork(), coordinatePick.start(options))),
    startAreaPolygonWork: options => viewport.navigate(() => (prepareMapWork(), areaPolygonWork.start(options))),
    startSegmentRouteWork: options => {
      prepareMapWork();
      if (lastRenderedState) {
        segmentDraftPreview.render(lastRenderedState, lastHiddenSegmentIds, true);
      }
      const stop = viewport.navigate(() => segmentRouteWork.start(options));
      return () => {
        stop();
        if (lastRenderedState) {
          segmentDraftPreview.render(lastRenderedState, lastHiddenSegmentIds, false);
        }
      };
    },
    setSegmentRouteWorkState: state => segmentRouteWork.setState(state),
    fitAllGeometry: state => viewport.navigate(() => fitBounds(map, segmentDraftPreview.extendBounds(allGeometryBounds(state)))),
    focusSavedTripView: metadata => viewport.navigate(() => focusSavedTripView(map, metadata)),
    focusActiveEntity: (state, target) => viewport.navigate(() => segmentDraftPreview.focus(target) ?? focusActiveEntity(map, state, target)),
    showSearchPreview: (coordinate, label) => viewport.navigate(() => searchPreview.show(coordinate, label)),
    dispose: () => {
      searchPreview.dispose();
      placeDraftPreview.dispose();
      coordinatePick.dispose();
      areaPolygonWork.dispose();
      segmentRouteWork.dispose();
      segmentDraftPreview.dispose();
      segmentPresentation.dispose();
      mapUtilities.remove();
      viewport.dispose();
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
    element.setAttribute('data-segment-id', segment.id);
    element.setAttribute('data-route-owner', 'saved');
    element.setAttribute('data-route-kind', segment.hasCustomRoute ? 'custom' : 'fallback');
  }
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
      burstCapacity?: number;
      retryAfterSeconds?: number;
    };
  }
}
