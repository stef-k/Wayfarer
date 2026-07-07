import { createApp } from 'vue';
import App from './App.vue';
import type { ViewerMode } from './types';
import './styles.css';
import './shell.css';
import './parity.css';
import './responsive.css';

export type TripViewerMountConfig = {
  tripId: string;
  tripName: string;
  viewerMode: ViewerMode;
  viewerStateEndpoint: string;
  publicViewUrl: string | null;
  openCanonicalUrl: string | null;
  tilesUrl: string;
  tileAttribution: string;
  assetMode: 'development' | 'published';
};

const validViewerModes = new Set<ViewerMode>(['private', 'public', 'embed']);

const mountElement = document.getElementById('trip-viewer-app') ?? createFallbackMount();
const parsed = parseMountConfig(mountElement);

createApp(App, {
  config: parsed.config,
  configError: parsed.error
}).mount(mountElement);

// Creates a deterministic error host if the Razor shell did not emit the expected mount root.
function createFallbackMount(): HTMLElement {
  const element = document.createElement('div');
  element.id = 'trip-viewer-app';
  document.body.append(element);
  return element;
}

// Reads only server-emitted data attributes; viewer permissions come from the fetched state DTO.
function parseMountConfig(element: HTMLElement): { config: TripViewerMountConfig | null; error: string | null } {
  const tripId = element.dataset.tripId?.trim() ?? '';
  const tripName = element.dataset.tripName?.trim() ?? '';
  const viewerMode = element.dataset.viewerMode?.trim() ?? '';
  const viewerStateEndpoint = element.dataset.viewerStateEndpoint?.trim() ?? '';
  const tilesUrl = element.dataset.tilesUrl?.trim() ?? '';
  const tileAttribution = element.dataset.tileAttribution?.trim() ?? '';
  const assetMode = element.dataset.assetMode?.trim() ?? '';

  const errors: string[] = [];
  if (!tripId) errors.push('trip id');
  if (!tripName) errors.push('trip name');
  if (!validViewerModes.has(viewerMode as ViewerMode)) errors.push('viewer mode');
  if (!viewerStateEndpoint.startsWith('/')) errors.push('viewer state endpoint');
  if (!tilesUrl) errors.push('tile URL template');
  if (!tileAttribution) errors.push('tile attribution');
  if (assetMode !== 'development' && assetMode !== 'published') errors.push('asset mode');

  if (errors.length > 0) {
    return {
      config: null,
      error: `Trip Viewer mount config is missing or invalid: ${errors.join(', ')}.`
    };
  }

  return {
    config: {
      tripId,
      tripName,
      viewerMode: viewerMode as ViewerMode,
      viewerStateEndpoint,
      publicViewUrl: element.dataset.publicViewUrl?.trim() || null,
      openCanonicalUrl: element.dataset.openCanonicalUrl?.trim() || null,
      tilesUrl,
      tileAttribution,
      assetMode: assetMode as 'development' | 'published'
    },
    error: null
  };
}
