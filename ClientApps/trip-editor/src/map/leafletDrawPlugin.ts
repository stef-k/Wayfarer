import L from 'leaflet';
import { locale, message } from '../../../../wwwroot/js/localization/messages.js';
import leafletDrawSource from '../../../../wwwroot/lib/leaflet/leaflet.draw.js?raw';
import '../../../../wwwroot/lib/leaflet/leaflet.draw.css';

declare global {
  interface Window {
    L?: typeof L;
  }
}

let isLoaded = false;

type DrawTooltip = { start: string; cont: string; end: string };
type DrawLocale = {
  draw: { handlers: { polygon: { tooltip: DrawTooltip }; polyline: { tooltip: DrawTooltip; error: string } } };
  edit: { handlers: { edit: { tooltip: { text: string; subtext: string } } } };
  format?: { numeric: { delimiters: { thousands?: string; decimal?: string } } };
};

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

  localizeLeafletDraw((L as typeof L & { drawLocal: DrawLocale }).drawLocal);
  isLoaded = true;
}

/** Sets the hooks used by the Editor's draw and polygon editing handlers before they are created. */
function localizeLeafletDraw(drawLocal: DrawLocale): void {
  const text = document.createElement('span');
  // Leaflet.Draw renders these hooks as HTML, so serialize each catalog entry as text.
  const tooltipText = (key: string, fallback: string): string => {
    text.textContent = message(key, fallback);
    return text.innerHTML;
  };

  const { polygon, polyline } = drawLocal.draw.handlers;
  polygon.tooltip = {
    start: tooltipText('Editor.DrawPolygonStart', 'Click to start drawing the area.'),
    cont: tooltipText('Editor.DrawPolygonContinue', 'Click to add another vertex.'),
    end: tooltipText('Editor.DrawPolygonFinish', 'Click the first vertex to close the area.')
  };
  polyline.tooltip = {
    start: tooltipText('Editor.DrawPolylineStart', 'Click to start drawing the route.'),
    cont: tooltipText('Editor.DrawPolylineContinue', 'Click to add another route point.'),
    end: tooltipText('Editor.DrawPolylineFinish', 'Click the last point to finish the route.')
  };
  // Leaflet.Draw shares this crossing error with polygon drawing and vertex editing.
  polyline.error = tooltipText('Editor.DrawEdgesCannotCross', 'Area edges cannot cross.');
  drawLocal.edit.handlers.edit.tooltip = {
    text: tooltipText('Editor.EditPolygonHandles', 'Drag the handles to edit the area.'),
    subtext: tooltipText('Editor.EditPolygonCancel', 'Click Cancel to undo changes.')
  };

  const parts = new Intl.NumberFormat(locale).formatToParts(12345.6);
  drawLocal.format = { numeric: { delimiters: {
    thousands: parts.find(part => part.type === 'group')?.value,
    decimal: parts.find(part => part.type === 'decimal')?.value
  } } };
}

/** Detects the constructors used by the map-work layers. */
function hasLeafletDraw(): boolean {
  const leaflet = L as unknown as { Draw?: unknown; EditToolbar?: unknown };
  return Boolean(leaflet.Draw && leaflet.EditToolbar);
}
