<script setup lang="ts">
import type { EditorPlace, EditorPlaceDraft, EditorRegion, EditorTripState } from '../types';

const props = defineProps<{
  activePlace: EditorPlace | null;
  draft: EditorPlaceDraft;
  fieldErrors: (key: string) => string[];
  formSummaryErrors: string[];
  isSaving: boolean;
  normalRegions: EditorRegion[];
  placeDirty: boolean;
  state: EditorTripState;
  statusText: string;
}>();

const emit = defineEmits<{
  cancel: [];
  delete: [];
  reset: [];
  save: [];
}>();
</script>

<template>
  <form class="trip-editor-region-form" @submit.prevent="emit('save')">
    <div class="trip-editor-panel__line">
      <h3>{{ props.draft.id ? 'Edit Place' : 'Add Place' }}</h3>
      <span class="trip-editor-save-state">{{ props.statusText }}</span>
    </div>

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
      <label class="trip-editor-field">
        <span>Marker Color</span>
        <select v-model="props.draft.markerColor">
          <option v-for="color in props.state.options.markerColorClasses" :key="color" :value="color">{{ color }}</option>
        </select>
        <small v-for="message in props.fieldErrors('markerColor')" :key="message">{{ message }}</small>
      </label>
    </div>

    <label class="trip-editor-check">
      <input v-model="props.draft.reverseGeocode" type="checkbox" />
      <span>Reverse geocode this location on save</span>
    </label>
    <small v-for="message in props.fieldErrors('reverseGeocode')" :key="message">{{ message }}</small>

    <div class="trip-editor-actions">
      <button type="submit" class="btn btn-primary btn-sm" :disabled="props.isSaving">Save Place</button>
      <button type="button" class="btn btn-outline-secondary btn-sm" :disabled="props.isSaving || !props.placeDirty" @click="emit('reset')">Reset</button>
      <button type="button" class="btn btn-outline-light btn-sm" :disabled="props.isSaving" @click="emit('cancel')">Cancel</button>
      <button v-if="props.activePlace?.capabilities.canDelete" type="button" class="btn btn-outline-danger btn-sm" :disabled="props.isSaving" @click="emit('delete')">
        Delete
      </button>
    </div>
  </form>
</template>
