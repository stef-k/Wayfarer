<script setup lang="ts">
import { computed, ref } from 'vue';
import type { EditorRegion, EditorSegmentDraft, EditorTripState, Guid } from '../types';
import RichNotesEditor from './RichNotesEditor.vue';
import SegmentWaypointEditor from './SegmentWaypointEditor.vue';
import { fallbackRoute } from './segmentRouteMapWork';

const props = defineProps<{
  baselineDraft: EditorSegmentDraft;
  draft: EditorSegmentDraft;
  fieldErrors: (key: string) => string[];
  formId: string;
  formSummaryErrors: string[];
  isDirty: boolean;
  isSaving: boolean;
  state: EditorTripState;
}>();

defineEmits<{
  clearError: [key: string];
  save: [];
}>();

const notesEditor = ref<{ focusEditor: () => void } | null>(null);

/** Delegates Reset focus to the real Quill editing surface. */
function focusNotes(): void {
  notesEditor.value?.focusEditor();
}

defineExpose({ focusNotes });

const normalRegions = computed(() => props.state.regionOrder.map(id => props.state.regionsById[id]).filter(region => region && !region.isShadow) as EditorRegion[]);
// Preserve the current inactive or legacy value as a disabled option; changing away removes it from the selectable list.
const transportModes = computed(() => {
  const active = props.state.options.transportModes;
  if (!props.draft.mode || active.some(mode => mode.value === props.draft.mode)) return active;
  return [{ value: props.draft.mode, label: `${props.draft.mode} (inactive)`, speedKmh: null, inactive: true }, ...active];
});
const routeSummary = computed(() => {
  if (props.draft.route) {
    return `${props.isDirty ? 'Unsaved' : 'Saved'} route · ${props.draft.route.coordinates.length} custom route points`;
  }

  return fallbackRoute(props.draft, props.state)
    ? `Ordered anchor fallback available${props.isDirty ? ' · unsaved' : ''}`
    : 'No route';
});

function orderedPlaceIds(regionId: Guid): Guid[] {
  return props.state.placeOrderByRegionId[regionId] ?? [];
}
</script>

<template>
  <form :id="formId" class="trip-editor-form" @submit.prevent="$emit('save')">
    <div v-if="formSummaryErrors.length > 0" class="trip-editor-form-error" role="alert">
      <p v-for="message in formSummaryErrors" :key="message">{{ message }}</p>
    </div>

    <label class="trip-editor-field">
      <span>From place</span>
      <select v-model="draft.fromPlaceId" data-segment-field="fromPlaceId" @change="$emit('clearError', 'fromPlaceId')">
        <option :value="null">Unlinked</option>
        <optgroup v-for="region in normalRegions" :key="region.id" :label="region.name">
          <option v-for="placeId in orderedPlaceIds(region.id)" :key="placeId" :value="placeId">{{ state.placesById[placeId]?.name }}</option>
        </optgroup>
      </select>
      <small v-for="message in fieldErrors('fromPlaceId')" :key="message">{{ message }}</small>
    </label>

    <SegmentWaypointEditor :baseline-draft="baselineDraft" :draft="draft" :field-errors="fieldErrors" :is-saving="isSaving" :state="state" @clear-error="$emit('clearError', $event)" />

    <label class="trip-editor-field">
      <span>To place</span>
      <select v-model="draft.toPlaceId" data-segment-field="toPlaceId" @change="$emit('clearError', 'toPlaceId')">
        <option :value="null">Unlinked</option>
        <optgroup v-for="region in normalRegions" :key="region.id" :label="region.name">
          <option v-for="placeId in orderedPlaceIds(region.id)" :key="placeId" :value="placeId">{{ state.placesById[placeId]?.name }}</option>
        </optgroup>
      </select>
      <small v-for="message in fieldErrors('toPlaceId')" :key="message">{{ message }}</small>
    </label>

    <label class="trip-editor-field">
      <span>Transport mode</span>
      <select v-model="draft.mode" data-segment-field="mode">
        <option value="">Unset</option>
        <option v-for="mode in transportModes" :key="mode.value" :value="mode.value" :disabled="'inactive' in mode && mode.inactive">{{ mode.label }}</option>
      </select>
      <small v-for="message in fieldErrors('mode')" :key="message">{{ message }}</small>
    </label>

    <div class="trip-editor-field-grid">
      <label class="trip-editor-field">
        <span>Estimated distance km</span>
        <input :value="draft.estimatedDistanceKm" type="number" readonly aria-readonly="true" />
        <small v-for="message in fieldErrors('estimatedDistanceKm')" :key="message">{{ message }}</small>
      </label>

      <div class="trip-editor-field">
        <span>Duration estimate</span>
        <label><input v-model="draft.estimatedDurationSource" data-segment-field="estimatedDurationSource" type="radio" value="Automatic" /> Use automatic estimate</label>
        <label><input v-model="draft.estimatedDurationSource" data-segment-field="estimatedDurationSource" type="radio" value="Manual" /> Enter manually</label>
        <small v-for="message in fieldErrors('estimatedDurationSource')" :key="message">{{ message }}</small>
      </div>

      <label class="trip-editor-field">
        <span>Estimated duration minutes</span>
        <input v-if="draft.estimatedDurationSource === 'Manual'" v-model="draft.estimatedDurationMinutes" data-segment-field="estimatedDurationMinutes" type="number" min="0" step="any" />
        <input v-else :value="draft.estimatedDurationMinutes" type="number" disabled readonly aria-readonly="true" :placeholder="draft.estimatedDurationMinutes === '' ? 'Unavailable until route and speed are available' : undefined" />
        <small v-for="message in fieldErrors('estimatedDurationMinutes')" :key="message">{{ message }}</small>
      </label>
    </div>

    <div data-segment-field="notesHtml" tabindex="-1">
      <RichNotesEditor ref="notesEditor" :editor-id="`${formId}-notes`" v-model="draft.notesHtml" label="Notes" :validation-messages="fieldErrors('notesHtml')" />
    </div>

    <div class="trip-editor-field" data-segment-field="route" tabindex="-1">
      <span>Route</span>
      <p class="trip-editor-empty-state">{{ routeSummary }}</p>
      <small v-for="message in [...fieldErrors('route'), ...fieldErrors('route.coordinates')]" :key="message">{{ message }}</small>
    </div>
  </form>
</template>
