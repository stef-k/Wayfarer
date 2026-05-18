import L, { type Map as LeafletMap } from 'leaflet';
import type { EditorCoordinate } from '../types';
import { previewMarkerIcon } from './markerRendering';

/// Renders the transient marker for a selected geosearch result before it is opened as a place draft.
export const createSearchPreviewLayer = (map: LeafletMap): {
  clear: () => void;
  dispose: () => void;
  show: (coordinate: EditorCoordinate, label: string) => void;
} => {
  const layer = L.layerGroup().addTo(map);

  const clear = (): void => {
    layer.clearLayers();
  };

  const show = (coordinate: EditorCoordinate, label: string): void => {
    const title = `Search result preview: ${label}`;
    clear();
    L.marker([coordinate.latitude, coordinate.longitude], {
      icon: previewMarkerIcon('search', title),
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
