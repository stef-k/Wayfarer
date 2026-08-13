<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue';
import type { SegmentRoutePointEditorController } from './segmentRouteMapWork';
import type { SegmentRouteWorkNode } from './segmentRouteWorkState';

const props = defineProps<{ controller: SegmentRoutePointEditorController }>();
const status = ref('');
const invalidFields = ref(new Set<string>());
const invalidText = ref(new Map<string, string>());
const nodes = computed(() => props.controller.nodes());

/** Reports local coordinate validity without transferring field ownership. */
defineExpose({
  hasInvalidCoordinates: () => invalidFields.value.size > 0,
  focusFirstInvalid: () => document.querySelector<HTMLInputElement>('.segment-route-point-editor input[aria-invalid="true"]')?.focus()
});

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

/** Applies a complete finite coordinate pair while invalid text remains local to its control. */
function move(node: SegmentRouteWorkNode, axis: 0 | 1, event: Event): void {
  if (node.kind !== 'anonymous') return;
  const input = event.target as HTMLInputElement;
  const value = input.valueAsNumber;
  const limit = axis === 0 ? 180 : 90;
  if (!Number.isFinite(value) || value < -limit || value > limit) {
    setInvalidText(node.key, axis, input.value);
    setInvalid(node.key, axis, true);
    return;
  }
  const coordinate: [number, number] = [...node.coordinate];
  coordinate[axis] = value;
  if (!props.controller.move(node.key, coordinate)) {
    setInvalid(node.key, axis, true);
    return;
  }
  setInvalidText(node.key, axis, null);
  setInvalid(node.key, axis, false);
  status.value = `${nodeLabel(node, nodes.value.indexOf(node))} moved.`;
}

/** Tracks each anonymous coordinate field independently. */
function setInvalid(nodeKey: string, axis: 0 | 1, invalid: boolean): void {
  const next = new Set(invalidFields.value);
  const key = fieldKey(nodeKey, axis);
  if (invalid) next.add(key); else next.delete(key);
  invalidFields.value = next;
}

function fieldKey(nodeKey: string, axis: 0 | 1): string {
  return `${nodeKey}:${axis}`;
}

function isInvalid(nodeKey: string, axis: 0 | 1): boolean {
  return invalidFields.value.has(fieldKey(nodeKey, axis));
}

function fieldValue(node: Extract<SegmentRouteWorkNode, { kind: 'anonymous' }>, axis: 0 | 1): string | number {
  return invalidText.value.get(fieldKey(node.key, axis)) ?? node.coordinate[axis];
}

function setInvalidText(nodeKey: string, axis: 0 | 1, value: string | null): void {
  const next = new Map(invalidText.value);
  const key = fieldKey(nodeKey, axis);
  if (value === null) next.delete(key); else next.set(key, value);
  invalidText.value = next;
}

/** Creates a stable accessible error target from the route point identity. */
function errorId(nodeKey: string, axis: 0 | 1): string {
  return `route-point-${nodeKey.replace(/[^a-zA-Z0-9_-]/g, '-')}-${axis === 0 ? 'longitude' : 'latitude'}-error`;
}

/** Removes an anonymous point and restores focus to the nearest meaningful route control. */
async function remove(node: SegmentRouteWorkNode, index: number): Promise<void> {
  if (!props.controller.remove(node.key)) return;
  setInvalid(node.key, 0, false);
  setInvalid(node.key, 1, false);
  setInvalidText(node.key, 0, null);
  setInvalidText(node.key, 1, null);
  status.value = `${nodeLabel(node, index)} removed.`;
  await nextTick();
  const target = document.querySelector<HTMLElement>(`[data-route-point-index="${Math.min(index, nodes.value.length - 1)}"] button, [data-route-point-index="${Math.min(index, nodes.value.length - 1)}"] input`)
    ?? document.querySelector<HTMLElement>('[data-route-insert-action]');
  target?.focus();
}

watch(
  () => new Set(nodes.value.filter(node => node.kind === 'anonymous').map(node => node.key)),
  keys => {
    const next = new Set([...invalidFields.value].filter(key => keys.has(key.slice(0, key.lastIndexOf(':')))));
    if (next.size !== invalidFields.value.size) invalidFields.value = next;
    const nextText = new Map([...invalidText.value].filter(([key]) => keys.has(key.slice(0, key.lastIndexOf(':')))));
    if (nextText.size !== invalidText.value.size) invalidText.value = nextText;
  }
);
</script>

<template>
  <section class="segment-route-point-editor" aria-labelledby="segment-route-point-heading">
    <h3 id="segment-route-point-heading" tabindex="-1">Route points</h3>
    <p>Start, Via, and End anchors follow their saved Places and cannot be moved or removed here.</p>
    <ol class="segment-route-point-list">
      <li v-for="(node, index) in nodes" :key="node.key" :data-route-point-index="index" :data-route-point-key="node.key">
        <strong>{{ nodeLabel(node, index) }}</strong>
        <template v-if="node.kind === 'anonymous'">
          <label>Longitude <input type="number" min="-180" max="180" step="any" :value="fieldValue(node, 0)" :aria-invalid="isInvalid(node.key, 0) ? 'true' : undefined" :aria-describedby="isInvalid(node.key, 0) ? errorId(node.key, 0) : undefined" @input="move(node, 0, $event)" /></label>
          <small v-if="isInvalid(node.key, 0)" :id="errorId(node.key, 0)">Longitude must be between -180 and 180.</small>
          <label>Latitude <input type="number" min="-90" max="90" step="any" :value="fieldValue(node, 1)" :aria-invalid="isInvalid(node.key, 1) ? 'true' : undefined" :aria-describedby="isInvalid(node.key, 1) ? errorId(node.key, 1) : undefined" @input="move(node, 1, $event)" /></label>
          <small v-if="isInvalid(node.key, 1)" :id="errorId(node.key, 1)">Latitude must be between -90 and 90.</small>
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
