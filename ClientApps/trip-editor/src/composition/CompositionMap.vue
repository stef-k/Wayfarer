<script setup lang="ts">
import { onMounted, onUnmounted, ref, watch } from 'vue';
import L from 'leaflet';
import { createTripEditorTileLayer } from '../map/tileRetryLayer';
import { anchorIds, contents, find, isCoordinate, label, spatialContent } from './compositionModel';
import type { Content, Kind, Selection, Snapshot, Workspace } from './compositionTypes';

const props = defineProps<{ workspace: Workspace; selection: Selection | null; counterpart: string; visible: Record<string, boolean> }>();
const emit = defineEmits<{ select: [selection: Selection]; status: [message: string] }>();
const host = ref<HTMLElement | null>(null);
let map: L.Map | null = null;
let layers: L.LayerGroup[] = [];
let observer: ResizeObserver | null = null;
const colors = { a: '#1466cf', b: '#bf6300', c: '#198443' };
const snapshots = (): [string, Snapshot][] => [['a', props.workspace.sourceA], ['b', props.workspace.sourceB], ['c', props.workspace.current]];

/** Complete rendered geography is fitted inside the actual map box, independently of drawer width/height. */
function focus(): void {
  if (!map) return;
  const selection = props.selection;
  const source = selection ? selection.side === 'a' ? props.workspace.sourceA : props.workspace.sourceB : null;
  const points = selection && source ? spatialContent(source, selection.kind, selection.id)
    .concat(props.counterpart ? spatialContent(props.workspace.current, selection.kind, props.counterpart) : [])
    : snapshots().filter(([side]) => props.visible[side]).flatMap(([, s]) => spatialContent(s, 'trip', s.graph.id));
  const size = map.getSize();
  if (!points.length) { emit('status', 'No located geography for this selection. Historical or empty objects have no invented location.'); return; }
  if (size.x < 48 || size.y < 48) return;
  // Clamp both axes to leave a positive fitting area, even under a narrow viewport or a tall drawer.
  const pad = Math.min(32, Math.floor(Math.min(size.x, size.y) / 8));
  const controls = host.value?.parentElement?.querySelector('.composition-layers')?.getBoundingClientRect();
  const right = Math.min(size.x * .45, Math.max(pad, (controls?.width ?? 0) + 20));
  // Reserve the actual layer controls, native zoom buttons and bottom status without exhausting either axis.
  map.fitBounds(L.latLngBounds(points.map(p => L.latLng(p[1], p[0]))), {
    paddingTopLeft: [Math.min(48, size.x * .15), pad], paddingBottomRight: [right, Math.min(56, size.y / 4)], maxZoom: 16, animate: false });
  emit('status', 'Map fitted to the selection’s complete available geography.');
}

/** A/B layers never mutate their source. Fallback lines require complete located anchors. */
function render(): void {
  if (!map) return;
  layers.forEach(layer => layer.remove()); layers = [];
  for (const [side, snapshot] of snapshots()) {
    if (!props.visible[side]) continue;
    const layer = L.layerGroup().addTo(map); layers.push(layer);
    const selected = (kind: Kind, id: string): boolean => side === 'c' ? props.selection?.kind === kind && props.counterpart === id
      : props.selection?.side === side && props.selection.kind === kind && props.selection.id === id
        || props.selection?.kind === kind && props.workspace.mappings.some(m => m.source === side && m.kind === kind && m.sourceId === id
          && m.destinationId === props.counterpart && !['skip', 'destinationRemoved'].includes(m.decision));
    const attach = (path: L.Path, kind: Kind, item: Content): void => {
      path.addTo(layer).bindTooltip(`${side.toUpperCase()} · ${kind} · ${label(item)}`);
      if (side !== 'c') path.on('click', () => emit('select', { side: side as 'a' | 'b', kind, id: item.id }));
    };
    for (const kind of ['region', 'place'] as Kind[]) for (const item of contents(snapshot, kind)) {
      const coordinate = kind === 'region' ? item.center : item.location;
      if (!isCoordinate(coordinate)) continue;
      attach(L.circleMarker([coordinate[1], coordinate[0]], { radius: selected(kind, item.id) ? 11 : kind === 'region' ? 8 : 5,
        color: colors[side as keyof typeof colors], weight: selected(kind, item.id) ? 4 : 2, fillOpacity: kind === 'region' ? .08 : .6,
        ...(kind === 'region' ? { dashArray: '3 3' } : {}) }), kind, item);
    }
    for (const item of contents(snapshot, 'area')) {
      try {
        const geometry = JSON.parse(item.geometryGeoJson ?? 'null');
        if (geometry?.type !== 'Polygon' || !spatialContent(snapshot, 'area', item.id).length) continue;
        const polygon = L.geoJSON(geometry, { style: { color: colors[side as keyof typeof colors], fillOpacity: .1,
          weight: selected('area', item.id) ? 5 : 2 } });
        polygon.addTo(layer).bindTooltip(`${side.toUpperCase()} · Area · ${label(item)}`);
        if (side !== 'c') polygon.on('click', () => emit('select', { side: side as 'a' | 'b', kind: 'area', id: item.id }));
      } catch { /* Invalid historical geometry is not repaired or replaced by a fabricated polygon. */ }
    }
    for (const item of snapshot.graph.segments) {
      let coordinates: number[][] = [];
      try { const route = JSON.parse(item.routeJson ?? 'null'); if (route?.type === 'LineString') coordinates = route.coordinates; } catch { /* No invented route. */ }
      if (!item.hasCustomRoute && !item.routeJson && item.fromPlaceId && item.toPlaceId) {
        const chain = [item.fromPlaceId, ...[...item.waypoints ?? []].sort((a, b) => a.position - b.position).map(w => w.placeId), item.toPlaceId];
        const points = chain.map(id => find(snapshot, 'place', id)?.location);
        if (points.every((p): p is number[] => Boolean(p))) coordinates = points;
      }
      if (coordinates.length >= 2 && coordinates.every(isCoordinate)) attach(L.polyline(coordinates.map(p => [p[1], p[0]]), {
        color: colors[side as keyof typeof colors], weight: selected('segment', item.id) ? 7 : 3,
        dashArray: item.hasCustomRoute ? undefined : '6 5' }), 'segment', item);
      if (selected('segment', item.id)) for (const id of anchorIds(item)) {
        const p = find(snapshot, 'place', id)?.location;
        if (isCoordinate(p)) L.circleMarker([p[1], p[0]], { color: colors[side as keyof typeof colors], radius: 9, fillOpacity: .1 }).addTo(layer);
      }
    }
  }
}
onMounted(() => {
  map = L.map(host.value!, { zoomControl: true }).setView([20, 0], 2);
  createTripEditorTileLayer('/Public/tiles/{z}/{x}/{y}.png', { maxZoom: 19, attribution: '© OpenStreetMap contributors' }).addTo(map);
  render(); focus();
  observer = new ResizeObserver(() => { map?.invalidateSize({ animate: false }); focus(); }); observer.observe(host.value!);
});
watch(() => [props.workspace.current, props.visible.a, props.visible.b, props.visible.c], render, { deep: true });
watch(() => [props.selection?.side, props.selection?.kind, props.selection?.id, props.counterpart], () => { render(); focus(); });
onUnmounted(() => { observer?.disconnect(); map?.remove(); map = null; });
defineExpose({ focus });
</script>

<template>
  <div ref="host" class="composition-map" role="region" aria-label="Shared A B C Trip map" aria-describedby="composition-map-help" tabindex="0"></div>
  <p id="composition-map-help" class="visually-hidden">Use the source trees and contextual inspector to select ideas. Arrow keys pan the map; plus and minus zoom.</p>
</template>
