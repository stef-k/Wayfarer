import L from 'leaflet';
import { placeMarkerIconUrl, placeMarkerLabel } from '../displayHelpers';
import type { EditorPlace, EditorRegion } from '../types';

const markerWidth = 28;
const markerHeight = 45;

/// Builds app marker icons from static Wayfarer PNGs so Leaflet default image paths are never used.
export function placeMarkerIcon(place: EditorPlace): L.DivIcon {
  const visitBadge = place.visitSummary.isVisited
    ? `<span class="trip-editor-map-marker__badge" title="${escapeHtml(place.visitSummary.visitCount === 1 ? 'Visited' : `Visited ${place.visitSummary.visitCount} times`)}">${escapeHtml(place.visitSummary.visitCount === 1 ? '✓' : String(place.visitSummary.visitCount))}</span>`
    : '';

  return appMarkerIcon({
    className: 'trip-editor-map-marker',
    imageClassName: 'trip-editor-map-marker__image',
    src: placeMarkerIconUrl(place.iconName, place.markerColor),
    alt: placeMarkerLabel(place),
    dataAttribute: `data-place-marker-icon="${escapeHtml(place.id)}"`,
    style: `--trip-editor-selected-marker-color: ${markerSelectionColor(place.markerColor)}`,
    extraHtml: visitBadge,
    iconSize: [36, 50],
    iconAnchor: [18, 50],
    popupAnchor: [0, -45]
  });
}

/// Builds the legacy-equivalent red map marker used for region centers.
export function regionMarkerIcon(region: EditorRegion): L.DivIcon {
  return appMarkerIcon({
    className: 'trip-editor-map-marker trip-editor-map-marker--region',
    imageClassName: 'trip-editor-map-marker__image',
    src: placeMarkerIconUrl('map', 'bg-red'),
    alt: `${region.name} region center`,
    dataAttribute: `data-region-marker-icon="${escapeHtml(region.id)}"`,
    iconSize: [markerWidth, markerHeight],
    iconAnchor: [14, 45],
    popupAnchor: [0, -45]
  });
}

/// Builds temporary preview markers for coordinate picking and map search.
export function previewMarkerIcon(kind: 'coordinate' | 'search', label: string): L.DivIcon {
  return appMarkerIcon({
    className: `trip-editor-map-marker trip-editor-map-marker--preview trip-editor-map-marker--preview-${kind}`,
    imageClassName: 'trip-editor-map-marker__image',
    src: placeMarkerIconUrl('marker', 'bg-blue'),
    alt: label,
    dataAttribute: `data-${kind}-preview-marker="true"`,
    iconSize: [markerWidth, markerHeight],
    iconAnchor: [14, 45],
    popupAnchor: [0, -45]
  });
}

interface AppMarkerIconOptions {
  alt: string;
  className: string;
  dataAttribute: string;
  extraHtml?: string;
  iconAnchor: [number, number];
  iconSize: [number, number];
  imageClassName: string;
  popupAnchor: [number, number];
  src: string;
  style?: string;
}

function appMarkerIcon(options: AppMarkerIconOptions): L.DivIcon {
  const style = options.style ? ` style="${escapeHtml(options.style)}"` : '';
  return L.divIcon({
    className: options.className,
    html: `<span class="trip-editor-map-marker__halo" aria-hidden="true"></span><img class="${options.imageClassName}" src="${options.src}" width="${markerWidth}" height="${markerHeight}" alt="${escapeHtml(options.alt)}"${style} ${options.dataAttribute}>${options.extraHtml ?? ''}`,
    iconSize: options.iconSize,
    iconAnchor: options.iconAnchor,
    popupAnchor: options.popupAnchor
  });
}

function markerSelectionColor(markerColor: string | null | undefined): string {
  return ({
    'bg-blue': '#0d6efd',
    'bg-cyan': '#0dcaf0',
    'bg-red': '#dc3545',
    'bg-green': '#198754',
    'bg-indigo': '#6610f2',
    'bg-yellow': '#ffc107',
    'bg-orange': '#fd7e14',
    'bg-purple': '#6f42c1',
    'bg-pink': '#d63384',
    'bg-teal': '#20c997',
    'bg-gray': '#6c757d',
    'bg-black': '#212529',
    'bg-white': '#adb5bd'
  })[markerColor ?? ''] ?? '#0d6efd';
}

function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#039;');
}
