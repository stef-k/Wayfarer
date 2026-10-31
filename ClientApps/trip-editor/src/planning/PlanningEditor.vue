<script setup lang="ts">
import { computed, inject, ref, shallowRef, watch } from 'vue';
import type { EditorTarget } from '../composables/useEditorSurface';
import { confirm } from '../composables/useConfirmDialog';
import { planningWorkspaceKey, type PlanningDraft } from './planningState';
import type { PlanningEntry, PlanningTiming } from './planningTypes';
import PlanningValueInput from './PlanningValueInput.vue';

const props = defineProps<{ target: EditorTarget }>();
const workspace = inject(planningWorkspaceKey, null);
const draft = shallowRef<PlanningDraft | null>(null);
watch(() => props.target.identity, () => { draft.value = workspace?.open(props.target) ?? null; }, { immediate: true });
const summary = computed(() => draft.value?.original?.entries.map(entry => `${entry.label ? `${entry.label}: ` : ''}${describe(entry.when)}`).join('; '));
const zoneLosses = computed(() => (draft.value?.entries ?? []).flatMap(entry => {
  const before = draft.value?.original?.entries.find(item => item.id === entry.id)?.when;
  return ['value', 'start', 'end'].flatMap(key => {
    const previous = key === 'value' ? before?.value : before?.[key as 'start' | 'end']?.value;
    const next = key === 'value' ? entry.when.value : entry.when[key as 'start' | 'end']?.value;
    return previous?.zone && next && !next.zone
      ? [`${entry.label || 'Entry'} ${key}: removing zone ${previous.zone}${previous.selectedOffsetSeconds !== undefined ? ` and occurrence offset ${previous.selectedOffsetSeconds}` : ''}; the civil components remain ${next.local}.`] : [];
  });
}));
const reviewError = ref<string | null>(null);

/** One literal summary; no inferred occupancy, nights or travel interpretation. */
function describe(when: PlanningTiming): string {
  const value = (item: NonNullable<PlanningTiming['value']>) => `${item.local} (${item.precision}${item.precision === 'dateTime' ? `, ${item.zone ?? 'zone unspecified'}` : ''})`;
  if (when.form === 'at' && when.value) return value(when.value);
  if (when.form === 'period') return `Start ${when.start?.value ? value(when.start.value) : when.start?.state ?? 'unspecified'} → End ${when.end?.value ? value(when.end.value) : when.end?.state ?? 'unspecified'}`;
  return when.form;
}

/** Opening Planning alone creates no intention; adding a date is an explicit action. */
const add = (): void => { draft.value?.entries.push({ id: crypto.randomUUID(), when: { form: 'at', value: { precision: 'date', local: '' } } }); };
const period = (entry: PlanningEntry): void => {
  entry.when = { form: 'period', ...(entry.when.value ? { start: { state: 'known', value: entry.when.value } } : {}) };
};
/** Meaning/form edits retain all compatible authored values; allocation loss requires a visible explicit choice. */
const setForm = async (entry: PlanningEntry, form: PlanningTiming['form']): Promise<void> => {
  if (entry.when.form === form) return;
  if (!await confirm({ title: 'Change timing?', message: `Replace ${describe(entry.when)} with ${form}? This removes the current timing fields; the entry identity and optional details stay.`,
    confirmLabel: 'Change timing', cancelLabel: 'Keep timing', variant: 'warning' })) return;
  entry.when = form === 'at' ? { form, value: { precision: 'date', local: '' } } : { form };
};
const review = async (): Promise<void> => {
  if (!draft.value || !workspace) return;
  if (!await confirm({ title: 'Review current comparison?', message: 'Load current owner planning as the new comparison while keeping your complete draft. Saving afterward replaces that entire aggregate.',
    confirmLabel: 'Use current comparison', cancelLabel: 'Keep original comparison', variant: 'warning' })) return;
  try { await workspace.review(draft.value); reviewError.value = null; }
  catch (error) { reviewError.value = error instanceof Error ? error.message : 'Review unavailable.'; }
};
// Keep known rejection text visible while editing; the workspace owns drafts across remounts.
watch(() => props.target.identity, () => { reviewError.value = null; });
</script>

<template>
  <details v-if="draft" class="trip-editor-panel" data-planning>
    <summary>Planning <span v-if="summary">· {{ summary }}</span></summary>
    <p v-for="message in workspace?.diagnostics(target)" :key="message" role="status">{{ message }} Stored planning is retained; review explicitly before changing it.</p>
    <p v-if="draft.error" role="alert">{{ draft.error }}</p>
    <p v-if="reviewError" role="alert">{{ reviewError }}</p>
    <p v-for="loss in zoneLosses" :key="loss" role="status">{{ loss }}</p>
    <p v-if="draft.confirmed" role="status">{{ workspace?.dirty(draft) ? 'Earlier planning saved; newer changes remain unsaved.' : 'Planning saved.' }}</p>
    <fieldset v-for="(entry, index) in draft.entries" :key="entry.id" class="trip-editor-settings-group">
      <legend>{{ entry.label || `Date ${index + 1}` }}</legend>
      <PlanningValueInput v-if="entry.when.form === 'at' && entry.when.value" :value="entry.when.value" label="At" />
      <template v-if="entry.when.form === 'period'">
        <template v-for="bound in ['start', 'end'] as const" :key="bound">
          <PlanningValueInput v-if="entry.when[bound]?.value" :value="entry.when[bound]!.value!" :label="bound === 'end' && entry.kind === 'stay' ? 'Checkout' : bound === 'start' ? 'Start' : 'End'" />
          <button v-else type="button" class="btn btn-outline-light btn-sm" @click="entry.when[bound] = { state: 'known', value: { precision: 'date', local: '' } }">{{ bound === 'start' ? 'Add start date' : 'Add end date' }}</button>
          <label class="trip-editor-field"><span>{{ bound }} information</span>
            <select :value="entry.when[bound]?.state ?? 'unspecified'" @change="entry.when[bound] = ($event.target as HTMLSelectElement).value === 'known' ? { state: 'known', value: entry.when[bound]?.value ?? { precision: 'date', local: '' } } : { state: ($event.target as HTMLSelectElement).value as 'unknown' | 'flexible' | 'unspecified' }">
              <option value="unspecified">Unspecified</option><option value="known">Known</option><option value="unknown">Explicitly unknown</option><option value="flexible">Deliberately flexible</option>
            </select>
          </label>
        </template>
        <small>End is an endpoint, not a latest deadline.</small>
      </template>
      <button v-if="entry.when.form === 'at'" type="button" class="btn btn-outline-light btn-sm" @click="period(entry)">Add end date</button>
      <details><summary>More</summary>
        <label class="trip-editor-field"><span>Label (optional)</span><input v-model="entry.label" maxlength="200" @change="() => { if (!entry.label) delete entry.label }" /></label>
        <label class="trip-editor-field"><span>Meaning (optional)</span><select v-model="entry.kind" @change="() => { if (!entry.kind) delete entry.kind }">
          <option :value="undefined">Unspecified</option><option value="window">Planning window</option>
          <option v-if="draft.kind !== 'segment'" value="presence">Activity requiring presence</option><option v-if="draft.kind !== 'segment'" value="stay">Stay/base availability</option>
          <option v-if="draft.kind === 'trip' || draft.kind === 'segment'" value="travel">Journey</option>
        </select></label>
        <label class="trip-editor-field"><span>Elapsed allocation in minutes (optional)</span><input v-model.number="entry.allocationMinutes" type="number" min="0" step="1" @change="() => { if (String(entry.allocationMinutes) === '') delete entry.allocationMinutes }" /></label>
        <small v-if="entry.allocationMinutes !== undefined">Allocation is allowed on At or undated, unclassified/presence/journey entries. Remove it explicitly before choosing a window, stay or period.</small>
        <label class="trip-editor-field"><span>Timing</span><select :value="entry.when.form" @change="setForm(entry, ($event.target as HTMLSelectElement).value as PlanningTiming['form'])">
          <option value="at">At</option><option v-if="entry.when.form === 'period'" value="period">Period</option><option value="unspecified">Undated, unspecified</option><option value="unknown">Explicitly unknown</option><option value="flexible">Deliberately flexible</option>
        </select></label>
      </details>
      <button type="button" class="btn btn-outline-danger btn-sm" @click="draft.entries.splice(index, 1)">Remove entry</button>
    </fieldset>
    <button type="button" class="btn btn-outline-light btn-sm" :disabled="draft.entries.length >= 128" @click="add">{{ draft.entries.length ? 'Add another date' : 'Add date' }}</button>
    <button type="button" class="btn btn-primary btn-sm" :disabled="draft.pending || draft.uncertain || !workspace?.dirty(draft)" @click="workspace?.save(draft)">Save planning</button>
    <button v-if="draft.error && !draft.uncertain" type="button" class="btn btn-outline-secondary btn-sm" @click="review">Review current comparison</button>
  </details>
</template>
