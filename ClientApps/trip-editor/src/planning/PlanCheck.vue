<script setup lang="ts">
import { computed, inject, onUnmounted } from 'vue';
import type { EditorTripState } from '../types';
import type { EditorSurfaceController } from '../composables/useEditorSurface';
import { planningWorkspaceKey } from './planningState';
import { createPlanningReview } from './planningReviewState';
import type { CheckEntryInput, CheckItem } from './planningCheckTypes';
import { describePlanningTiming } from './planningTypes';
import CheckMovement from './CheckMovement.vue';
import './planCheck.css';

const props = defineProps<{ state: EditorTripState; endpoint: string; antiforgery: string; ordinaryDirty: boolean; surface: EditorSurfaceController }>();
const workspace = inject(planningWorkspaceKey, null);
const review = createPlanningReview({ state: () => props.state, endpoint: props.endpoint, antiforgery: props.antiforgery,
  workspace, ordinaryDirty: () => props.ordinaryDirty || props.surface.isActiveTargetDirty(), saveOrdinary: () => props.surface.saveActiveTarget() });
const { setup, result, busy, stale, choices, opened, error, storageNotice, dismissed, showDismissed, held, summary, hasDrafts } = review;
onUnmounted(review.dispose);
const items = computed(() => result.value?.inventory ?? []);
const byKey = computed(() => Object.fromEntries(items.value.map(i => [i.key, i])));
const scope = computed(() => setup.value.selection === null ? 'Whole Trip · every current saved entry' : 'Explicit selection · remainder unassessed');
const findings = computed(() => (result.value?.findings ?? []).filter(f => showDismissed.value || !dismissed.value.includes(f.id)));
const label = (item: CheckItem): string => `${item.name} · ${item.entry.label || describePlanningTiming(item.entry.when)}`;
const names = (keys: string[]): string => keys.map(key => byKey.value[key] ? label(byKey.value[key]!) : 'Removed entry').join(' → ');
/** Local refinement creates no planning aggregate and cannot become a protected save original. */
const input = (key: string): CheckEntryInput => setup.value.entries[key] ??= {};
const interpret = (key: string, field: keyof CheckEntryInput, value: unknown): void => {
  const next = { ...input(key), [field]: value === '' ? null : value };
  if (field === 'context') next.contextDependency = null;
  if (field === 'occurrences') next.occurrenceDependency = null;
  setup.value.entries[key] = next;
};
/** Selection is temporary; excluded entries remain visible and new entries cannot acquire hidden certification. */
const select = (item: CheckItem, checked: boolean): void => {
  const keys = setup.value.selection ?? items.value.map(i => i.key);
  setup.value.selection = checked ? [...new Set([...keys, item.key])] : keys.filter(k => k !== item.key);
  setup.value.travellerDependency = null;
};
const selectContext = (item: CheckItem, key: string, checked: boolean): void => {
  const keys = input(item.key).context ?? [];
  interpret(item.key, 'context', checked ? [...new Set([...keys, key])] : keys.filter(k => k !== key));
};
const wholeTrip = (): void => { setup.value.selection = null; review.request(); };
const physicalKey = (keys: string[]): string => [...keys].sort().join('|');
const incompatible = (keys: string[], checked = true): void => {
  if (checked) setup.value.incompatible[physicalKey(keys)] = null;
  else delete setup.value.incompatible[physicalKey(keys)];
};
/** Saved endpoint zones need no question; only absent clocks or interpreted civil windows can use this input. */
const needsZone = (item: CheckItem): boolean => {
  const timing = item.entry.when;
  const values = [timing.value, timing.start?.value, timing.end?.value].filter(v => v != null);
  return values.some(v => v.precision === 'dateTime' && !v.zone)
    || (item.entry.kind ?? setup.value.entries[item.key]?.kind) === 'window' && values.some(v => v.precision !== 'dateTime');
};
/** Only checks affected by a fold ask for an occurrence; saved explicit choices remain authoritative. */
const occurrenceValues = (item: CheckItem) => {
  if (!Object.values(setup.value.entries[item.key]?.occurrences ?? {}).some(v => v != null)
    && !result.value?.findings.some(f => f.entries.includes(item.key) && /fold|occurrence/.test(f.result.message))) return [];
  const timing = item.entry.when;
  return (['value', 'start', 'end'] as const).flatMap(key => {
    const value = key === 'value' ? timing.value : timing[key]?.value;
    return value?.precision === 'dateTime' && value.selectedOffsetSeconds == null ? [{ key, value }] : [];
  });
};
/** Skipping closes only this question; Unknown and its count remain in the displayed review. */
const skip = (event: Event): void => {
  const details = (event.currentTarget as HTMLElement).closest('details');
  if (details) details.open = false;
};
</script>

<template>
  <section class="trip-editor-panel plan-check" aria-label="Check plan">
    <div class="trip-editor-panel__line">
      <strong>Optional planning checks</strong>
      <button type="button" class="btn btn-outline-light btn-sm" :disabled="busy" @click="wholeTrip">Check plan</button>
    </div>
    <button v-if="surface.activeTarget.value?.mode === 'edit'" type="button" class="btn btn-outline-secondary btn-sm" :disabled="busy" @click="review.request(surface.activeTarget.value)">Check this entity only</button>
    <p>Editing and saving need no assessment or warning acknowledgement.</p>
    <template v-if="opened">
      <p><strong>{{ scope }}</strong>. Checks use a fresh saved owner snapshot.</p>
      <p v-if="hasDrafts" role="status">Unsaved or unconfirmed drafts are separate and are not assessed as saved facts.</p>
      <div v-if="choices" class="plan-check__choices" role="group" aria-label="Choose version to check">
        <p>Your drafts have not been saved by Check plan.</p>
        <button type="button" class="btn btn-primary btn-sm" :disabled="held" @click="review.saveAndCheck">Save and check</button>
        <button type="button" class="btn btn-outline-light btn-sm" @click="review.run">Check saved version</button>
        <button type="button" class="btn btn-outline-secondary btn-sm" @click="choices = false; opened = false">Return to editing</button>
      </div>
      <p v-if="error" role="alert">{{ error }}</p>
      <p v-if="busy" role="status">Checking saved information…</p>
      <p v-if="stale" role="status">This review is stale: saved facts, drafts or check inputs changed. Check again for current results.</p>
      <template v-if="result">
        <p class="plan-check__summary" role="status">{{ summary.attention }} findings · {{ summary.completed }} completed checks · {{ summary.unknown }} Unknown · {{ summary.excluded }} entries explicitly unassessed</p>
        <p>Results apply only to the listed entries and assumptions. They do not certify whole-Trip consistency, arrival or attention compatibility.</p>
        <details v-if="result.reused.length || result.invalidated.length">
          <summary>Using last inputs · review/change</summary>
          <p v-for="(value, index) in result.reused" :key="`reuse-${index}`">Reused: {{ value }}</p>
          <p v-for="(value, index) in result.invalidated" :key="`invalid-${index}`">Reopened: {{ value }}</p>
        </details>
        <div class="plan-check__results" aria-label="Planning results" aria-live="polite">
          <article v-for="finding in findings" :key="finding.id" class="plan-check__finding" :data-status="finding.result.status">
            <strong>{{ finding.result.status === 'unknown' ? 'Unknown' : finding.result.status }} · {{ finding.check }}</strong>
            <small>{{ names(finding.entries) }}</small>
            <p>{{ finding.result.message }}</p>
            <button v-if="!dismissed.includes(finding.id) && !['completed', 'excluded'].includes(finding.result.status)" type="button" class="btn btn-outline-secondary btn-sm" @click="dismissed.push(finding.id)">Dismiss for this review</button>
            <button v-if="finding.check === 'physical' && finding.result.status === 'unknown' && finding.result.message.includes('incompatible locations') && finding.entries.length === 2" type="button" class="btn btn-outline-light btn-sm" @click="incompatible(finding.entries)">These locations require separate presence</button>
          </article>
        </div>
        <p v-if="dismissed.length">{{ dismissed.length }} collapsed findings; counts retained. <button type="button" class="btn btn-link btn-sm" @click="showDismissed = !showDismissed">{{ showDismissed ? 'Hide dismissed' : 'Show dismissed' }}</button></p>
        <details class="plan-check__inputs">
          <summary>Coverage and targeted check inputs · {{ items.length }} saved entries</summary>
          <p>Use saved facts first. Supply only facts an Unknown result needs. Leave unresolved/Skip retains Unknown. Inputs here stay private on this device and never change planning or exports.</p>
          <label v-if="setup.singleTraveller || result.findings.some(f => f.check === 'physical' && f.result.message.includes('single-traveller'))"><input v-model="setup.singleTraveller" type="checkbox" @change="setup.travellerDependency = null" /> One traveller for this selected scope</label>
          <label v-for="finding in result.findings.filter(f => f.check === 'physical' && f.entries.length === 2 && (f.result.message.includes('incompatible locations') || Object.hasOwn(setup.incompatible, physicalKey(f.entries))))" :key="finding.id"><input type="checkbox" :checked="Object.hasOwn(setup.incompatible, physicalKey(finding.entries))" @change="incompatible(finding.entries, ($event.target as HTMLInputElement).checked)" /> {{ names(finding.entries) }}: locations require separate presence</label>
          <p v-if="!items.length">No saved planning entries in this Trip.</p>
          <details v-for="item in items" :key="item.key" class="plan-check__entry">
            <summary>{{ label(item) }} · {{ item.selected ? 'assessed scope' : 'unassessed remainder' }}</summary>
            <p>Saved: {{ describePlanningTiming(item.entry.when) }} · {{ item.entry.kind || 'meaning unspecified' }}</p>
            <label><input type="checkbox" :checked="setup.selection === null || setup.selection.includes(item.key)" @change="select(item, ($event.target as HTMLInputElement).checked)" /> Assess this entry for this check</label>
            <label v-if="!item.entry.kind" class="trip-editor-field"><span>Meaning for this check only</span>
              <select :value="setup.entries[item.key]?.kind ?? ''" @change="interpret(item.key, 'kind', ($event.target as HTMLSelectElement).value)">
                <option value="">Leave unresolved/Skip</option><option value="window">Planning window</option><option value="presence">Activity requiring presence</option><option value="stay">Stay/base availability</option><option value="travel">Journey</option>
              </select>
            </label>
            <label v-if="needsZone(item)" class="trip-editor-field"><span>Needed zone / civil-window interpretation for this check</span><input :value="setup.entries[item.key]?.zone ?? ''" placeholder="Leave unresolved, or UTC / IANA zone" @change="interpret(item.key, 'zone', ($event.target as HTMLInputElement).value)" /></label>
            <small v-if="needsZone(item)">Applies only where a zone is absent, or to civil-window edges. Saved zones and fold choices stay authoritative.</small>
            <label v-for="occurrence in occurrenceValues(item)" :key="occurrence.key" class="trip-editor-field"><span>{{ occurrence.key }} occurrence UTC offset in seconds for this check · {{ occurrence.value.local }} ({{ occurrence.value.zone || setup.entries[item.key]?.zone || 'zone unspecified' }})</span><input type="number" step="1" :value="setup.entries[item.key]?.occurrences?.[occurrence.key] ?? ''" @change="interpret(item.key, 'occurrences', { ...setup.entries[item.key]?.occurrences, [occurrence.key]: ($event.target as HTMLInputElement).value === '' ? null : Number(($event.target as HTMLInputElement).value) })" /></label>
            <label v-if="item.entry.when.form === 'at' && item.entry.when.value?.precision === 'dateTime' && item.entry.allocationMinutes == null && !['window', 'stay'].includes(item.entry.kind ?? setup.entries[item.key]?.kind ?? '')" class="trip-editor-field"><span>Needed elapsed dwell for this check (minutes)</span><input type="number" min="0" step="1" :value="setup.entries[item.key]?.allocationMinutes ?? ''" @change="interpret(item.key, 'allocationMinutes', ($event.target as HTMLInputElement).value === '' ? null : Number(($event.target as HTMLInputElement).value))" /></label>
            <fieldset v-if="item.parentWindows.length"><legend>Choose explicitly relevant parent-window union</legend>
              <template v-for="key in item.parentWindows" :key="key">
                <label v-if="byKey[key]?.entry.kind === 'window' || setup.entries[key]?.kind === 'window'"><input type="checkbox" :checked="setup.entries[item.key]?.context?.includes(key) ?? false" @change="selectContext(item, key, ($event.target as HTMLInputElement).checked)" /> {{ byKey[key] ? label(byKey[key]!) : key }}</label>
              </template>
              <small>Missing meanings can be clarified in the corresponding entry above. Unselected ownership imposes no temporal constraint; union gaps remain.</small>
            </fieldset>
            <button type="button" class="btn btn-outline-secondary btn-sm" @click="skip">Leave unresolved/Skip</button>
          </details>
          <CheckMovement :setup="setup" :items="items" :segments="state.segmentsById" :places="state.placesById" />
          <button type="button" class="btn btn-primary btn-sm" :disabled="busy" @click="review.run">Use for this check · check saved version</button>
        </details>
        <button type="button" class="btn btn-outline-light btn-sm" :disabled="busy" @click="review.request()">Check again</button>
        <button type="button" class="btn btn-outline-secondary btn-sm" @click="review.clear">Clear latest private inputs</button>
      </template>
      <p v-if="storageNotice" role="status">{{ storageNotice }}</p>
    </template>
  </section>
</template>
