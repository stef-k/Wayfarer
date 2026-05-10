<script setup lang="ts">
import type { EditorPlace, EditorPlaceDraft, EditorRegion, EditorTripState } from '../types';

const props = defineProps<{
  activePlace: EditorPlace | null;
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

const markerColorLabel = (color: string): string => {
  const name = color.replace(/^bg-/, '').replace(/[-_]+/g, ' ');
  return `${name.charAt(0).toUpperCase()}${name.slice(1)} marker color`;
};
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

    <label class="trip-editor-field">
      <span>Notes HTML</span>
      <textarea v-model="props.draft.notesHtml" rows="6"></textarea>
      <small v-for="message in props.fieldErrors('notesHtml')" :key="message">{{ message }}</small>
    </label>

    <label class="trip-editor-field">
      <span>Address</span>
      <input v-model="props.draft.address" type="text" autocomplete="off" />
      <small v-for="message in props.fieldErrors('address')" :key="message">{{ message }}</small>
    </label>

    <div class="trip-editor-grid">
      <label class="trip-editor-field">
        <span>Latitude</span>
        <input v-model="props.draft.latitude" type="number" step="any" />
        <small v-for="message in props.fieldErrors('location.latitude')" :key="message">{{ message }}</small>
      </label>
      <label class="trip-editor-field">
        <span>Longitude</span>
        <input v-model="props.draft.longitude" type="number" step="any" />
        <small v-for="message in props.fieldErrors('location.longitude')" :key="message">{{ message }}</small>
      </label>
    </div>

    <div class="trip-editor-grid">
      <label class="trip-editor-field">
        <span>Icon</span>
        <select v-model="props.draft.iconName">
          <option v-for="icon in props.state.options.iconNames" :key="icon" :value="icon">{{ icon }}</option>
        </select>
        <small v-for="message in props.fieldErrors('iconName')" :key="message">{{ message }}</small>
      </label>
      <fieldset class="trip-editor-field trip-editor-marker-color-field">
        <legend>Marker Color</legend>
        <div class="trip-editor-marker-swatch-group" role="radiogroup" aria-label="Marker Color">
          <label
            v-for="color in props.state.options.markerColorClasses"
            :key="color"
            class="trip-editor-marker-swatch"
            :class="{ 'trip-editor-marker-swatch--selected': props.draft.markerColor === color }"
            :title="markerColorLabel(color)"
          >
            <input v-model="props.draft.markerColor" class="trip-editor-marker-swatch__input" type="radio" name="markerColor" :value="color" :aria-label="markerColorLabel(color)" />
            <span class="trip-editor-marker-swatch__sample" :class="color" aria-hidden="true"></span>
          </label>
        </div>
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
