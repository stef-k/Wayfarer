<script setup lang="ts">
import { locale, message as localize, number } from '../../../../wwwroot/js/localization/messages.js';
import { computed, inject, ref, shallowRef, watch } from 'vue';
import type { EditorTarget } from '../composables/useEditorSurface';
import { confirm } from '../composables/useConfirmDialog';
import { planningWorkspaceKey, type PlanningDraft } from './planningState';
import { describePlanningTiming as describe, planningFormText, type PlanningEntry, type PlanningTiming } from './planningTypes';
import PlanningValueInput from './PlanningValueInput.vue';

const deletedTargetName = (kind: string): string => locale === 'en-US' ? kind : localize(
  `Editor.Entity${kind[0].toUpperCase()}${kind.slice(1)}`, kind);

const props = defineProps<{ target: EditorTarget }>();
const workspace = inject(planningWorkspaceKey, null);
const draft = shallowRef<PlanningDraft | null>(null);
watch(() => props.target.identity, () => { draft.value = workspace?.open(props.target) ?? null; }, { immediate: true });
const summary = computed(() => draft.value?.original?.entries.map(entry => `${entry.label ? `${entry.label}: ` : ''}${describe(entry.when)}`).join('; '));
// Warn with the previously saved zone and the unchanged civil value.
const boundLabel = (key: string): string => key === 'start' ? localize('Editor.PlanningStart', 'Start')
  : key === 'end' ? localize('Editor.PlanningEnd', 'End') : localize('Editor.At', 'At');
const zoneLosses = computed(() => (draft.value?.entries ?? []).flatMap(entry => {
  const before = draft.value?.original?.entries.find(item => item.id === entry.id)?.when;
  return ['value', 'start', 'end'].flatMap(key => {
    const previous = key === 'value' ? before?.value : before?.[key as 'start' | 'end']?.value;
    const next = key === 'value' ? entry.when.value : entry.when[key as 'start' | 'end']?.value;
    if (!previous?.zone || !next || next.zone) return [];
    const name = entry.label || localize('Editor.PlanningEntry', 'Entry');
    return previous.selectedOffsetSeconds !== undefined
      ? [localize('Editor.PlanningZoneOffsetRemoved', '{0} ({1}): removing zone {2} and occurrence offset {3}; the civil components remain {4}.',
        name, boundLabel(key), previous.zone, number(previous.selectedOffsetSeconds), next.local)]
      : [localize('Editor.PlanningZoneRemoved', '{0} ({1}): removing zone {2}; the civil components remain {3}.', name, boundLabel(key), previous.zone, next.local)];
  });
}));
const reviewError = ref<string | null>(null);

/** Opening Planning alone creates no intention; adding a date is an explicit action. */
const add = (): void => { draft.value?.entries.push({ id: crypto.randomUUID(), when: { form: 'at', value: { precision: 'date', local: '' } } }); };
const period = (entry: PlanningEntry): void => {
  entry.when = { form: 'period', ...(entry.when.value ? { start: { state: 'known', value: entry.when.value } } : {}) };
};
/** Meaning/form edits retain all compatible authored values; allocation loss requires a visible explicit choice. */
const setForm = async (entry: PlanningEntry, form: PlanningTiming['form']): Promise<void> => {
  if (entry.when.form === form) return;
  if (!await confirm({ title: localize('Editor.PlanningChangeQuestion', 'Change timing?'),
    message: localize('Editor.PlanningChangeExplain', 'Replace {0} with {1}? This removes the current timing fields; the entry identity and optional details stay.', describe(entry.when), planningFormText(form)),
    confirmLabel: localize('Editor.PlanningChangeTiming', 'Change timing'), cancelLabel: localize('Editor.PlanningKeepTiming', 'Keep timing'), variant: 'warning' })) return;
  entry.when = form === 'at' ? { form, value: { precision: 'date', local: '' } } : { form };
};
const review = async (): Promise<void> => {
  if (!draft.value || !workspace) return;
  if (!await confirm({ title: localize('Editor.PlanningReviewQuestion', 'Review current comparison?'),
    message: localize('Editor.PlanningReviewExplain', 'Load current owner planning as the new comparison while keeping your complete draft. Saving afterward replaces that entire aggregate.'),
    confirmLabel: localize('Editor.PlanningUseComparison', 'Use current comparison'), cancelLabel: localize('Editor.PlanningKeepComparison', 'Keep original comparison'), variant: 'warning' })) return;
  try { await workspace.review(draft.value); reviewError.value = null; }
  catch { reviewError.value = localize('Editor.PlanningReviewUnavailable', 'Current planning could not be loaded. Keep the draft and try Review current comparison again.'); }
};
/** Deleted-target drafts remain individually recoverable; unresolved attempts cannot be discarded. */
const discardDeletedPlanning = async (draft: PlanningDraft): Promise<void> => {
  if (draft.pending || draft.uncertain) return;
  if (await confirm({ title: localize('Editor.PlanningDiscardDeletedQuestion', 'Discard deleted target planning?'),
    message: localize('Editor.PlanningDiscardDeletedExplain', 'Discard only this retained Planning draft? Copy its contents first if you need them. Other drafts stay.'),
    confirmLabel: localize('Editor.PlanningDiscardDraft', 'Discard this draft'), cancelLabel: localize('Editor.PlanningKeepDraft', 'Keep draft'), variant: 'warning' })) workspace?.discardDeleted(draft);
};

// Keep known rejection text visible while editing; the workspace owns drafts across remounts.
watch(() => props.target.identity, () => { reviewError.value = null; });
</script>

<template>
  <section v-for="draft in workspace?.deletedDrafts.value" :key="`${draft.kind}/${draft.id}`" class="trip-editor-panel" :aria-label="localize('Editor.DeletedTargetPlanningRecovery', 'Deleted target Planning recovery')">
    <p role="status">{{ localize('Editor.PlanningDeletedCopy', 'This {0} was deleted. Its Planning draft is retained below for copying.', deletedTargetName(draft.kind)) }}
      {{ draft.pending || draft.uncertain ? localize('Editor.PlanningDeletedUnconfirmed', 'The earlier save may still finish; its outcome remains unconfirmed.')
        : draft.confirmed ? localize('Editor.PlanningDeletedConfirmed', 'The earlier Planning save succeeded; this target remains deleted.') : '' }}
      {{ !draft.pending && !draft.uncertain ? localize('Editor.PlanningDiscardWhenDone', 'Discard this recovery when you no longer need it.') : '' }}</p>
    <label>{{ localize('Editor.PlanningRetainedLabel', 'Retained Planning ({0})', draft.id) }}
      <textarea readonly :value="JSON.stringify(draft.entries, null, 2)"></textarea>
    </label>
    <button type="button" class="btn btn-outline-secondary btn-sm" :disabled="draft.pending || draft.uncertain" @click="discardDeletedPlanning(draft)">{{ localize("Editor.DiscardThisPlanningDraft", "Discard this Planning draft") }}</button>
  </section>

  <details v-if="draft" class="trip-editor-panel" data-planning>
    <summary>{{ localize("Editor.Planning", "Planning") }} <span v-if="summary">· {{ summary }}</span></summary>
    <p>{{ localize("Editor.SavePlanningSavesOnlyThisObjectS", "Save Planning saves only this object’s Planning. Trip settings, Notes and routes use their own save actions.") }}</p>
    <p v-for="message in workspace?.diagnostics(target)" :key="message" role="status">{{ message }} {{ localize("Editor.StoredPlanningIsRetainedReviewExplicitlyBefore", "Stored planning is retained; review explicitly before changing it.") }}</p>
    <p v-if="draft.error" role="alert">{{ draft.error }}</p>
    <p v-if="reviewError" role="alert">{{ reviewError }}</p>
    <p v-for="loss in zoneLosses" :key="loss" role="status">{{ loss }}</p>
    <p v-if="draft.confirmed" role="status">{{ draft.deleted ? localize('Editor.PlanningDeletedConfirmed', 'The earlier Planning save succeeded; this target remains deleted.')
      : workspace?.dirty(draft) ? localize('Editor.PlanningSavedNewer', 'Earlier planning saved; newer changes remain unsaved.') : localize('Editor.PlanningSaved', 'Planning saved.') }}</p>
    <fieldset v-for="(entry, index) in draft.entries" :key="entry.id" class="trip-editor-settings-group">
      <legend>{{ entry.label || localize('Editor.PlanningEntryDate', 'Date {0}', number(index + 1)) }}</legend>
      <PlanningValueInput v-if="entry.when.form === 'at' && entry.when.value" :value="entry.when.value" :label="localize('Editor.At', 'At')" />
      <template v-if="entry.when.form === 'period'">
        <template v-for="bound in ['start', 'end'] as const" :key="bound">
          <PlanningValueInput v-if="entry.when[bound]?.value" :value="entry.when[bound]!.value!" :label="bound === 'end' && entry.kind === 'stay' ? localize('Editor.PlanningCheckout', 'Checkout') : boundLabel(bound)" />
          <button v-else type="button" class="btn btn-outline-light btn-sm" @click="entry.when[bound] = { state: 'known', value: { precision: 'date', local: '' } }">{{ bound === 'start' ? localize('Editor.PlanningAddStart', 'Add start date') : localize('Editor.AddEndDate', 'Add end date') }}</button>
          <label class="trip-editor-field"><span>{{ bound === 'start' ? localize('Editor.PlanningStartInfo', 'Start information') : localize('Editor.PlanningEndInfo', 'End information') }}</span>
            <select :value="entry.when[bound]?.state ?? 'unspecified'" @change="entry.when[bound] = ($event.target as HTMLSelectElement).value === 'known' ? { state: 'known', value: entry.when[bound]?.value ?? { precision: 'date', local: '' } } : { state: ($event.target as HTMLSelectElement).value as 'unknown' | 'flexible' | 'unspecified' }">
              <option value="unspecified">{{ localize("Editor.Unspecified", "Unspecified") }}</option><option value="known">{{ localize("Editor.Known", "Known") }}</option><option value="unknown">{{ localize("Editor.ExplicitlyUnknown", "Explicitly unknown") }}</option><option value="flexible">{{ localize("Editor.DeliberatelyFlexible", "Deliberately flexible") }}</option>
            </select>
          </label>
        </template>
        <small>{{ localize("Editor.EndIsAnEndpointNotALatest", "End is an endpoint, not a latest deadline.") }}</small>
      </template>
      <button v-if="entry.when.form === 'at'" type="button" class="btn btn-outline-light btn-sm" @click="period(entry)">{{ localize("Editor.AddEndDate", "Add end date") }}</button>
      <details><summary>{{ localize("Editor.More", "More") }}</summary>
        <label class="trip-editor-field"><span>{{ localize("Editor.LabelOptional", "Label (optional)") }}</span><input v-model="entry.label" maxlength="200" @change="() => { if (!entry.label) delete entry.label }" /></label>
        <label class="trip-editor-field"><span>{{ localize("Editor.MeaningOptional", "Meaning (optional)") }}</span><select v-model="entry.kind" @change="() => { if (!entry.kind) delete entry.kind }">
          <option :value="undefined">{{ localize("Editor.Unspecified", "Unspecified") }}</option><option value="window">{{ localize("Editor.PlanningWindow", "Planning window") }}</option>
          <option v-if="draft.kind !== 'segment'" value="presence">{{ localize("Editor.ActivityRequiringPresence", "Activity requiring presence") }}</option><option v-if="draft.kind !== 'segment'" value="stay">{{ localize("Editor.StayBaseAvailability", "Stay/base availability") }}</option>
          <option v-if="draft.kind === 'trip' || draft.kind === 'segment'" value="travel">{{ localize("Editor.Journey", "Journey") }}</option>
        </select></label>
        <label class="trip-editor-field"><span>{{ localize("Editor.ElapsedAllocationInMinutesOptional", "Elapsed allocation in minutes (optional)") }}</span><input v-model.number="entry.allocationMinutes" type="number" min="0" step="1" @change="() => { if (String(entry.allocationMinutes) === '') delete entry.allocationMinutes }" /></label>
        <small v-if="entry.allocationMinutes !== undefined">{{ localize("Editor.AllocationIsAllowedOnAtOrUndated", "Allocation is allowed on At or undated, unclassified/presence/journey entries. Remove it explicitly before choosing a window, stay or period.") }}</small>
        <label class="trip-editor-field"><span>{{ localize("Editor.Timing", "Timing") }}</span><select :value="entry.when.form" @change="setForm(entry, ($event.target as HTMLSelectElement).value as PlanningTiming['form'])">
          <option value="at">{{ localize("Editor.At", "At") }}</option><option v-if="entry.when.form === 'period'" value="period">{{ localize("Editor.Period", "Period") }}</option><option value="unspecified">{{ localize("Editor.UndatedUnspecified", "Undated, unspecified") }}</option><option value="unknown">{{ localize("Editor.ExplicitlyUnknown", "Explicitly unknown") }}</option><option value="flexible">{{ localize("Editor.DeliberatelyFlexible", "Deliberately flexible") }}</option>
        </select></label>
      </details>
      <button type="button" class="btn btn-outline-danger btn-sm" @click="draft.entries.splice(index, 1)">{{ localize("Editor.RemoveEntry", "Remove entry") }}</button>
    </fieldset>
    <button type="button" class="btn btn-outline-light btn-sm" :disabled="draft.entries.length >= 128" @click="add">{{ draft.entries.length ? localize('Editor.PlanningAddAnother', 'Add another date') : localize('Editor.PlanningAddDate', 'Add date') }}</button>
    <button type="button" class="btn btn-primary btn-sm" :disabled="draft.pending || draft.uncertain || !workspace?.dirty(draft)" @click="workspace?.save(draft)">{{ localize("Editor.SavePlanning", "Save planning") }}</button>
    <button v-if="draft.error && !draft.uncertain" type="button" class="btn btn-outline-secondary btn-sm" @click="review">{{ localize("Editor.ReviewCurrentComparison", "Review current comparison") }}</button>
  </details>
</template>
