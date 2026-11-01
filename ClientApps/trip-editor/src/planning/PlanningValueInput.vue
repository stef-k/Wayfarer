<script setup lang="ts">
import { computed } from 'vue';
import { confirm } from '../composables/useConfirmDialog';
import type { PlanningValue } from './planningTypes';

const props = defineProps<{ value: PlanningValue; label: string }>();
const datePart = computed({ get: () => props.value.local.split('T')[0] ?? '', set: value => {
  props.value.local = props.value.precision === 'dateTime' ? `${value}T${props.value.local.split('T')[1] ?? ''}` : value;
} });
const timePart = computed({ get: () => props.value.local.split('T')[1] ?? '', set: value => { props.value.local = `${datePart.value}T${value}`; } });

/** Precision loss is an explicit reviewed edit; adding a clock leaves the date and optional zone intact. */
const precision = async (next: PlanningValue['precision']): Promise<void> => {
  if (next === props.value.precision) return;
  if ((next === 'month' || props.value.precision === 'dateTime') && !await confirm({ title: 'Change precision?',
    message: `Changing ${props.label} from ${props.value.local}${props.value.zone ? ` (${props.value.zone})` : ''} to ${next} removes its finer components and zone/occurrence choice.`,
    confirmLabel: 'Change precision', cancelLabel: 'Keep current value', variant: 'warning' })) return;
  const current = datePart.value;
  props.value.precision = next;
  props.value.local = next === 'month' ? current.slice(0, 7) : next === 'dateTime' ? `${current}T` : current;
  if (next !== 'dateTime') { delete props.value.zone; delete props.value.selectedOffsetSeconds; }
};
</script>

<template>
  <fieldset class="trip-editor-settings-group">
    <legend>{{ label }}</legend>
    <label class="trip-editor-field"><span>{{ value.precision === 'month' ? 'Month' : 'Date' }}</span>
      <input v-model="datePart" :type="value.precision === 'month' ? 'month' : 'date'" min="0001-01-01" max="9999-12-31" />
    </label>
    <button v-if="value.precision !== 'month'" type="button" class="btn btn-outline-secondary btn-sm" @click="precision('month')">Month only</button>
    <button v-else type="button" class="btn btn-outline-secondary btn-sm" @click="precision('date')">Choose exact date</button>
    <button v-if="value.precision === 'date'" type="button" class="btn btn-outline-secondary btn-sm" @click="precision('dateTime')">Add time</button>
    <template v-if="value.precision === 'dateTime'">
      <label class="trip-editor-field"><span>Time</span><input v-model="timePart" type="time" step="60" /></label>
      <details><summary>Zone and occurrence (optional)</summary>
        <label class="trip-editor-field"><span>UTC or IANA zone</span><input :value="value.zone ?? ''" placeholder="Unspecified" @input="value.zone = ($event.target as HTMLInputElement).value || undefined; if (!value.zone) delete value.selectedOffsetSeconds" /></label>
        <label class="trip-editor-field"><span>Occurrence UTC offset in seconds (optional)</span><input v-model.number="value.selectedOffsetSeconds" type="number" step="1" @change="() => { if (String(value.selectedOffsetSeconds) === '') delete value.selectedOffsetSeconds }" /></label>
        <small>Leave occurrence unresolved, or supply the zone's offset for the intended occurrence of a repeated clock. No zone is inferred.</small>
      </details>
      <button type="button" class="btn btn-outline-secondary btn-sm" @click="precision('date')">Remove time</button>
    </template>
    <small>{{ value.precision }} precision<span v-if="value.precision === 'dateTime'"> · {{ value.zone || 'zone unspecified' }}</span></small>
  </fieldset>
</template>
