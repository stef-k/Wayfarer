import L from 'leaflet';
import { message } from '../../../../wwwroot/js/localization/messages.js';
import 'leaflet/dist/leaflet.css';
import { createTripEditorTileLayer } from './tileRetryLayer';

/** Base map shared by ordinary editing and comparison, independent of their tools and overlays. */
export const createTripMap = (element: HTMLElement, tilesUrl = window.wayfarerTileConfig?.tilesUrl ?? '/Public/tiles/{z}/{x}/{y}.png'): L.Map => {
  const map = L.map(element, { zoomControl: false }).setView([20, 0], 2);
  L.control.zoom({
    zoomInTitle: message('Shell.ZoomIn', 'Zoom in'), zoomOutTitle: message('Shell.ZoomOut', 'Zoom out')
  }).addTo(map);
  // The shared layout supplies final provider-safe attribution for every map client.
  createTripEditorTileLayer(tilesUrl, {
    attribution: window.wayfarerTileConfig?.attribution,
    maxZoom: 19
  }).addTo(map);
  map.attributionControl.setPrefix('&copy; <a href="https://wayfarer.stefk.me" title="Powered by Wayfarer, made by Stef" target="_blank" rel="noopener">Wayfarer</a> | <a href="https://stefk.me" title="Check my blog" target="_blank" rel="noopener">Stef K</a> | &copy; <a href="https://leafletjs.com/" target="_blank" rel="noopener">Leaflet</a>');
  map.attributionControl.getContainer()?.setAttribute('aria-label', message('Shell.MapAttribution', 'Map attribution'));
  map.attributionControl.getContainer()?.setAttribute('title', message('Shell.MapAttribution', 'Map attribution'));

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
