<script setup lang="ts">
import { computed, nextTick, ref } from 'vue';
import type { SegmentRoutePointEditorController } from './segmentRouteMapWork';
import type { SegmentRouteWorkNode } from './segmentRouteWorkState';

const props = defineProps<{ controller: SegmentRoutePointEditorController }>();
const status = ref('');
const nodes = computed(() => props.controller.nodes());

/** Names semantic anchors and anonymous route points independently of mutable indices. */
function nodeLabel(node: SegmentRouteWorkNode, index: number): string {
  if (node.kind === 'anonymous') return `Route point ${anonymousOrdinal(index)}`;
  if (node.role === 'from') return `Start — ${node.placeName} — fixed`;
  if (node.role === 'to') return `End — ${node.placeName} — fixed`;
  const via = nodes.value.slice(0, index + 1).filter(candidate => candidate.kind === 'anchor' && candidate.role === 'waypoint').length;
  return `Via ${via} — ${node.placeName} — fixed`;
}

function anonymousOrdinal(index: number): number {
  return nodes.value.slice(0, index + 1).filter(node => node.kind === 'anonymous').length;
}

/** Inserts deterministically at the selected semantic interval and focuses the new controls. */
async function insertAfter(node: SegmentRouteWorkNode, index: number): Promise<void> {
  const key = props.controller.insertAfter(node.key);
  if (!key) return;
  status.value = `Inserted route point between ${nodeLabel(node, index)} and ${nodeLabel(nodes.value[index + 2], index + 2)}.`;
  await nextTick();
  document.querySelector<HTMLInputElement>(`[data-route-point-key="${CSS.escape(key)}"] input`)?.focus();
}

/** Applies a complete finite coordinate pair; invalid partial input remains local to its control. */
function move(node: SegmentRouteWorkNode, axis: 0 | 1, event: Event): void {
  if (node.kind !== 'anonymous') return;
  const value = Number((event.target as HTMLInputElement).value);
  const coordinate: [number, number] = [...node.coordinate];
  coordinate[axis] = value;
  if (!props.controller.move(node.key, coordinate)) {
    status.value = axis === 0 ? 'Longitude must be between -180 and 180.' : 'Latitude must be between -90 and 90.';
    return;
  }
  status.value = `${nodeLabel(node, nodes.value.indexOf(node))} moved.`;
}

/** Removes an anonymous point and restores focus to the nearest meaningful route control. */
async function remove(node: SegmentRouteWorkNode, index: number): Promise<void> {
  if (!props.controller.remove(node.key)) return;
  status.value = `${nodeLabel(node, index)} removed.`;
  await nextTick();
  const target = document.querySelector<HTMLElement>(`[data-route-point-index="${Math.min(index, nodes.value.length - 1)}"] button, [data-route-point-index="${Math.min(index, nodes.value.length - 1)}"] input`)
    ?? document.querySelector<HTMLElement>('[data-route-insert-action]');
  target?.focus();
}
</script>

<template>
  <section class="segment-route-point-editor" aria-labelledby="segment-route-point-heading">
    <h3 id="segment-route-point-heading" tabindex="-1">Route points</h3>
    <p>Start, Via, and End anchors follow their saved Places and cannot be moved or removed here.</p>
    <ol class="segment-route-point-list">
      <li v-for="(node, index) in nodes" :key="node.key" :data-route-point-index="index" :data-route-point-key="node.key">
        <strong>{{ nodeLabel(node, index) }}</strong>
        <template v-if="node.kind === 'anonymous'">
          <label>Longitude <input type="number" min="-180" max="180" step="any" :value="node.coordinate[0]" @change="move(node, 0, $event)" /></label>
          <label>Latitude <input type="number" min="-90" max="90" step="any" :value="node.coordinate[1]" @change="move(node, 1, $event)" /></label>
          <button type="button" class="btn btn-outline-danger btn-sm" :aria-label="`Remove ${nodeLabel(node, index)}`" @click="remove(node, index)">Remove</button>
        </template>
        <span v-else class="small">Fixed saved-Place anchor</span>
        <button
          v-if="index < nodes.length - 1"
          type="button"
          class="btn btn-outline-light btn-sm"
          data-route-insert-action
          :aria-label="`Insert route point after ${nodeLabel(node, index)}`"
          @click="insertAfter(node, index)"
        >Insert route point after</button>
      </li>
    </ol>
    <p class="visually-hidden" aria-live="polite">{{ status }}</p>
  </section>
</template>
