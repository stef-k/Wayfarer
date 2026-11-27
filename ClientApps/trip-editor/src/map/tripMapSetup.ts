import L from 'leaflet';
import { message } from '../../../../wwwroot/js/localization/messages.js';
import { installMapAttribution } from '../../../../wwwroot/js/util/map-attribution.js';
import 'leaflet/dist/leaflet.css';
import { createTripEditorTileLayer } from './tileRetryLayer';

/** Base map shared by ordinary editing and comparison, independent of their tools and overlays. */
export const createTripMap = (element: HTMLElement, tilesUrl = window.wayfarerTileConfig?.tilesUrl ?? '/Public/tiles/{z}/{x}/{y}.png',
  options: Pick<L.MapOptions, 'trackResize'> = {}): L.Map => {
  const map = L.map(element, { zoomControl: false, ...options }).setView([20, 0], 2);
  L.control.zoom({
    zoomInTitle: message('Shell.ZoomIn', 'Zoom in'), zoomOutTitle: message('Shell.ZoomOut', 'Zoom out')
  }).addTo(map);
  // The shared layout supplies final provider-safe attribution for every map client.
  createTripEditorTileLayer(tilesUrl, {
    attribution: window.wayfarerTileConfig?.attribution,
    maxZoom: 19
  }).addTo(map);
  installMapAttribution(map);

  return map;
};

declare global {
  interface Window {
    wayfarerTileConfig?: {
      tilesUrl?: string;
      attribution?: string;
      burstCapacity?: number;
      retryAfterSeconds?: number;
    };
  }
}
