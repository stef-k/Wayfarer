<script setup lang="ts">
import { ref } from 'vue';
import type { EditorSegment } from '../types';
import type { CheckItem, CheckSetup, CheckTransfer } from './planningCheckTypes';

const props = defineProps<{ setup: CheckSetup; items: CheckItem[]; segments: Record<string, EditorSegment> }>();
const from = ref('');
const to = ref('');
const label = (item: CheckItem): string => `${item.name} · ${item.entry.label || item.entry.id.slice(0, 8)}`;
/** Selecting a pair establishes only this requested movement check, never schedule order or stored route links. */
const add = (): void => {
  if (!from.value || !to.value || from.value === to.value || props.setup.transfers.length >= 64) return;
  if (!props.setup.transfers.some(t => t.from === from.value && t.to === to.value))
    props.setup.transfers.push({ from: from.value, to: to.value, relevant: false, allowance: 'unknown', connection: 'unknown' });
};
/** Explicit input changes are new assessments; old directional relevance bindings must not silently survive them. */
const changed = (transfer: CheckTransfer): void => {
  transfer.dependency = null;
  for (const key of ['estimateMinutes', 'allowanceMinutes', 'connectionMinutes'] as const)
    if (typeof transfer[key] !== 'number') transfer[key] = null;
};
const whatIf = (transfer: CheckTransfer, local: string): void => {
  transfer.whatIfRelease = local ? { ...transfer.whatIfRelease, precision: 'dateTime', local } : null;
  transfer.whatIfAt = null;
  transfer.whatIfDependency = null;
};
</script>

<template>
  <fieldset class="plan-check__movement"><legend>Movement and timing</legend>
    <p>Choose the intended release → arrival pair. No saved Segment or provider call is needed.</p>
    <label class="trip-editor-field"><span>Release from entry</span><select v-model="from"><option value="">Leave unresolved</option><option v-for="item in items.filter(i => i.selected)" :key="item.key" :value="item.key">{{ label(item) }}</option></select></label>
    <label class="trip-editor-field"><span>Arrival at entry</span><select v-model="to"><option value="">Leave unresolved</option><option v-for="item in items.filter(i => i.selected && i.key !== from)" :key="item.key" :value="item.key">{{ label(item) }}</option></select></label>
    <button type="button" class="btn btn-outline-light btn-sm" :disabled="!from || !to || from === to" @click="add">Assess this movement</button>
    <fieldset v-for="(transfer, index) in setup.transfers" :key="`${transfer.from}|${transfer.to}`">
      <legend>{{ items.find(i => i.key === transfer.from)?.name }} → {{ items.find(i => i.key === transfer.to)?.name }}</legend>
      <label class="trip-editor-field"><span>Estimate source</span><select v-model="transfer.segmentId" @change="changed(transfer)"><option :value="null">My estimate for this check</option><option v-for="segment in Object.values(segments)" :key="segment.id" :value="segment.id">{{ segment.mode }} · {{ segment.estimatedDurationMinutes ?? 'Unknown' }} min · {{ segment.estimatedDurationSource }} · {{ segment.fromPlaceId }} → {{ segment.toPlaceId }}</option></select></label>
      <label v-if="!transfer.segmentId" class="trip-editor-field"><span>Travel estimate in minutes</span><input v-model.number="transfer.estimateMinutes" type="number" min="0" step="1" @change="changed(transfer)" /></label>
      <label><input v-model="transfer.relevant" type="checkbox" @change="changed(transfer)" /> This estimate is relevant to this direction, locations and travel mode</label>
      <template v-for="extra in ['allowance', 'connection'] as const" :key="extra">
        <label class="trip-editor-field"><span>{{ extra === 'allowance' ? 'Rest / buffer allowance' : 'Necessary connection information' }}</span><select v-model="transfer[extra]" @change="changed(transfer)"><option value="unknown">Unknown / Skip</option><option value="none">None needed for this check</option><option value="included">Already included in the selected estimate</option><option value="additional">Additional minutes, counted once</option></select></label>
        <label v-if="transfer[extra] === 'additional'" class="trip-editor-field"><span>Additional {{ extra }} minutes (not already counted in estimate or gap)</span><input v-if="extra === 'allowance'" v-model.number="transfer.allowanceMinutes" type="number" min="0" step="1" @change="changed(transfer)" /><input v-else v-model.number="transfer.connectionMinutes" type="number" min="0" step="1" @change="changed(transfer)" /></label>
      </template>
      <details><summary>What if the release is different?</summary>
        <p>A current-release assumption lasts at most 30 minutes on this UTC day and changes no intention, estimate or observed history.</p>
        <label class="trip-editor-field"><span>What-if release clock</span><input type="datetime-local" :value="transfer.whatIfRelease?.local ?? ''" @change="whatIf(transfer, ($event.target as HTMLInputElement).value)" /></label>
        <label v-if="transfer.whatIfRelease" class="trip-editor-field"><span>What-if release zone</span><input v-model="transfer.whatIfRelease.zone" placeholder="UTC or IANA zone; no device default" @change="() => { if (transfer.whatIfRelease && !transfer.whatIfRelease.zone) delete transfer.whatIfRelease.zone; transfer.whatIfAt = null; transfer.whatIfDependency = null }" /></label>
      </details>
      <button type="button" class="btn btn-outline-secondary btn-sm" @click="setup.transfers.splice(index, 1)">Remove this movement check</button>
    </fieldset>
    <small>Other saved activities are not automatically deducted from this pair's gap. Select the relevant release/arrival entries and account for each rest once.</small>
  </fieldset>
</template>
