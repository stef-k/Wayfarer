import L from 'leaflet';
import type { ViewerCoordinate, ViewerPlace } from './types';

const iconBasePath = '/icons/wayfarer-map-icons/dist/png/marker';
const markerSelectionColors: Record<string, string> = {
  'bg-blue': '#0d6efd',
  'bg-red': '#dc3545',
  'bg-green': '#198754',
  'bg-purple': '#6f42c1',
  'bg-black': '#212529'
};

export function toLatLng(coordinate: ViewerCoordinate): L.LatLngExpression {
  return [coordinate.latitude, coordinate.longitude];
}

export function placeMarkerIcon(place: ViewerPlace, selected: boolean): L.DivIcon {
  const visitBadge = place.visitSummary.isVisited
    ? `<span class="trip-viewer-map-marker__badge">${escapeHtml(place.visitSummary.visitCount === 1 ? '✓' : String(place.visitSummary.visitCount))}</span>`
    : '';

  return appMarkerIcon({
    alt: place.visitSummary.isVisited ? `${place.name}, visited ${place.visitSummary.visitCount} time(s)` : place.name,
    className: `trip-viewer-map-marker${selected ? ' trip-viewer-map-marker--selected' : ''}`,
    src: markerIconUrl(place.iconName, place.markerColor),
    style: `--trip-viewer-marker-selected-color: ${markerSelectionColors[place.markerColor] ?? '#0d6efd'}`,
    extraHtml: visitBadge
  });
}

export function regionMarkerIcon(name: string, selected: boolean): L.DivIcon {
  return appMarkerIcon({
    alt: `${name} region center`,
    className: `trip-viewer-map-marker trip-viewer-map-marker--region${selected ? ' trip-viewer-map-marker--selected' : ''}`,
    src: markerIconUrl('map', 'bg-red')
  });
}

export function markerIconUrl(iconName: string | null | undefined, markerColor: string | null | undefined): string {
  return `${iconBasePath}/${safePathSegment(markerColor, 'bg-blue')}/${safePathSegment(iconName, 'marker')}.png`;
}

export function popupHtml(title: string, type: string, preview: string, entityType: string, entityId: string): string {
  const body = preview ? `<p>${escapeHtml(preview)}</p>` : '';
  return `<div class="trip-viewer-popup">
    <strong>${escapeHtml(title)}</strong>
    <span>${escapeHtml(type)}</span>
    ${body}
    <button type="button" class="trip-viewer-popup__button" data-trip-viewer-select="${escapeHtml(entityType)}:${escapeHtml(entityId)}">View details</button>
  </div>`;
}

function appMarkerIcon(options: { alt: string; className: string; extraHtml?: string; src: string; style?: string }): L.DivIcon {
  const style = options.style ? ` style="${escapeHtml(options.style)}"` : '';
  return L.divIcon({
    className: options.className,
    html: `<img class="trip-viewer-map-marker__image" src="${options.src}" width="28" height="45" alt="${escapeHtml(options.alt)}"${style}>${options.extraHtml ?? ''}`,
    iconSize: [36, 50],
    iconAnchor: [18, 50],
    popupAnchor: [0, -45]
  });
}

function safePathSegment(value: string | null | undefined, fallback: string): string {
  return encodeURIComponent(value?.trim() || fallback);
}

function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#039;');
}
