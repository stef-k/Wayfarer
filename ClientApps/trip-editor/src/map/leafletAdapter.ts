import L, { type LayerGroup, type Map as LeafletMap } from 'leaflet';
import 'leaflet/dist/leaflet.css';
import type { EditorArea, EditorPlace, EditorRegion, EditorSegment, EditorTripState } from '../types';

interface TripEditorMapAdapter {
  render: (state: EditorTripState) => void;
  dispose: () => void;
}

export const createTripEditorMap = (element: HTMLElement, tilesUrl: string): TripEditorMapAdapter => {
  const map = L.map(element, { zoomControl: true }).setView([20, 0], 2);
  const layers = L.layerGroup().addTo(map);

  L.tileLayer(tilesUrl, {
    attribution: window.wayfarerTileConfig?.attribution ?? '&copy; OpenStreetMap contributors',
    maxZoom: 19
  }).addTo(map);

  const render = (state: EditorTripState): void => {
    layers.clearLayers();

    Object.values(state.regionsById).forEach(region => renderRegion(region, layers));
    Object.values(state.areasById).forEach(area => renderArea(area, layers));
    Object.values(state.placesById).forEach(place => renderPlace(place, layers));
    Object.values(state.segmentsById).forEach(segment => renderSegment(segment, state, layers));

    fitMapToState(map, state);
  };

  return {
    render,
    dispose: () => map.remove()
  };
};

const renderRegion = (region: EditorRegion, layers: LayerGroup): void => {
  if (!region.center) {
    return;
  }

  L.circleMarker([region.center.latitude, region.center.longitude], {
    radius: region.isShadow ? 5 : 7,
    color: region.isShadow ? '#64748b' : '#2563eb',
    weight: 2,
    fillOpacity: 0.7
  }).bindTooltip(escapeHtml(region.name)).addTo(layers);
};

const renderPlace = (place: EditorPlace, layers: LayerGroup): void => {
  if (!place.location) {
    return;
  }

  const marker = L.marker([place.location.latitude, place.location.longitude], {
    title: place.name
  });
  const visitText = place.visitSummary.isVisited ? ` · ${place.visitSummary.visitCount} visit(s)` : '';
  marker.bindPopup(`<strong>${escapeHtml(place.name)}</strong>${visitText}`);
  marker.addTo(layers);
};

const renderArea = (area: EditorArea, layers: LayerGroup): void => {
  if (!area.geometry) {
    return;
  }

  const rings = area.geometry.coordinates.map(ring => ring.map(([longitude, latitude]) => [latitude, longitude] as [number, number]));
  L.polygon(rings, {
    color: area.fillHex,
    fillColor: area.fillHex,
    fillOpacity: 0.25,
    weight: 2
  }).bindTooltip(escapeHtml(area.name)).addTo(layers);
};

const renderSegment = (segment: EditorSegment, state: EditorTripState, layers: LayerGroup): void => {
  const coordinates = segment.route?.coordinates ?? fallbackSegmentCoordinates(segment, state);
  if (!coordinates || coordinates.length < 2) {
    return;
  }

  const latLngs = coordinates.map(([longitude, latitude]) => [latitude, longitude] as [number, number]);
  L.polyline(latLngs, {
    color: '#0ea5e9',
    weight: 3,
    opacity: 0.8
  }).bindTooltip(escapeHtml(segmentLabel(segment, state))).addTo(layers);
};

const fallbackSegmentCoordinates = (segment: EditorSegment, state: EditorTripState): Array<[number, number]> | null => {
  const from = segment.fromPlaceId ? state.placesById[segment.fromPlaceId]?.location : null;
  const to = segment.toPlaceId ? state.placesById[segment.toPlaceId]?.location : null;
  return from && to ? [[from.longitude, from.latitude], [to.longitude, to.latitude]] : null;
};

const fitMapToState = (map: LeafletMap, state: EditorTripState): void => {
  const bounds = L.latLngBounds([]);

  Object.values(state.regionsById).forEach(region => region.center && bounds.extend([region.center.latitude, region.center.longitude]));
  Object.values(state.placesById).forEach(place => place.location && bounds.extend([place.location.latitude, place.location.longitude]));
  Object.values(state.areasById).forEach(area => area.geometry?.coordinates.flat().forEach(([longitude, latitude]) => bounds.extend([latitude, longitude])));
  Object.values(state.segmentsById).forEach(segment => (segment.route?.coordinates ?? fallbackSegmentCoordinates(segment, state))?.forEach(([longitude, latitude]) => bounds.extend([latitude, longitude])));

  if (bounds.isValid()) {
    map.fitBounds(bounds, { padding: [32, 32], maxZoom: 12 });
    return;
  }

  if (state.metadata.center) {
    map.setView([state.metadata.center.latitude, state.metadata.center.longitude], state.metadata.zoom ?? 8);
  }
};

const segmentLabel = (segment: EditorSegment, state: EditorTripState): string => {
  const fromName = segment.fromPlaceId ? state.placesById[segment.fromPlaceId]?.name : null;
  const toName = segment.toPlaceId ? state.placesById[segment.toPlaceId]?.name : null;
  return [fromName, toName].filter(Boolean).join(' to ') || segment.mode || 'Segment';
};

const escapeHtml = (value: string): string =>
  value.replace(/[&<>"']/g, character => ({
    '&': '&amp;',
    '<': '&lt;',
    '>': '&gt;',
    '"': '&quot;',
    "'": '&#39;'
  })[character] ?? character);

declare global {
  interface Window {
    wayfarerTileConfig?: {
      attribution?: string;
    };
  }
}
