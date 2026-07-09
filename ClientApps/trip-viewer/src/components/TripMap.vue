<script setup lang="ts">
import L from 'leaflet';
import 'leaflet/dist/leaflet.css';
import { nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { coordinateLabel, distanceLabel, durationLabel, notesPreview, segmentTitle, visitSummaryForPlace } from '../viewModel';
import type { SegmentSummary } from '../viewModel';
import { placeMarkerIcon, popupHtml, regionMarkerIcon, toLatLng, wayfarerAttributionPrefix } from '../mapRendering';
import type { PopupRow } from '../mapRendering';
import type { EntityType, Guid, TripViewerState, ViewerSelection } from '../types';

const props = defineProps<{
  state: TripViewerState;
  selection: ViewerSelection;
  segments: SegmentSummary[];
  layoutSignal: number;
  fullTripViewSignal: number;
}>();

const emit = defineEmits<{
  select: [selection: ViewerSelection];
  'restore-full-trip': [];
}>();

const mapElement = ref<HTMLDivElement | null>(null);
let map: L.Map | null = null;
let layerGroup: L.LayerGroup | null = null;
let mapTools: L.Control | null = null;
let measureTool: MeasureTool | null = null;
const featureLayers = new Map<string, L.Layer>();

onMounted(() => {
  if (!mapElement.value) return;

  map = L.map(mapElement.value, { zoomControl: false });
  map.attributionControl.setPrefix(wayfarerAttributionPrefix);
  L.tileLayer(props.state.map.tileUrlTemplate, {
    attribution: props.state.map.tileAttribution,
    maxZoom: 19
  }).addTo(map);
  L.control.zoom({ position: 'bottomright' }).addTo(map);
  if (props.state.viewerMode !== 'embed') {
    mapTools = createMapToolsControl();
    mapTools.addTo(map);
  }
  layerGroup = L.layerGroup().addTo(map);

  renderLayers();
  applyInitialView();
  invalidateAfterLayout();
  updateBrowserMapQuery();
  // Applies startup query selection after initial map params are synced, before move tracking begins.
  focusSelection(props.selection);

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
  measureTool?.cancel();
  measureTool = null;
  if (mapTools && map) {
    map.removeControl(mapTools);
  }
  mapTools = null;
  map?.remove();
  map = null;
  layerGroup = null;
  featureLayers.clear();
});

watch(() => props.selection, () => {
  renderLayers();
  focusSelection(props.selection);
}, { deep: true });

watch(() => props.layoutSignal, () => {
  invalidateAfterLayout();
});

watch(() => props.fullTripViewSignal, () => {
  applyMapView('reset');
  invalidateAfterLayout();
});

function invalidateAfterLayout(): void {
  // Leaflet needs a deferred size pass after responsive, iframe, and screenshot viewport transitions.
  void nextTick(() => {
    window.requestAnimationFrame(() => {
      map?.invalidateSize({ pan: false });
    });
  });
}

function renderLayers(): void {
  if (!layerGroup) return;
  layerGroup.clearLayers();
  featureLayers.clear();

  props.state.regionOrder.forEach(regionId => {
    const region = props.state.regionsById[regionId];
    if (!region?.center) return;
    const selected = isSelected('region', region.id);
    const marker = L.marker(toLatLng(region.center), { icon: regionMarkerIcon(region.name, selected) })
      .bindPopup(popupHtml(region.name, 'Region center', notesPreview(region.notes), 'region', region.id, [
        { label: 'Center', value: coordinateLabel(region.center) }
      ]))
      .on('click', event => selectFeature(event, { type: 'region', id: region.id }));
    addFeature('region', region.id, marker);
  });

  props.state.regionOrder.forEach(regionId => {
    (props.state.placeOrderByRegionId[regionId] ?? []).forEach(placeId => {
      const place = props.state.placesById[placeId];
      if (!place?.location) return;
      const marker = L.marker(toLatLng(place.location), { icon: placeMarkerIcon(place, isSelected('place', place.id), visitSummaryForPlace(props.state, place)) })
        .bindPopup(popupHtml(place.name, 'Place', notesPreview(place.notes), 'place', place.id, placePopupRows(place.id)))
        .on('click', event => selectFeature(event, { type: 'place', id: place.id }));
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
      }).bindPopup(popupHtml(area.name, 'Area', notesPreview(area.notes), 'area', area.id, [
        { label: 'Region', value: props.state.regionsById[area.regionId]?.name ?? 'Unknown region' }
      ]))
        .on('click', event => selectFeature(event, { type: 'area', id: area.id }));
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
    }).bindPopup(popupHtml(segmentTitle(summary), 'Segment', notesPreview(summary.segment.notes), 'segment', summary.segment.id, segmentPopupRows(summary)))
      .on('click', event => selectFeature(event, { type: 'segment', id: summary.segment.id }));
    addFeature('segment', summary.segment.id, polyline);
  });
}

function addFeature(type: EntityType, id: Guid, layer: L.Layer): void {
  layer.addTo(layerGroup!);
  featureLayers.set(`${type}:${id}`, layer);
}

function applyInitialView(): void {
  applyMapView('initial');
}

// Resolves initial query precedence separately from deliberate full-trip restoration.
function applyMapView(intent: 'initial' | 'reset'): void {
  if (!map) return;
  const resolved = resolveMapView(intent);
  if (resolved.kind === 'fit' && fitAllFeatures()) return;
  map.setView([resolved.latitude, resolved.longitude], resolved.zoom);
}

function resolveMapView(intent: 'initial' | 'reset'): { kind: 'view'; latitude: number; longitude: number; zoom: number } | { kind: 'fit'; latitude: number; longitude: number; zoom: number } {
  const initial = props.state.map.initialView;
  if (intent === 'initial') {
    return initial.source === 'query' || initial.source === 'trip'
      ? { kind: 'view', ...initial }
      : { kind: 'fit', ...initial };
  }

  const { center, zoom } = props.state.trip;
  if (center && isValidMapView(center.latitude, center.longitude, zoom)) {
    return { kind: 'view', latitude: center.latitude, longitude: center.longitude, zoom };
  }

  // Reset must never reuse query coordinates; feature bounds are preferred before this safe fallback.
  return { kind: 'fit', latitude: 20, longitude: 0, zoom: 2 };
}

function isValidMapView(latitude: number, longitude: number, zoom: number | null): zoom is number {
  return Number.isFinite(latitude)
    && latitude >= -90
    && latitude <= 90
    && Number.isFinite(longitude)
    && longitude >= -180
    && longitude <= 180
    && zoom !== null
    && Number.isFinite(zoom)
    && zoom >= 0
    && zoom <= 19;
}

function selectFeature(event: L.LeafletMouseEvent, nextSelection: ViewerSelection): void {
  if (measureTool?.consumeFeatureClick(event.latlng)) {
    event.originalEvent?.preventDefault();
    L.DomEvent.stopPropagation(event.originalEvent);
    map?.closePopup();
    return;
  }

  emit('select', nextSelection);
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

function placePopupRows(placeId: Guid): PopupRow[] {
  const place = props.state.placesById[placeId];
  if (!place) return [];
  const rows: PopupRow[] = [
    { label: 'Region', value: props.state.regionsById[place.regionId]?.name ?? 'Unknown region' },
    { label: 'Coordinates', value: coordinateLabel(place.location) }
  ];
  if (place.address) {
    rows.push({ label: 'Address', value: place.address });
  }

  const visitSummary = visitSummaryForPlace(props.state, place);
  if (visitSummary?.isVisited) {
    rows.push({ label: 'Visits', value: visitSummary.visitCount === 1 ? 'Visited once' : `Visited ${visitSummary.visitCount} times` });
  }

  return rows;
}

function segmentPopupRows(summary: SegmentSummary): PopupRow[] {
  return [
    { label: 'Mode', value: summary.segment.mode || 'Segment' },
    { label: 'Distance', value: distanceLabel(summary.segment.estimatedDistanceKm) },
    { label: 'Duration', value: durationLabel(summary.segment.estimatedDurationMinutes) }
  ];
}

function createMapToolsControl(): L.Control {
  const control = new L.Control({ position: 'topright' });
  control.onAdd = mapInstance => {
    const container = L.DomUtil.create('div', 'leaflet-bar trip-viewer-map-tools');
    L.DomEvent.disableClickPropagation(container);
    L.DomEvent.disableScrollPropagation(container);

    const zoomText = L.DomUtil.create('span', 'trip-viewer-map-tools__zoom', container);
    const updateZoom = () => { zoomText.textContent = `Zoom: ${mapInstance.getZoom()}`; };
    updateZoom();
    mapInstance.on('zoomend', updateZoom);

    const measureButton = mapToolButton(container, 'Measure distance', '/lib/bootstrap-icons/bootstrap-icons-1.13.1/rulers.svg');
    const resetButton = mapToolButton(container, 'Recenter full trip', '/lib/bootstrap-icons/bootstrap-icons-1.13.1/arrow-counterclockwise.svg');
    const copyButton = mapToolButton(container, 'Copy map link', '/lib/bootstrap-icons/bootstrap-icons-1.13.1/link-45deg.svg');

    L.DomEvent.on(measureButton, 'click', event => {
      L.DomEvent.stop(event);
      if (measureTool) {
        measureTool.cancel();
        measureTool = null;
        measureButton.classList.remove('active');
        return;
      }

      measureButton.classList.add('active');
      measureTool = createMeasureTool(mapInstance, () => {
        measureTool = null;
        measureButton.classList.remove('active');
      });
    });

    L.DomEvent.on(resetButton, 'click', event => {
      L.DomEvent.stop(event);
      emit('restore-full-trip');
    });

    L.DomEvent.on(copyButton, 'click', event => {
      L.DomEvent.stop(event);
      void copyCurrentMapLink(mapInstance, copyButton);
    });

    const originalOnRemove = control.onRemove;
    control.onRemove = removedMap => {
      removedMap.off('zoomend', updateZoom);
      originalOnRemove?.call(control, removedMap);
    };

    return container;
  };

  return control;
}

function mapToolButton(container: HTMLElement, label: string, iconUrl: string): HTMLButtonElement {
  const button = L.DomUtil.create('button', 'trip-viewer-map-tools__button', container) as HTMLButtonElement;
  button.type = 'button';
  button.title = label;
  button.setAttribute('aria-label', label);
  button.innerHTML = `<img src="${iconUrl}" alt="" width="18" height="18">`;
  return button;
}

async function copyCurrentMapLink(mapInstance: L.Map, button: HTMLButtonElement): Promise<void> {
  const center = mapInstance.getCenter();
  const url = new URL(window.location.href);
  url.searchParams.set('lat', center.lat.toFixed(6));
  url.searchParams.set('lon', center.lng.toFixed(6));
  url.searchParams.delete('lng');
  url.searchParams.set('zoom', String(mapInstance.getZoom()));

  try {
    if (navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(url.toString());
    } else {
      copyTextFallback(url.toString());
    }
    showCopied(button);
  } catch {
    copyTextFallback(url.toString());
    showCopied(button);
  }
}

function copyTextFallback(text: string): void {
  const input = document.createElement('input');
  input.value = text;
  input.style.position = 'fixed';
  input.style.opacity = '0';
  document.body.appendChild(input);
  input.select();
  document.execCommand('copy');
  input.remove();
}

function showCopied(button: HTMLButtonElement): void {
  const previousLabel = button.getAttribute('aria-label') ?? 'Copy map link';
  const previousTitle = button.title;
  const previousHtml = button.innerHTML;
  button.setAttribute('aria-label', 'Map link copied');
  button.title = 'Map link copied';
  button.innerHTML = '<img src="/lib/bootstrap-icons/bootstrap-icons-1.13.1/check.svg" alt="" width="18" height="18">';
  window.setTimeout(() => {
    button.setAttribute('aria-label', previousLabel);
    button.title = previousTitle;
    button.innerHTML = previousHtml;
  }, 1400);
}

interface MeasureTool {
  cancel: () => void;
  consumeFeatureClick: (point: L.LatLng) => boolean;
}

function createMeasureTool(mapInstance: L.Map, onCancel: () => void): MeasureTool {
  const points: L.LatLng[] = [];
  const pointMarkers: L.CircleMarker[] = [];
  const labelMarkers: L.Marker[] = [];
  let line: L.Polyline | null = null;
  const help = L.control({ position: 'bottomleft' });

  const reset = () => {
    pointMarkers.splice(0).forEach(marker => marker.remove());
    labelMarkers.splice(0).forEach(marker => marker.remove());
    line?.remove();
    line = null;
  };

  const cancel = () => {
    reset();
    help.remove();
    mapInstance.off('click', onMapClick);
    window.removeEventListener('keydown', onKey);
    mapInstance.getContainer().style.cursor = '';
    onCancel();
  };

  const addPoint = (point: L.LatLng) => {
    points.push(point);
    pointMarkers.push(L.circleMarker(point, {
      radius: 4,
      color: '#0d6efd',
      fillColor: '#0d6efd',
      fillOpacity: 0.9
    }).addTo(mapInstance));

    if (!line) {
      line = L.polyline(points, { color: '#0d6efd', dashArray: '5 5', weight: 3 }).addTo(mapInstance);
    } else {
      line.setLatLngs(points);
    }

    if (points.length > 1) {
      const km = distanceKilometers(points);
      labelMarkers.push(L.marker(point, {
        icon: L.divIcon({
          className: 'trip-viewer-map-distance-label',
          html: `<span>${km.toFixed(2)} km</span>`,
          iconSize: [76, 26],
          iconAnchor: [38, 13]
        })
      }).addTo(mapInstance));
    }
  };

  const onMapClick = (event: L.LeafletMouseEvent) => addPoint(event.latlng);

  const onKey = (event: KeyboardEvent) => {
    if (event.key === 'Escape') {
      cancel();
    }
  };

  help.onAdd = () => {
    const container = L.DomUtil.create('div', 'leaflet-bar trip-viewer-map-measure-help');
    container.textContent = 'Click points to measure. Esc cancels.';
    return container;
  };
  help.addTo(mapInstance);
  mapInstance.getContainer().style.cursor = 'crosshair';
  mapInstance.on('click', onMapClick);
  window.addEventListener('keydown', onKey);

  return { cancel, consumeFeatureClick: point => {
    addPoint(point);
    return true;
  } };
}

function distanceKilometers(points: L.LatLng[]): number {
  return points.reduce((total, point, index) => {
    if (index === 0) return total;
    return total + points[index - 1].distanceTo(point) / 1000;
  }, 0);
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
