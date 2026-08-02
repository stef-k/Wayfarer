<script setup lang="ts">
import { computed } from 'vue';
import type { EditorRegion, EditorSegmentDraft, EditorTripState, Guid } from '../types';
import RichNotesEditor from './RichNotesEditor.vue';
import { fallbackRoute } from './segmentRouteMapWork';

const props = defineProps<{
  draft: EditorSegmentDraft;
  fieldErrors: (key: string) => string[];
  formId: string;
  formSummaryErrors: string[];
  isDirty: boolean;
  state: EditorTripState;
}>();

defineEmits<{
  save: [];
}>();

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
    ? `Endpoint fallback available until saved${props.isDirty ? ' · unsaved' : ''}`
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
      <select v-model="draft.fromPlaceId">
        <option :value="null">Unlinked</option>
        <optgroup v-for="region in normalRegions" :key="region.id" :label="region.name">
          <option v-for="placeId in orderedPlaceIds(region.id)" :key="placeId" :value="placeId">{{ state.placesById[placeId]?.name }}</option>
        </optgroup>
      </select>
      <small v-for="message in fieldErrors('fromPlaceId')" :key="message">{{ message }}</small>
    </label>

    <label class="trip-editor-field">
      <span>To place</span>
      <select v-model="draft.toPlaceId">
        <option :value="null">Unlinked</option>
        <optgroup v-for="region in normalRegions" :key="region.id" :label="region.name">
          <option v-for="placeId in orderedPlaceIds(region.id)" :key="placeId" :value="placeId">{{ state.placesById[placeId]?.name }}</option>
        </optgroup>
      </select>
      <small v-for="message in fieldErrors('toPlaceId')" :key="message">{{ message }}</small>
    </label>

    <label class="trip-editor-field">
      <span>Transport mode</span>
      <select v-model="draft.mode">
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
        <label><input v-model="draft.estimatedDurationSource" type="radio" value="Automatic" /> Use automatic estimate</label>
        <label><input v-model="draft.estimatedDurationSource" type="radio" value="Manual" /> Enter manually</label>
        <small v-for="message in fieldErrors('estimatedDurationSource')" :key="message">{{ message }}</small>
      </div>

      <label v-if="draft.estimatedDurationSource === 'Manual'" class="trip-editor-field">
        <span>Estimated duration minutes</span>
        <input v-model="draft.estimatedDurationMinutes" type="number" min="0" step="any" />
        <small v-for="message in fieldErrors('estimatedDurationMinutes')" :key="message">{{ message }}</small>
      </label>
    </div>

    <RichNotesEditor :editor-id="`${formId}-notes`" v-model="draft.notesHtml" label="Notes" :validation-messages="fieldErrors('notesHtml')" />

    <div class="trip-editor-field">
      <span>Route</span>
      <p class="trip-editor-empty-state">{{ routeSummary }}</p>
      <small v-for="message in [...fieldErrors('route'), ...fieldErrors('route.coordinates')]" :key="message">{{ message }}</small>
    </div>
  </form>
</template>
