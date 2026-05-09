<script setup lang="ts">
import type { EditorRegion } from '../types';
import type { RegionDraft } from './regionPlaceDrafts';

const props = defineProps<{
  activeRegion: EditorRegion | null;
  draft: RegionDraft;
  fieldErrors: (key: string) => string[];
  formSummaryErrors: string[];
  isDirty: boolean;
  isSaving: boolean;
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
      <h3>{{ props.draft.id ? 'Edit Region' : 'Add Region' }}</h3>
      <span class="trip-editor-save-state">{{ props.statusText }}</span>
    </div>

    <div v-if="props.formSummaryErrors.length > 0" class="trip-editor-form-error" role="alert">
      <p v-for="message in props.formSummaryErrors" :key="message">{{ message }}</p>
    </div>

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
      <span>Cover Image URL</span>
      <input v-model="props.draft.coverImageRawUrl" type="url" autocomplete="off" />
      <small v-for="message in props.fieldErrors('coverImage.rawUrl')" :key="message">{{ message }}</small>
    </label>

    <div class="trip-editor-grid">
      <label class="trip-editor-field">
        <span>Center Latitude</span>
        <input v-model="props.draft.centerLatitude" type="number" step="any" />
        <small v-for="message in props.fieldErrors('center.latitude')" :key="message">{{ message }}</small>
      </label>
      <label class="trip-editor-field">
        <span>Center Longitude</span>
        <input v-model="props.draft.centerLongitude" type="number" step="any" />
        <small v-for="message in props.fieldErrors('center.longitude')" :key="message">{{ message }}</small>
      </label>
    </div>

    <div class="trip-editor-actions">
      <button type="submit" class="btn btn-primary btn-sm" :disabled="props.isSaving">Save Region</button>
      <button type="button" class="btn btn-outline-secondary btn-sm" :disabled="props.isSaving || !props.isDirty" @click="emit('reset')">Reset</button>
      <button type="button" class="btn btn-outline-light btn-sm" :disabled="props.isSaving" @click="emit('cancel')">Cancel</button>
      <button v-if="props.activeRegion?.capabilities.canDelete" type="button" class="btn btn-outline-danger btn-sm" :disabled="props.isSaving" @click="emit('delete')">
        Delete
      </button>
    </div>
  </form>
</template>
