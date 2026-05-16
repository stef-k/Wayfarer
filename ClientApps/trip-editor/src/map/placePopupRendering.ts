import { placeNotesPreviewHtml } from '../displayHelpers';
import type { EditorPlace } from '../types';

/// Builds sanitized place popup HTML from existing editor state only.
export function placePopupHtml(place: EditorPlace, regionName: string | null | undefined): string {
  const notesHtml = placeNotesPreviewHtml(place.notesHtml, 220);
  return [
    '<div class="trip-editor-place-popup__content">',
    '<div class="trip-editor-place-popup__header">',
    `<strong>${escapeHtml(place.name || 'Unnamed Place')}</strong>`,
    regionName ? `<span>${escapeHtml(regionName)}</span>` : '',
    '</div>',
    place.location ? `<div class="trip-editor-place-popup__meta"><span>Lat:</span> ${formatCoordinate(place.location.latitude)} <span>Lon:</span> ${formatCoordinate(place.location.longitude)}</div>` : '',
    place.address ? `<div class="trip-editor-place-popup__meta"><span>Address:</span> ${escapeHtml(place.address)}</div>` : '',
    visitSummaryHtml(place),
    notesHtml ? `<div class="trip-editor-place-popup__notes"><span>Notes:</span><div>${notesHtml}</div></div>` : '',
    '<div class="trip-editor-place-popup__footer">Click marker to select this place</div>',
    '</div>'
  ].join('');
}

function visitSummaryHtml(place: EditorPlace): string {
  if (!place.visitSummary.isVisited) {
    return '<div class="trip-editor-place-popup__meta"><span>Visits:</span> Not visited yet</div>';
  }

  const count = place.visitSummary.visitCount;
  return `<div class="trip-editor-place-popup__meta"><span>Visits:</span> ${escapeHtml(count === 1 ? '1 visit' : `${count} visits`)}</div>`;
}

function formatCoordinate(value: number): string {
  return Number.isFinite(value) ? value.toFixed(5) : '';
}

function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#039;');
}
