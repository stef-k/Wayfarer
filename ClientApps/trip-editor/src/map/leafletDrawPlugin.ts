import L from 'leaflet';
import leafletDrawSource from '../../../../wwwroot/lib/leaflet/leaflet.draw.js?raw';
import '../../../../wwwroot/lib/leaflet/leaflet.draw.css';

declare global {
  interface Window {
    L?: typeof L;
  }
}

let isLoaded = false;

/// Loads the repo-local Leaflet.Draw UMD asset into the Vite-bundled Leaflet instance.
export function ensureLeafletDraw(): void {
  if (isLoaded && hasLeafletDraw()) {
    return;
  }

  window.L = L;
  Function('window', 'document', leafletDrawSource)(window, document);
  if (!hasLeafletDraw()) {
    throw new Error('Leaflet.Draw failed to initialize for Trip Editor area map-work.');
  }

  isLoaded = true;
}

function hasLeafletDraw(): boolean {
  const leaflet = L as unknown as { Draw?: unknown; EditToolbar?: unknown };
  return Boolean(leaflet.Draw && leaflet.EditToolbar);
}
