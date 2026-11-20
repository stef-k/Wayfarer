import { message, number } from '../../../../wwwroot/js/localization/messages.js';
import { placeNotesPreviewHtml, placeVisitCount } from '../displayHelpers';
import type { EditorPlace } from '../types';

/// Builds sanitized place popup HTML from existing editor state only.
export function placePopupHtml(place: EditorPlace, regionName: string | null | undefined): string {
  const notesHtml = placeNotesPreviewHtml(place.notesHtml, 220);
  const precision = precisionNotice(place.resolvedFeatureType);
  return [
    '<div class="trip-editor-place-popup__content">',
    '<div class="trip-editor-place-popup__header">',
    `<strong>${escapeHtml(place.name || message('Editor.UnnamedPlace', 'Unnamed Place'))}</strong>`,
    regionName ? `<span>${escapeHtml(regionName)}</span>` : '',
    '</div>',
    '<div class="trip-editor-place-popup__body">',
    place.location ? `<div class="trip-editor-place-popup__meta"><span>${escapeHtml(message('Shell.LatitudeShort', 'Lat:'))}</span> ${formatCoordinate(place.location.latitude)} <span>${escapeHtml(message('Shell.LongitudeShort', 'Lon:'))}</span> ${formatCoordinate(place.location.longitude)}</div>` : '',
    place.address ? `<div class="trip-editor-place-popup__meta"><span>${escapeHtml(message('Shell.AddressLabel', 'Address:'))}</span> ${escapeHtml(place.address)}</div>` : '',
    place.resolvedFeatureName ? `<div class="trip-editor-place-popup__meta">${escapeHtml(message('Editor.PlacePopupDetected', 'Detected place: {0}', place.resolvedFeatureName))}</div>` : '',
    precision ? `<div class="trip-editor-place-popup__meta" role="note">${escapeHtml(precision)}</div>` : '',
    visitSummaryHtml(place),
    notesHtml ? `<div class="trip-editor-place-popup__notes"><span>${escapeHtml(message('Shell.NotesLabel', 'Notes:'))}</span><div>${notesHtml}</div></div>` : '',
    '</div>',
    `<div class="trip-editor-place-popup__footer">${escapeHtml(message('Editor.PlacePopupSelectMarker', 'Click marker to select this place'))}</div>`,
    '</div>'
  ].join('');
}

/** Translates the precision category while preserving the provider's name and address. */
function precisionNotice(value: string | null | undefined): string {
  switch ((value || '').trim().toLowerCase()) {
    case 'postcode': return message('Editor.PlacePopupPostcode', 'Approximate address — resolved to postcode level.');
    case 'city': return message('Editor.PlacePopupCity', 'Approximate address — resolved to city level.');
    case 'suburb':
    case 'district': return message('Editor.PlacePopupLocalArea', 'Approximate address — resolved to local-area level.');
    case 'county':
    case 'state':
    case 'country': return message('Editor.PlacePopupRegion', 'Approximate address — resolved only to a regional level.');
    default: return '';
  }
}

/** Uses the same cardinal phrase as the Editor visit progress controls. */
function visitSummaryHtml(place: EditorPlace): string {
  const summary = place.visitSummary.isVisited
    ? placeVisitCount(place.visitSummary.visitCount)
    : message('Editor.PlacePopupNotVisited', 'Not visited yet');
  return `<div class="trip-editor-place-popup__meta"><span>${escapeHtml(message('Editor.PlacePopupVisits', 'Visits:'))}</span> ${escapeHtml(summary)}</div>`;
}

/** Formats popup coordinates for display with the existing five-decimal precision. */
function formatCoordinate(value: number): string {
  return Number.isFinite(value) ? number(value, { minimumFractionDigits: 5, maximumFractionDigits: 5, useGrouping: false }) : '';
}

function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#039;');
}
