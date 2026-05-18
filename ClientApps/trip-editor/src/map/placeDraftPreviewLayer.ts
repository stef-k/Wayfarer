import L, { type Map as LeafletMap } from 'leaflet';
import type { EditorCoordinate, EditorPlace } from '../types';
import { previewMarkerIcon } from './markerRendering';

/// Renders the unsaved add-place location that must be visible before Save Place persists it.
export const createPlaceDraftPreviewLayer = (map: LeafletMap): {
  clear: () => void;
  dispose: () => void;
  show: (coordinate: EditorCoordinate, label: string, preview: Pick<EditorPlace, 'iconName' | 'markerColor'>) => void;
} => {
  const layer = L.layerGroup().addTo(map);

  const clear = (): void => {
    layer.clearLayers();
  };

  const show = (coordinate: EditorCoordinate, label: string, preview: Pick<EditorPlace, 'iconName' | 'markerColor'>): void => {
    const title = `Pending place location: ${label}`;
    clear();
    L.marker([coordinate.latitude, coordinate.longitude], {
      icon: previewMarkerIcon('place-draft', title, preview),
      interactive: false,
      keyboard: false,
      title,
      alt: title
    }).addTo(layer);
    map.setView([coordinate.latitude, coordinate.longitude], Math.max(map.getZoom(), 13));
  };

  return {
    clear,
    dispose: clear,
    show
  };
};
