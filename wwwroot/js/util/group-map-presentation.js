import { message, number } from '../localization/messages.js';
import { locationAddressText, renderLocationAddress } from './location-address.js';
import { encodeFeatureText as groupHtml } from './feature-metadata.js';
import { notesHtmlForDisplay } from './notes-images.js';
import { groupDateTime, groupMessage as t } from './group-web-messages.js';
import { installMapAttribution } from './map-attribution.js';

const sourceZone = location => location.timezone || location.timeZoneId || location.timeZone;
const sourceTime = location => sourceZone(location) ? groupDateTime(location.localTimestamp, sourceZone(location)) : '';
const address = location => location.fullAddress || location.address || location.place || '';

/** The same Leaflet controls and attribution are used in both Group maps. */
export const setupGroupMapControls = map => {
  L.control.zoom({
    zoomInTitle: message('Shell.ZoomIn', 'Zoom in'),
    zoomOutTitle: message('Shell.ZoomOut', 'Zoom out'),
  }).addTo(map);
  installMapAttribution(map);
};

/** Formats a map tooltip with authored identity and address as text. */
export const groupLocationTooltip = (info, location) => {
  const name = info.display ? `${info.username || ''} (${info.display})` : info.username || '';
  const local = sourceTime(location);
  const recorded = local || t('RecordedUnavailable', 'Recorded time unavailable');
  const recordedLine = !local && sourceZone(location) ? `${recorded} (${sourceZone(location)})` : recorded;
  return `${groupHtml(name)}<br/>${groupHtml(groupDateTime(location.localTimestamp))}<br/>
    ${groupHtml(t('RecordedTooltip', 'Recorded: {0}', recordedLine))}<br/>${groupHtml(locationAddressText(location, address(location)))}`;
};

const googleMapsLink = location => {
  const addr = locationAddressText(location, address(location));
  const lat = location.coordinates.latitude;
  const lon = location.coordinates.longitude;
  const coordinates = `${lat.toFixed(6)},${lon.toFixed(6)}`;
  const query = encodeURIComponent(addr ? `${addr} (${coordinates})` : coordinates);
  return `<a href="https://www.google.com/maps/search/?api=1&query=${query}" target="_blank" rel="noopener"
    class="ms-2 btn btn-outline-primary btn-sm" title="${groupHtml(t('GoogleMaps', 'View in Google Maps'))}">
    <i class="bi bi-globe-europe-africa" aria-hidden="true"></i> ${groupHtml(t('Maps', 'Maps'))}</a>`;
};

/** Historical markers and current markers render the recorded time and their actual status. */
export const groupLocationDetails = (location, status) => {
  const statuses = {
    live: ['danger', t('LiveLocation', 'Live Location')],
    latest: ['success', t('LatestLocation', 'Latest Location')],
    history: ['secondary', t('HistoricalLocation', 'Historical Location')],
  };
  const [color, label] = statuses[status];
  const local = sourceTime(location);
  const recorded = local || t('SourceZoneUnavailable', 'Source timezone unavailable');
  const zone = !local && sourceZone(location) ? `<div class="small text-muted">${groupHtml(sourceZone(location))}</div>` : '';
  const notes = notesHtmlForDisplay(location.notes);
  return `<div class="container-fluid">
    <div class="row mb-2"><div class="col-12"><span class="badge bg-${color} float-end ms-2">${groupHtml(label)}</span></div></div>
    <div class="row mb-2">
      <div class="col-6"><strong>${groupHtml(t('ViewerTime', 'Date and time in your timezone'))}</strong><div>${groupHtml(groupDateTime(location.localTimestamp))}</div></div>
      <div class="col-6"><strong>${groupHtml(t('RecordedTime', 'Recorded local time'))}</strong><div>${groupHtml(recorded)}</div>${zone}</div>
    </div>
    <div class="row mb-2">${renderLocationAddress(location, address(location))}${googleMapsLink(location)}</div>
    <div class="row mb-2">
      <div class="col-6"><strong>${groupHtml(t('Latitude', 'Latitude'))}</strong> <span class="fw-bold text-primary">${number(location.coordinates.latitude, { maximumFractionDigits: 20 })}</span></div>
      <div class="col-6"><strong>${groupHtml(t('Longitude', 'Longitude'))}</strong> <span class="fw-bold text-primary">${number(location.coordinates.longitude, { maximumFractionDigits: 20 })}</span></div>
    </div>
    ${notes ? `<div class="row mb-2"><div class="col-12"><strong>${groupHtml(message('Shell.NotesLabel', 'Notes:'))}</strong><div class="border p-1">${notes}</div></div></div>` : ''}
  </div>`;
};

/** Updates just the map's read feedback, leaving the surrounding alert state alone. */
export const setGroupMapStatus = (id, text = '', error = false) => {
  const element = document.getElementById(id);
  element.textContent = text;
  element.hidden = !text;
  element.classList.toggle('text-danger', error);
};
