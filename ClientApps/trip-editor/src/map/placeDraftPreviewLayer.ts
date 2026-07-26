import L, { type LeafletMouseEvent, type Map as LeafletMap } from 'leaflet';
import type { EditorCoordinate, EditorPlace } from '../types';
import { previewMarkerIcon } from './markerRendering';

export interface CoordinatePickOptions {
  initialCoordinate: EditorCoordinate | null;
  onPicked: (coordinate: EditorCoordinate) => void;
}

/// Renders the unsaved add-place location that must be visible before Save Place persists it.
export const createPlaceDraftPreviewLayer = (map: LeafletMap): {
  clear: () => void;
  dispose: () => void;
  setCoordinate: (coordinate: EditorCoordinate) => void;
  setPickMode: (active: boolean, onDragged?: (coordinate: EditorCoordinate) => void) => void;
  show: (coordinate: EditorCoordinate | null, label: string, preview: Pick<EditorPlace, 'iconName' | 'markerColor'>) => void;
} => {
  const layer = L.layerGroup().addTo(map);
  let marker: L.Marker | null = null;
  let dragHandler: (() => void) | null = null;
  let pickActive = false;
  let onMarkerDragged: ((coordinate: EditorCoordinate) => void) | undefined;
  let markerLabel = 'New place';
  let markerPreview: Pick<EditorPlace, 'iconName' | 'markerColor'> = { iconName: 'marker', markerColor: 'bg-blue' };

  const clear = (): void => {
    dragHandler?.();
    dragHandler = null;
    marker = null;
    layer.clearLayers();
  };

  const setCoordinate = (coordinate: EditorCoordinate): void => {
    if (!marker) {
      renderMarker(coordinate);
      return;
    }

    marker.setLatLng([coordinate.latitude, coordinate.longitude]);
    exposeCoordinate(coordinate);
  };

  const setPickMode = (active: boolean, onDragged?: (coordinate: EditorCoordinate) => void): void => {
    dragHandler?.();
    dragHandler = null;
    pickActive = active;
    onMarkerDragged = active ? onDragged : undefined;
    if (!marker) return;

    if (!active) {
      marker.dragging?.disable();
      return;
    }

    marker.dragging?.enable();
    const handleDragEnd = (): void => {
      const coordinate = marker!.getLatLng();
      onMarkerDragged?.({ latitude: coordinate.lat, longitude: coordinate.lng });
    };
    const stopMarkerClick = (event: LeafletMouseEvent): void => {
      if (event.originalEvent) L.DomEvent.stop(event.originalEvent);
    };
    marker.on('dragend', handleDragEnd);
    marker.on('click', stopMarkerClick);
    dragHandler = () => {
      marker?.off('dragend', handleDragEnd);
      marker?.off('click', stopMarkerClick);
    };
  };

  const renderMarker = (coordinate: EditorCoordinate): void => {
    const title = `Pending place location: ${markerLabel}`;
    marker = L.marker([coordinate.latitude, coordinate.longitude], {
      icon: previewMarkerIcon('place-draft', title, markerPreview),
      draggable: pickActive,
      interactive: true,
      keyboard: true,
      title,
      alt: title
    }).addTo(layer);
    exposeCoordinate(coordinate);
    if (pickActive) {
      setPickMode(true, onMarkerDragged);
    }
  };

  const show = (coordinate: EditorCoordinate | null, label: string, preview: Pick<EditorPlace, 'iconName' | 'markerColor'>): void => {
    markerLabel = label;
    markerPreview = preview;
    if (!coordinate) {
      return;
    }
    if (!marker) {
      renderMarker(coordinate);
      return;
    }

    marker.setLatLng([coordinate.latitude, coordinate.longitude]);
    marker.setIcon(previewMarkerIcon('place-draft', `Pending place location: ${label}`, preview));
    exposeCoordinate(coordinate);
  };

  const exposeCoordinate = (coordinate: EditorCoordinate): void => {
    const element = marker?.getElement();
    if (element) {
      element.dataset.placeDraftLatitude = String(coordinate.latitude);
      element.dataset.placeDraftLongitude = String(coordinate.longitude);
    }
  };

  return {
    clear,
    dispose: () => {
      setPickMode(false);
      clear();
    },
    setCoordinate,
    setPickMode,
    show
  };
};

/// Coordinates map clicks, persisted-marker clicks, and draft-marker drags through one pending coordinate owner.
export const createPlaceCoordinatePickLayer = (
  map: LeafletMap,
  draftPreview: ReturnType<typeof createPlaceDraftPreviewLayer>
): {
  clearRegisteredMarkers: () => void;
  dispose: () => void;
  isActive: () => boolean;
  pick: (coordinate: EditorCoordinate) => void;
  registerMarker: (marker: L.Marker, coordinate: EditorCoordinate) => void;
  start: (options: CoordinatePickOptions) => () => void;
  stop: () => void;
} => {
  let clickHandler: ((event: LeafletMouseEvent) => void) | null = null;
  let onPicked: ((coordinate: EditorCoordinate) => void) | null = null;
  let previousCursor: string | null = null;
  const markers: Array<{ marker: L.Marker; coordinate: EditorCoordinate }> = [];
  const markerListeners: Array<() => void> = [];

  const setPreview = (coordinate: EditorCoordinate): void => draftPreview.setCoordinate(coordinate);
  const pick = (coordinate: EditorCoordinate): void => {
    setPreview(coordinate);
    onPicked?.(coordinate);
  };
  const detachMarkerListeners = (): void => {
    markerListeners.splice(0).forEach(detach => detach());
  };
  const stop = (): void => {
    if (clickHandler) {
      map.off('click', clickHandler);
      clickHandler = null;
    }
    onPicked = null;
    if (previousCursor !== null) {
      map.getContainer().style.cursor = previousCursor;
      previousCursor = null;
    }
    detachMarkerListeners();
    draftPreview.setPickMode(false);
  };
  const attachMarkerListener = (marker: L.Marker, coordinate: EditorCoordinate): void => {
    const handler = (event: LeafletMouseEvent): void => {
      if (event.originalEvent) L.DomEvent.stop(event.originalEvent);
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
  const registerMarker = (marker: L.Marker, coordinate: EditorCoordinate): void => {
    markers.push({ marker, coordinate });
    if (clickHandler) attachMarkerListener(marker, coordinate);
  };
  const clearRegisteredMarkers = (): void => {
    detachMarkerListeners();
    markers.splice(0);
  };
  const start = (options: CoordinatePickOptions): (() => void) => {
    stop();
    onPicked = options.onPicked;
    previousCursor = map.getContainer().style.cursor;
    map.getContainer().style.cursor = 'default';
    attachMarkerListeners();
    draftPreview.setPickMode(true, pick);
    if (options.initialCoordinate) setPreview(options.initialCoordinate);
    clickHandler = event => pick({ latitude: event.latlng.lat, longitude: event.latlng.lng });
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
