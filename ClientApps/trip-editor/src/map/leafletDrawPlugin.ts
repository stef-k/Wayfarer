import L from 'leaflet';
import { formatMapDistanceLabel } from '../../../../wwwroot/js/localization/mapMeasurementText.js';
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
type DrawGeometryUtil = { readableDistance: (...args: unknown[]) => string };

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

  const { drawLocal, GeometryUtil } = L as typeof L & { drawLocal: DrawLocale; GeometryUtil: DrawGeometryUtil };
  localizeLeafletDraw(drawLocal, GeometryUtil);
  isLoaded = true;
}

/** Localizes the Editor's drawing tooltips and measurements before creating the handlers. */
function localizeLeafletDraw(drawLocal: DrawLocale, geometry: DrawGeometryUtil): void {
  const text = document.createElement('span');
  // Leaflet.Draw inserts tooltip content as HTML.
  const tooltipHtml = (value: string): string => {
    text.textContent = value;
    return text.innerHTML;
  };
  const tooltipText = (key: string, fallback: string): string => tooltipHtml(message(key, fallback));

  const { polygon, polyline } = drawLocal.draw.handlers;
  polygon.tooltip = {
    start: tooltipText('Shell.DrawPolygonStart', 'Click to start drawing the area.'),
    cont: tooltipText('Shell.DrawPolygonContinue', 'Click to add another vertex.'),
    end: tooltipText('Shell.DrawPolygonFinish', 'Click the first vertex to close the area.')
  };
  polyline.tooltip = {
    start: tooltipText('Editor.DrawPolylineStart', 'Click to start drawing the route.'),
    cont: tooltipText('Editor.DrawPolylineContinue', 'Click to add another route point.'),
    end: tooltipText('Editor.DrawPolylineFinish', 'Click the last point to finish the route.')
  };
  // Leaflet.Draw shares this crossing error with polygon drawing and vertex editing.
  polyline.error = tooltipText('Shell.DrawEdgesCannotCross', 'Area edges cannot cross.');
  drawLocal.edit.handlers.edit.tooltip = {
    text: tooltipText('Shell.EditPolygonHandles', 'Drag the handles to edit the area.'),
    subtext: tooltipText('Shell.EditPolygonCancel', 'Click Cancel to undo changes.')
  };

  const parts = new Intl.NumberFormat(locale).formatToParts(12345.6);
  drawLocal.format = { numeric: { delimiters: {
    thousands: parts.find(part => part.type === 'group')?.value,
    decimal: parts.find(part => part.type === 'decimal')?.value
  } } };

  const readableDistance = geometry.readableDistance.bind(geometry);
  geometry.readableDistance = (...args) => {
    // Retain Leaflet.Draw's formatted number and its choice of metric unit.
    const label = readableDistance(...args);
    const metric = /^(.+) (km|m)$/.exec(label);
    return metric ? tooltipHtml(formatMapDistanceLabel(metric[1], metric[2])) : label;
  };
}

/** Detects the constructors used by the map-work layers. */
function hasLeafletDraw(): boolean {
  const leaflet = L as unknown as { Draw?: unknown; EditToolbar?: unknown };
  return Boolean(leaflet.Draw && leaflet.EditToolbar);
}
