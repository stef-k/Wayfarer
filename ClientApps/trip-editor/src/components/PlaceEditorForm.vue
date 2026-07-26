<script setup lang="ts">
import { computed } from 'vue';
import type { EditorPlace, EditorPlaceDraft, EditorRegion, EditorTripState } from '../types';
import IconSelector from './IconSelector.vue';
import RichNotesEditor from './RichNotesEditor.vue';

const props = defineProps<{
  activePlace: EditorPlace | null;
  coordinateReadOnly: boolean;
  draft: EditorPlaceDraft;
  fieldErrors: (key: string) => string[];
  formId: string;
  formSummaryErrors: string[];
  isSaving: boolean;
  normalRegions: EditorRegion[];
  state: EditorTripState;
}>();

const emit = defineEmits<{
  save: [];
}>();

const orderedMarkerColors = ['bg-blue', 'bg-purple', 'bg-black', 'bg-green', 'bg-red'];
const markerColorOptions = computed(() => orderedMarkerColors.filter(color => props.state.options.markerColorClasses.includes(color)));

</script>

<template>
  <form :id="props.formId" class="trip-editor-region-form" @submit.prevent="emit('save')">
    <div v-if="props.formSummaryErrors.length > 0" class="trip-editor-form-error" role="alert">
      <p v-for="message in props.formSummaryErrors" :key="message">{{ message }}</p>
    </div>

    <label class="trip-editor-field">
      <span>Region</span>
      <select v-model="props.draft.regionId">
        <option v-for="region in props.normalRegions" :key="region.id" :value="region.id">{{ region.name }}</option>
      </select>
      <small v-for="message in props.fieldErrors('regionId')" :key="message">{{ message }}</small>
    </label>

    <label class="trip-editor-field">
      <span>Name</span>
      <input v-model="props.draft.name" type="text" autocomplete="off" />
      <small v-for="message in props.fieldErrors('name')" :key="message">{{ message }}</small>
    </label>

    <RichNotesEditor :editor-id="`${props.formId}-notes`" v-model="props.draft.notesHtml" label="Notes" :validation-messages="props.fieldErrors('notesHtml')" />

    <label class="trip-editor-field">
      <span>Address</span>
      <input v-model="props.draft.address" type="text" autocomplete="off" />
      <small v-for="message in props.fieldErrors('address')" :key="message">{{ message }}</small>
    </label>

    <div class="trip-editor-grid">
      <label class="trip-editor-field">
        <span>Latitude</span>
        <input v-model="props.draft.latitude" type="number" step="any" :readonly="props.coordinateReadOnly" />
        <small v-for="message in props.fieldErrors('location.latitude')" :key="message">{{ message }}</small>
      </label>
      <label class="trip-editor-field">
        <span>Longitude</span>
        <input v-model="props.draft.longitude" type="number" step="any" :readonly="props.coordinateReadOnly" />
        <small v-for="message in props.fieldErrors('location.longitude')" :key="message">{{ message }}</small>
      </label>
    </div>

    <div class="trip-editor-selector-grid">
      <fieldset class="trip-editor-field trip-editor-place-icon-field">
        <legend>Icon</legend>
        <IconSelector v-model="props.draft.iconName" :icons="props.state.options.iconNames" :marker-color="props.draft.markerColor || 'bg-blue'" />
        <small v-for="message in props.fieldErrors('iconName')" :key="message">{{ message }}</small>
      </fieldset>
      <fieldset class="trip-editor-field trip-editor-marker-color-field">
        <legend>Marker Color</legend>
        <IconSelector v-model="props.draft.markerColor" kind="color" :icons="markerColorOptions" marker-color="bg-blue" />
        <small v-for="message in props.fieldErrors('markerColor')" :key="message">{{ message }}</small>
      </fieldset>
    </div>

    <label class="trip-editor-check">
      <input v-model="props.draft.reverseGeocode" type="checkbox" />
      <span>Reverse geocode this location on save</span>
    </label>
    <small v-for="message in props.fieldErrors('reverseGeocode')" :key="message">{{ message }}</small>
  </form>
</template>
