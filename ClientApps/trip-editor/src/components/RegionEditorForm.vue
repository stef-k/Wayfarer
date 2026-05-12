<script setup lang="ts">
import type { EditorRegion } from '../types';
import RichNotesEditor from './RichNotesEditor.vue';
import type { RegionDraft } from './regionPlaceDrafts';

const props = defineProps<{
  activeRegion: EditorRegion | null;
  draft: RegionDraft;
  fieldErrors: (key: string) => string[];
  formId: string;
  formSummaryErrors: string[];
  isSaving: boolean;
}>();

const emit = defineEmits<{
  save: [];
}>();
</script>

<template>
  <form :id="props.formId" class="trip-editor-region-form" @submit.prevent="emit('save')">
    <div v-if="props.formSummaryErrors.length > 0" class="trip-editor-form-error" role="alert">
      <p v-for="message in props.formSummaryErrors" :key="message">{{ message }}</p>
    </div>

    <label class="trip-editor-field">
      <span>Name</span>
      <input v-model="props.draft.name" type="text" autocomplete="off" />
      <small v-for="message in props.fieldErrors('name')" :key="message">{{ message }}</small>
    </label>

    <RichNotesEditor :editor-id="`${props.formId}-notes`" v-model="props.draft.notesHtml" label="Notes" :validation-messages="props.fieldErrors('notesHtml')" />

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
  </form>
</template>
