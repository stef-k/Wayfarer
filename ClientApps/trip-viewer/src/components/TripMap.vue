<script setup lang="ts">
import L from 'leaflet';
import 'leaflet/dist/leaflet.css';
import { onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { notesPreview, segmentTitle, visitSummaryForPlace } from '../viewModel';
import type { SegmentSummary } from '../viewModel';
import { placeMarkerIcon, popupHtml, regionMarkerIcon, toLatLng } from '../mapRendering';
import type { EntityType, Guid, TripViewerState, ViewerSelection } from '../types';

const props = defineProps<{
  state: TripViewerState;
  selection: ViewerSelection;
  segments: SegmentSummary[];
}>();

const emit = defineEmits<{
  select: [selection: ViewerSelection];
}>();

const mapElement = ref<HTMLDivElement | null>(null);
let map: L.Map | null = null;
let layerGroup: L.LayerGroup | null = null;
const featureLayers = new Map<string, L.Layer>();

onMounted(() => {
  if (!mapElement.value) return;

  map = L.map(mapElement.value, { zoomControl: true });
  L.tileLayer(props.state.map.tileUrlTemplate, {
    attribution: props.state.map.tileAttribution,
    maxZoom: 19
  }).addTo(map);
  layerGroup = L.layerGroup().addTo(map);

  renderLayers();
  applyInitialView();
  updateBrowserMapQuery();

  map.on('moveend', updateBrowserMapQuery);
  map.on('popupopen', event => {
    const container = event.popup.getElement();
    container?.querySelectorAll<HTMLButtonElement>('[data-trip-viewer-select]').forEach(button => {
      button.addEventListener('click', () => {
        const [type, id] = (button.dataset.tripViewerSelect ?? '').split(':');
        if (isEntityType(type) && id) emit('select', { type, id });
      }, { once: true });
    });
  });
});

onBeforeUnmount(() => {
  map?.remove();
  map = null;
  layerGroup = null;
  featureLayers.clear();
});

watch(() => props.selection, () => {
  renderLayers();
  focusSelection(props.selection);
}, { deep: true });

function renderLayers(): void {
  if (!layerGroup) return;
  layerGroup.clearLayers();
  featureLayers.clear();

  props.state.regionOrder.forEach(regionId => {
    const region = props.state.regionsById[regionId];
    if (!region?.center) return;
    const selected = isSelected('region', region.id);
    const marker = L.marker(toLatLng(region.center), { icon: regionMarkerIcon(region.name, selected) })
      .bindPopup(popupHtml(region.name, 'Region center', notesPreview(region.notes), 'region', region.id))
      .on('click', () => emit('select', { type: 'region', id: region.id }));
    addFeature('region', region.id, marker);
  });

  props.state.regionOrder.forEach(regionId => {
    (props.state.placeOrderByRegionId[regionId] ?? []).forEach(placeId => {
      const place = props.state.placesById[placeId];
      if (!place?.location) return;
      const marker = L.marker(toLatLng(place.location), { icon: placeMarkerIcon(place, isSelected('place', place.id), visitSummaryForPlace(props.state, place)) })
        .bindPopup(popupHtml(place.name, 'Place', notesPreview(place.notes), 'place', place.id))
        .on('click', () => emit('select', { type: 'place', id: place.id }));
      addFeature('place', place.id, marker);
    });

    (props.state.areaOrderByRegionId[regionId] ?? []).forEach(areaId => {
      const area = props.state.areasById[areaId];
      if (!area?.geometry) return;
      const polygon = L.geoJSON(area.geometry, {
        style: {
          color: isSelected('area', area.id) ? '#0d6efd' : area.fillHex,
          fillColor: area.fillHex,
          fillOpacity: isSelected('area', area.id) ? 0.34 : 0.22,
          opacity: 0.9,
          weight: isSelected('area', area.id) ? 4 : 2
        }
      }).bindPopup(popupHtml(area.name, 'Area', notesPreview(area.notes), 'area', area.id))
        .on('click', () => emit('select', { type: 'area', id: area.id }));
      addFeature('area', area.id, polygon);
    });
  });

  props.segments.forEach(summary => {
    const line = segmentLine(summary);
    if (!line) return;
    const polyline = L.polyline(line, {
      color: isSelected('segment', summary.segment.id) ? '#0d6efd' : '#334155',
      opacity: 0.86,
      weight: isSelected('segment', summary.segment.id) ? 5 : 3,
      dashArray: summary.segment.route ? undefined : '6 6'
    }).bindPopup(popupHtml(segmentTitle(summary), 'Segment', notesPreview(summary.segment.notes), 'segment', summary.segment.id))
      .on('click', () => emit('select', { type: 'segment', id: summary.segment.id }));
    addFeature('segment', summary.segment.id, polyline);
  });
}

function addFeature(type: EntityType, id: Guid, layer: L.Layer): void {
  layer.addTo(layerGroup!);
  featureLayers.set(`${type}:${id}`, layer);
}

function applyInitialView(): void {
  if (!map) return;
  const initial = props.state.map.initialView;
  const shouldFitContent = initial.source !== 'query' && initial.source !== 'trip';
  if (shouldFitContent && fitAllFeatures()) return;
  map.setView([initial.latitude, initial.longitude], initial.zoom);
}

function fitAllFeatures(): boolean {
  if (!map || featureLayers.size === 0) return false;
  const bounds = L.latLngBounds([]);
  featureLayers.forEach(layer => {
    if ('getBounds' in layer && typeof layer.getBounds === 'function') {
      bounds.extend(layer.getBounds());
    } else if ('getLatLng' in layer && typeof layer.getLatLng === 'function') {
      bounds.extend(layer.getLatLng());
    }
  });

  if (!bounds.isValid()) return false;
  map.fitBounds(bounds.pad(0.18), { maxZoom: 13 });
  return true;
}

function focusSelection(selection: ViewerSelection): void {
  if (!map || selection.type === 'trip') return;
  const layer = featureLayers.get(`${selection.type}:${selection.id}`);
  if (!layer) return;

  if ('getBounds' in layer && typeof layer.getBounds === 'function') {
    map.fitBounds(layer.getBounds().pad(0.22), { maxZoom: 14 });
  } else if ('getLatLng' in layer && typeof layer.getLatLng === 'function') {
    map.setView(layer.getLatLng(), Math.max(map.getZoom(), 13));
  }
}

function segmentLine(summary: SegmentSummary): L.LatLngExpression[] | null {
  if (summary.segment.route?.coordinates.length) {
    return summary.segment.route.coordinates.map(([longitude, latitude]) => [latitude, longitude]);
  }

  if (summary.segment.fallbackStart && summary.segment.fallbackEnd) {
    return [toLatLng(summary.segment.fallbackStart), toLatLng(summary.segment.fallbackEnd)];
  }

  return null;
}

function updateBrowserMapQuery(): void {
  if (!map || props.state.viewerMode === 'embed') return;
  const center = map.getCenter();
  const params = new URLSearchParams(window.location.search);
  params.set('lat', center.lat.toFixed(6));
  params.set('lon', center.lng.toFixed(6));
  params.delete('lng');
  params.set('zoom', String(map.getZoom()));
  window.history.replaceState(null, '', `${window.location.pathname}?${params.toString()}`);
}

function isSelected(type: EntityType, id: Guid): boolean {
  return props.selection.type === type && props.selection.id === id;
}

function isEntityType(value: string | undefined): value is EntityType {
  return value === 'region' || value === 'place' || value === 'area' || value === 'segment';
}
</script>

<template>
  <section class="trip-viewer-map-shell" aria-label="Trip map">
    <div ref="mapElement" class="trip-viewer-map"></div>
  </section>
</template>
