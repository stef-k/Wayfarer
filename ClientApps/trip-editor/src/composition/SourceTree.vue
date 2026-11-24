<script setup lang="ts">
import { message as tr } from '../../../../wwwroot/js/localization/messages.js';
import { computed, ref } from 'vue';
import { find, label, ordered } from './compositionModel';
import { compositionDecision, displayLabel } from './compositionPresentation';
import type { Kind, Mapping, Selection, Side, Snapshot } from './compositionTypes';
const props = defineProps<{ snapshot: Snapshot; side: Side; selection: Selection | null; mappings: Mapping[]; expanded: string[];
  objectsOnly?: boolean; caption?: string; counterpart?: { kind: Kind; id: string } | null }>();
const emit = defineEmits<{ select: [selection: Selection]; expand: [key: string, open: boolean] }>();
const search = ref('');
const matches = (kind: Kind, id: string): boolean => label(find(props.snapshot, kind, id)!).toLowerCase().includes(search.value.toLowerCase());
const regions = computed(() => ordered(props.snapshot, 'region').filter(r => matches('region', r.id)
  || [...ordered(props.snapshot, 'place', r.id), ...ordered(props.snapshot, 'area', r.id)].some(x => label(x).toLowerCase().includes(search.value.toLowerCase()))));
const active = (kind: Kind, id: string): boolean => props.selection?.side === props.side && props.selection.kind === kind && props.selection.id === id;
const linked = (kind: Kind, id: string): boolean => props.counterpart?.kind === kind && props.mappings.some(m => m.source === props.side
  && m.kind === kind && m.sourceId === id && m.destinationId === props.counterpart?.id && !['skip', 'destinationRemoved'].includes(m.decision));
/** Durable status is descriptive. Only an explicitly accepted reuse is labelled Matched. */
const status = (kind: Kind, id: string): string => {
  const choices = props.mappings.filter(m => m.source === props.side && m.kind === kind && m.sourceId === id);
  if (!choices.length) return tr('Editor.CompositionNotReviewed', 'Not reviewed');
  return [...new Set(choices.map(m => compositionDecision(m.decision)))].join(' · ');
};
const select = (kind: Kind, id: string): void => emit('select', { side: props.side, kind, id });
const toggle = (key: string, event: Event): void => emit('expand', key, (event.target as HTMLDetailsElement).open);
</script>

<template>
  <section class="composition-source" :class="`source-${side}`" :aria-label="tr('Editor.CompositionSource0Hierarchy', 'Source {0} hierarchy', side.toUpperCase())">
    <h2>{{ caption || side.toUpperCase() }} · {{ snapshot.graph.name }} <small>{{ tr('Editor.CompositionReadOnlySource', 'Read-only source') }}</small></h2>
    <label class="composition-field">{{ tr('Editor.CompositionSearchSource0', 'Search source {0}', side.toUpperCase()) }}<input v-model="search" type="search" maxlength="200" /></label>
    <button v-if="!objectsOnly" type="button" class="composition-row" :aria-pressed="active('trip', snapshot.graph.id)" @click="select('trip', snapshot.graph.id)">{{ tr('Editor.CompositionTripDetails', 'Trip details') }} <small>{{ status('trip', snapshot.graph.id) }}</small></button>
    <details v-for="region in regions" :key="region.id" :open="Boolean(search) || expanded.includes(`${side}/${region.id}`)" @toggle="toggle(`${side}/${region.id}`, $event)">
      <summary>{{ displayLabel(region, 'region') }}</summary>
      <button type="button" class="composition-row" :class="{ 'is-counterpart': linked('region', region.id) }" :aria-pressed="active('region', region.id)" @click="select('region', region.id)">{{ tr('Editor.CompositionRegion0', 'Region · {0}', displayLabel(region, 'region')) }} <small>{{ linked('region', region.id) ? tr('Editor.CompositionCurrentCCounterpart0', 'Current C counterpart · {0}', status('region', region.id)) : status('region', region.id) }}</small></button>
      <template v-for="kind in (['place', 'area'] as const)" :key="kind">
        <button v-for="item in ordered(snapshot, kind, region.id).filter(x => !search || matches(kind, x.id) || matches('region', region.id))" :key="item.id"
          type="button" class="composition-row composition-child" :class="{ 'is-counterpart': linked(kind, item.id) }" :aria-pressed="active(kind, item.id)" @click="select(kind, item.id)">
          {{ kind === 'place' ? tr('Editor.CompositionPlace2', '● Place') : tr('Editor.CompositionArea', '▱ Area') }} · {{ displayLabel(item, kind) }} <small>{{ linked(kind, item.id) ? tr('Editor.CompositionCurrentCCounterpart0', 'Current C counterpart · {0}', status(kind, item.id)) : status(kind, item.id) }}</small>
        </button>
      </template>
    </details>
    <details :open="expanded.includes(`${side}/segments`) || Boolean(search)" @toggle="toggle(`${side}/segments`, $event)">
      <summary>{{ tr('Editor.CompositionTripLevelSegments', '↝ Trip-level Segments') }}</summary>
      <button v-for="segment in ordered(snapshot, 'segment').filter(x => !search || matches('segment', x.id))" :key="segment.id" type="button"
        class="composition-row" :class="{ 'is-counterpart': linked('segment', segment.id) }" :aria-pressed="active('segment', segment.id)" @click="select('segment', segment.id)">↝ {{ displayLabel(segment) }} <small>{{ linked('segment', segment.id) ? tr('Editor.CompositionCurrentCCounterpart0', 'Current C counterpart · {0}', status('segment', segment.id)) : status('segment', segment.id) }}</small></button>
      <p v-if="!snapshot.graph.segments.length">{{ tr('Editor.CompositionNoSegmentsInThisCapturedTrip', 'No Segments in this captured Trip.') }}</p>
    </details>
  </section>
</template>
